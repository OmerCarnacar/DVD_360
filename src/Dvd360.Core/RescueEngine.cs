using System.Diagnostics;
using System.Text.Json;

namespace Dvd360.Core;

public sealed class RescueOptions
{
    /// <summary>How many times each unreadable sector is tried on its own.</summary>
    public int Retries { get; set; } = 5;
    /// <summary>Read speed as a multiple of 1x; 0 = drive maximum. Slow speeds often read scratched discs better.</summary>
    public int SpeedMultiplier { get; set; }
    /// <summary>In the fast pass, a block slower than this counts as a damaged area to come back to later.</summary>
    public double SlowReadSeconds { get; set; } = 1.0;
    /// <summary>
    /// Drop to <see cref="SlowSpeedMultiplier"/> when most recent reads fail. On a badly scratched Video CD this
    /// read ten times more sectors per minute than full speed.
    /// </summary>
    public bool AutoSlowDown { get; set; } = true;
    public int SlowSpeedMultiplier { get; set; } = 4;
    /// <summary>
    /// When a CD sector keeps arriving with a few wrong bytes (Video CD sectors have no error correction), take a
    /// byte-by-byte majority vote over the copies; the result is accepted only if its checksum is right.
    /// </summary>
    public bool VoteOnCopies { get; set; } = true;
    /// <summary>Before each retry read a sector far away, so the drive cannot hand back the same cached bad copy.</summary>
    public bool BypassDriveCache { get; set; } = true;
    /// <summary>Check header address and EDC of every CD data sector ourselves.</summary>
    public bool VerifyCdSectors { get; set; } = true;
}

public enum RescuePass
{
    Idle,
    /// <summary>Forward, large blocks, jumps over damaged areas.</summary>
    FastCopy,
    /// <summary>Forward over the areas that were jumped over.</summary>
    Gaps,
    /// <summary>Each failed sector on its own, several times.</summary>
    SectorBySector,
    /// <summary>Another round on sectors that are still bad (after cleaning the disc, another drive...).</summary>
    RetryBad,
    Done,
}

/// <summary>Live numbers for the UI; read from any thread.</summary>
public sealed class RescueStats
{
    public RescuePass Pass { get; internal set; }
    public long CurrentLba { get; internal set; }
    public double BytesPerSecond { get; internal set; }
    public TimeSpan Elapsed { get; internal set; }
}

/// <summary>
/// Sector-by-sector rescue in the spirit of GNU ddrescue: get the easy data first, then come back
/// to the hard parts, and keep a map so nothing that was already read is ever read again.
/// </summary>
public sealed class RescueEngine
{
    private readonly ISectorDevice _device;
    private readonly RescueOptions _options;
    private readonly byte[] _buffer;
    private FileStream? _image;
    private DateTime _lastSave;
    private long _okBytesThisRun;
    private readonly Queue<bool> _recentErrors = new();
    private ReadOutcome _lastOutcome;
    private byte[]? _scratch;
    private bool _slowedDown;
    /// <summary>Sorted, merged [Start, End) ranges this run works on (whole disc by default).</summary>
    private List<(long Start, long End)> _scope = new();

    public RescueEngine(ISectorDevice device, string imagePath, RescueOptions? options = null)
    {
        _device = device;
        _options = options ?? new RescueOptions();
        ImagePath = imagePath;
        Info = device.Info;
        _buffer = new byte[device.MaxSectorsPerRead * Info.SectorSize];
        Map = LoadOrCreateMap();
    }

    public DiscInfo Info { get; }
    public string ImagePath { get; }
    public string MapPath => ImagePath + ".map";
    public string InfoPath => ImagePath + ".json";
    public SectorMap Map { get; }
    public RescueStats Stats { get; } = new();
    public bool IsResume { get; private set; }
    /// <summary>True once the engine lowered the read speed by itself.</summary>
    public bool SlowedDown => _slowedDown;

    public event Action<string>? Log;

    public long OkSectors => Map.CountOf(SectorState.Ok);
    public long BadSectors => Map.CountOf(SectorState.Bad);
    public long PendingSectors => Map.CountOf(SectorState.Untried) + Map.CountOf(SectorState.NeedsRetry);
    public long SkippedSectors => Map.CountOf(SectorState.Skipped);
    public bool IsComplete => PendingSectors == 0;

    private SectorMap LoadOrCreateMap()
    {
        if (File.Exists(MapPath) && File.Exists(ImagePath))
        {
            var m = SectorMap.Load(MapPath);
            if (m.Count != Info.TotalSectors || m.SectorSize != Info.SectorSize)
                throw new InvalidOperationException(CoreText.L("Bu yerdeki mevcut kalıp başka bir diske ait. Başka bir ad veya klasör seçin.", "The existing image in this place belongs to a different disc. Choose another name or folder."));
            IsResume = true;
            return m;
        }
        return new SectorMap(Info.TotalSectors, Info.SectorSize);
    }

    /// <summary>
    /// Runs the fast passes and the sector-by-sector pass. Safe to cancel and call again.
    /// With <paramref name="ranges"/>, only those sectors are read (e.g. just the files the user picked).
    /// </summary>
    public Task RunAsync(CancellationToken ct, IEnumerable<(long Start, long Count)>? ranges = null) =>
        Task.Run(() => Run(retryBad: false, ranges, ct), ct);

    /// <summary>Gives sectors that are still bad another <see cref="RescueOptions.Retries"/> tries.</summary>
    public Task RetryBadAsync(CancellationToken ct, IEnumerable<(long Start, long Count)>? ranges = null) =>
        Task.Run(() => Run(retryBad: true, ranges, ct), ct);

    /// <summary>Sectors still to read inside the given ranges (or the whole disc).</summary>
    public long PendingIn(IEnumerable<(long Start, long Count)>? ranges)
    {
        if (ranges == null) return PendingSectors;
        long n = 0;
        foreach (var (s, e) in Merge(ranges, Map.Count)) { var t = Map.Summarize(s, e - s); n += t.Pending + t.Retry; }
        return n;
    }

    internal static List<(long Start, long End)> Merge(IEnumerable<(long Start, long Count)> ranges, long total)
    {
        var list = ranges.Where(r => r.Count > 0)
            .Select(r => (Start: Math.Clamp(r.Start, 0, total), End: Math.Clamp(r.Start + r.Count, 0, total)))
            .Where(r => r.End > r.Start).OrderBy(r => r.Start).ToList();
        var merged = new List<(long Start, long End)>();
        foreach (var r in list)
        {
            if (merged.Count > 0 && r.Start <= merged[^1].End) merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, r.End));
            else merged.Add(r);
        }
        return merged;
    }

    private long CountInScope(SectorState state)
    {
        long n = 0;
        foreach (var (s, e) in _scope)
            for (long i = s; i < e; i++) if (Map[i] == state) n++;
        return n;
    }

    private void Run(bool retryBad, IEnumerable<(long Start, long Count)>? ranges, CancellationToken ct)
    {
        _scope = ranges == null ? [(0, Map.Count)] : Merge(ranges, Map.Count);
        var clock = Stopwatch.StartNew();
        _okBytesThisRun = 0;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(ImagePath))!);
        File.WriteAllText(InfoPath, JsonSerializer.Serialize(Info, new JsonSerializerOptions { WriteIndented = true }));

        using var image = new FileStream(ImagePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 1 << 16);
        _image = image;
        if (image.Length != Info.TotalBytes) image.SetLength(Info.TotalBytes);

        if (_options.SpeedMultiplier >= 0 && _device.SetReadSpeed(_options.SpeedMultiplier))
            Emit(_options.SpeedMultiplier == 0 ? CoreText.L("Okuma hızı: en yüksek", "Read speed: maximum") : CoreText.L($"Okuma hızı: {_options.SpeedMultiplier}x", $"Read speed: {_options.SpeedMultiplier}x"));

        var speedTimer = Stopwatch.StartNew();
        long speedMark = 0;
        void Tick()
        {
            Stats.Elapsed = clock.Elapsed;
            if (speedTimer.ElapsedMilliseconds >= 1000)
            {
                Stats.BytesPerSecond = (_okBytesThisRun - speedMark) / speedTimer.Elapsed.TotalSeconds;
                speedMark = _okBytesThisRun;
                speedTimer.Restart();
            }
            if ((DateTime.UtcNow - _lastSave).TotalSeconds >= 3) SaveMap();
        }

        try
        {
            if (retryBad)
            {
                SingleSectors(SectorState.Bad, RescuePass.RetryBad, ct, Tick);
            }
            FastCopy(ct, Tick);
            Gaps(ct, Tick);
            SingleSectors(SectorState.NeedsRetry, RescuePass.SectorBySector, ct, Tick);
            Stats.Pass = RescuePass.Done;
            WriteCompanionFiles();
            Emit(CoreText.L($"Bitti. Sağlam: {OkSectors:N0}, bozuk: {BadSectors:N0}, diskte olmayan: {SkippedSectors:N0} sektör.", $"Finished. Good: {OkSectors:N0}, bad: {BadSectors:N0}, not on disc: {SkippedSectors:N0} sectors."));
        }
        finally
        {
            Stats.Elapsed = clock.Elapsed;
            Stats.BytesPerSecond = 0;
            SaveMap();
            _image = null;
            if (Stats.Pass != RescuePass.Done) Stats.Pass = RescuePass.Idle;
        }
    }

    private void FastCopy(CancellationToken ct, Action tick)
    {
        if (CountInScope(SectorState.Untried) == 0) return;
        Stats.Pass = RescuePass.FastCopy;
        Emit(CoreText.L("1. geçiş: kolay okunan her yer hızlıca kopyalanıyor", "Pass 1: fast copy of everything that reads easily"));
        int block = _device.MaxSectorsPerRead;
        int failures = 0, streak = 0;
        uint normalTimeout = _device.ReadTimeoutSeconds;
        _device.ReadTimeoutSeconds = Math.Min(normalTimeout, 5); // don't let the drive grind on scratches now
        try
        {
          foreach (var (rangeStart, rangeEnd) in _scope)
          {
            long lba = rangeStart;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                lba = Map.FindNext(lba, SectorState.Untried);
                if (lba < 0 || lba >= rangeEnd) break;
                int n = (int)Math.Min(Map.RunLength(lba, SectorState.Untried, block), rangeEnd - lba);
                long started = Stopwatch.GetTimestamp();
                ReadBlock(lba, n, out bool readError);
                // A block that takes ages means the drive is fighting a scratch: keep what it gave us,
                // but treat the area as damaged and move on (like ddrescue's minimum read rate).
                if (Stopwatch.GetElapsedTime(started).TotalSeconds > _options.SlowReadSeconds) readError = true;
                NoteResult(readError);
                lba += n;
                // Only real read errors mean a damaged area worth jumping over. A sector that reads but fails
                // our EDC check (common on Video CDs, whose Form 2 sectors have no error correction) does not.
                if (!readError)
                {
                    if (++streak >= 8) failures = 0;
                }
                else
                {
                    // Damaged area: jump ahead, further while errors keep coming; pass 2 comes back for it.
                    streak = 0;
                    failures++;
                    lba += Math.Min((long)block << Math.Min(failures - 1, 8), 8192);
                }
                tick();
            }
          }
        }
        finally
        {
            _device.ReadTimeoutSeconds = normalTimeout;
        }
    }

    /// <summary>Forward over the areas pass 1 jumped over (optical drives read much faster forwards).</summary>
    private void Gaps(CancellationToken ct, Action tick)
    {
        if (CountInScope(SectorState.Untried) == 0) return;
        Stats.Pass = RescuePass.Gaps;
        Emit(CoreText.L("2. geçiş: atlanan bölgeler okunuyor", "Pass 2: reading the areas that were jumped over"));
        int block = _device.MaxSectorsPerRead;
        foreach (var (rangeStart, rangeEnd) in _scope)
        {
            long lba = rangeStart;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                lba = Map.FindNext(lba, SectorState.Untried);
                if (lba < 0 || lba >= rangeEnd) break;
                int n = (int)Math.Min(Map.RunLength(lba, SectorState.Untried, block), rangeEnd - lba);
                ReadBlock(lba, n, out bool gapError);
                NoteResult(gapError);
                lba += n;
                tick();
            }
        }
    }

    private void SingleSectors(SectorState target, RescuePass pass, CancellationToken ct, Action tick)
    {
        long total = CountInScope(target);
        if (total == 0) return;
        Stats.Pass = pass;
        Emit(pass == RescuePass.RetryBad
            ? CoreText.L($"{total:N0} bozuk sektör yeniden deneniyor, her biri {_options.Retries} kez", $"Retrying {total:N0} bad sectors, {_options.Retries} tries each")
            : CoreText.L($"3. geçiş: sektör sektör, {total:N0} sektör, her biri en çok {_options.Retries} kez", $"Pass 3: sector by sector, {total:N0} sectors, up to {_options.Retries} tries each"));
        long done = 0, rescued = 0, voted = 0;
        var copies = new List<byte[]>();
        foreach (var (rangeStart, rangeEnd) in _scope)
        for (long lba = rangeStart; ; lba++)
        {
            ct.ThrowIfCancellationRequested();
            lba = Map.FindNext(lba, target);
            if (lba < 0 || lba >= rangeEnd) break;
            for (int attempt = 0; attempt < Math.Max(1, _options.Retries); attempt++)
            {
                ct.ThrowIfCancellationRequested();
                if (attempt > 0) BypassCache(lba);
                ReadBlock(lba, 1, out _, failState: target);
                if (Map[lba] != target) break;
                // the drive returned data, but our checksum says it is wrong: keep the copy for voting
                if (_lastOutcome == ReadOutcome.Ok && copies.Count < 16) copies.Add(_buffer.AsSpan(0, Info.SectorSize).ToArray());
                tick();
            }
            if (Map[lba] == target && copies.Count >= 3 && TryVote(lba, copies)) voted++;
            copies.Clear();
            if (Map[lba] == SectorState.Ok) rescued++;
            if (Map[lba] == target) Map.Set(lba, SectorState.Bad);
            done++;
            if (done % 100 == 0) Emit(CoreText.L($"  {done:N0}/{total:N0} denendi, {rescued:N0} kurtarıldı", $"  {done:N0}/{total:N0} tried, {rescued:N0} rescued"));
            tick();
        }
        Emit(CoreText.L($"  Bu geçişte {total:N0} sektörden {rescued:N0} tanesi kurtarıldı", $"  {rescued:N0} of {total:N0} sectors rescued in this pass"));
        if (voted > 0) Emit(CoreText.L($"  {voted:N0} sektör çoklu okuma oylamasıyla onarıldı", $"  {voted:N0} sectors repaired by voting over several reads"));
    }

    /// <summary>Reads one sector far from <paramref name="lba"/> so the next read really goes to the disc.</summary>
    private void BypassCache(long lba)
    {
        if (!_options.BypassDriveCache || Map.Count < 2) return;
        long far = (lba + Map.Count / 2) % Map.Count;
        _scratch ??= new byte[Info.SectorSize];
        _device.Read(far, 1, _scratch); // result does not matter
    }

    /// <summary>
    /// Byte-by-byte majority over several imperfect copies of one sector. Different reads usually get different bytes
    /// wrong, so the majority is often the true sector. Accepted only if the CD checksum (EDC) then matches.
    /// </summary>
    private bool TryVote(long lba, List<byte[]> copies)
    {
        if (!_options.VoteOnCopies || !Info.IsCd || !_options.VerifyCdSectors) return false;
        var voted = MajorityVote(copies);
        bool good = CdSector.Verify(voted, lba);
        // even if it does not verify, the voted copy is the best guess we have (fewer wrong bytes in a video)
        _image!.Position = lba * Info.SectorSize;
        _image.Write(voted);
        if (!good) return false;
        Map.Set(lba, SectorState.Ok);
        _okBytesThisRun += Info.SectorSize;
        return true;
    }

    internal static byte[] MajorityVote(IReadOnlyList<byte[]> copies)
    {
        int n = copies[0].Length;
        var result = new byte[n];
        Span<int> counts = stackalloc int[256];
        for (int i = 0; i < n; i++)
        {
            counts.Clear();
            byte best = copies[0][i];
            int bestCount = 0;
            foreach (var c in copies)
            {
                int k = ++counts[c[i]];
                if (k > bestCount) { bestCount = k; best = c[i]; }
            }
            result[i] = best;
        }
        return result;
    }

    /// <summary>Watches the last 20 block reads and slows the drive down when most of them fail.</summary>
    private void NoteResult(bool error)
    {
        _recentErrors.Enqueue(error);
        if (_recentErrors.Count > 20) _recentErrors.Dequeue();
        if (!_options.AutoSlowDown || _slowedDown || _recentErrors.Count < 20) return;
        if (_options.SpeedMultiplier is > 0 and <= 4) return; // already slow
        if (_recentErrors.Count(e => e) < 10) return;
        if (_device.SetReadSpeed(_options.SlowSpeedMultiplier))
        {
            _slowedDown = true;
            Emit(CoreText.L($"Çok hata var: okuma hızı {_options.SlowSpeedMultiplier}x'e düşürüldü (çizikli disk yavaşta daha iyi okunur).",
                            $"Many errors: read speed lowered to {_options.SlowSpeedMultiplier}x (scratched discs read better slowly)."));
        }
    }

    /// <summary>Reads, verifies and stores a run of sectors. Returns true if all of them are now good.</summary>
    private bool ReadBlock(long lba, int count, out bool readError, SectorState failState = SectorState.NeedsRetry)
    {
        readError = false;
        Stats.CurrentLba = lba;
        int size = Info.SectorSize;
        var data = _buffer.AsSpan(0, count * size);
        var outcome = _device.Read(lba, count, data);
        _lastOutcome = outcome;

        if (outcome == ReadOutcome.NoMedia)
        {
            SaveMap();
            throw new NoMediaException(CoreText.L("Disk çıkarıldı veya sürücü yanıt vermiyor. Diski tekrar takıp \"Devam et\"e basın.", "The disc was removed or the drive stopped responding. Insert it again and press Continue."));
        }

        if (outcome == ReadOutcome.Ok)
        {
            _image!.Position = lba * size;
            _image.Write(data);
            bool all = true;
            for (int i = 0; i < count; i++)
            {
                bool good = !Info.IsCd || !_options.VerifyCdSectors || CdSector.Verify(data.Slice(i * size, size), lba + i);
                if (good)
                {
                    Map.Set(lba + i, SectorState.Ok);
                    _okBytesThisRun += size;
                }
                else
                {
                    if (Map[lba + i] != SectorState.Bad) Map.Set(lba + i, failState);
                    all = false;
                }
            }
            return all;
        }

        if (outcome == ReadOutcome.NotPresent && count == 1)
        {
            Map.Set(lba, SectorState.Skipped);
            return true;
        }

        readError = true;
        for (int i = 0; i < count; i++)
            if (Map[lba + i] is SectorState.Untried or SectorState.NeedsRetry) Map.Set(lba + i, failState);
        return false;
    }

    private void SaveMap()
    {
        _image?.Flush(true); // data first, then the map that says it is there
        Map.Save(MapPath);
        _lastSave = DateTime.UtcNow;
    }

    private void WriteCompanionFiles()
    {
        if (!Info.IsCd) return;
        _image!.Flush();
        string cue = Path.ChangeExtension(ImagePath, ".cue");
        CueSheet.Write(cue, Path.GetFileName(ImagePath), Info, lba =>
        {
            if (Map[lba] != SectorState.Ok) return null;
            var raw = new byte[CdSector.RawSize];
            _image.Position = lba * CdSector.RawSize;
            _image.ReadExactly(raw);
            return CdSector.GetKind(raw);
        });
        Emit(CoreText.L($"CUE dosyası yazıldı: {Path.GetFileName(cue)}", $"CUE sheet written: {Path.GetFileName(cue)}"));
    }

    private void Emit(string message) => Log?.Invoke(message);
}
