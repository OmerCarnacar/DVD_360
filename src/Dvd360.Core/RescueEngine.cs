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

    /// <summary>Runs the fast passes and the sector-by-sector pass. Safe to cancel and call again.</summary>
    public Task RunAsync(CancellationToken ct) => Task.Run(() => Run(retryBad: false, ct), ct);

    /// <summary>Gives sectors that are still bad another <see cref="RescueOptions.Retries"/> tries.</summary>
    public Task RetryBadAsync(CancellationToken ct) => Task.Run(() => Run(retryBad: true, ct), ct);

    private void Run(bool retryBad, CancellationToken ct)
    {
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
        if (Map.CountOf(SectorState.Untried) == 0) return;
        Stats.Pass = RescuePass.FastCopy;
        Emit(CoreText.L("1. geçiş: kolay okunan her yer hızlıca kopyalanıyor", "Pass 1: fast copy of everything that reads easily"));
        int block = _device.MaxSectorsPerRead;
        long lba = 0;
        int failures = 0, streak = 0;
        uint normalTimeout = _device.ReadTimeoutSeconds;
        _device.ReadTimeoutSeconds = Math.Min(normalTimeout, 5); // don't let the drive grind on scratches now
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                lba = Map.FindNext(lba, SectorState.Untried);
                if (lba < 0) break;
                int n = Map.RunLength(lba, SectorState.Untried, block);
                long started = Stopwatch.GetTimestamp();
                ReadBlock(lba, n, out bool readError);
                // A block that takes ages means the drive is fighting a scratch: keep what it gave us,
                // but treat the area as damaged and move on (like ddrescue's minimum read rate).
                if (Stopwatch.GetElapsedTime(started).TotalSeconds > _options.SlowReadSeconds) readError = true;
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
        finally
        {
            _device.ReadTimeoutSeconds = normalTimeout;
        }
    }

    /// <summary>Forward over the areas pass 1 jumped over (optical drives read much faster forwards).</summary>
    private void Gaps(CancellationToken ct, Action tick)
    {
        if (Map.CountOf(SectorState.Untried) == 0) return;
        Stats.Pass = RescuePass.Gaps;
        Emit(CoreText.L("2. geçiş: atlanan bölgeler okunuyor", "Pass 2: reading the areas that were jumped over"));
        int block = _device.MaxSectorsPerRead;
        long lba = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            lba = Map.FindNext(lba, SectorState.Untried);
            if (lba < 0) break;
            int n = Map.RunLength(lba, SectorState.Untried, block);
            ReadBlock(lba, n, out _);
            lba += n;
            tick();
        }
    }

    private void SingleSectors(SectorState target, RescuePass pass, CancellationToken ct, Action tick)
    {
        long total = Map.CountOf(target);
        if (total == 0) return;
        Stats.Pass = pass;
        Emit(pass == RescuePass.RetryBad
            ? CoreText.L($"{total:N0} bozuk sektör yeniden deneniyor, her biri {_options.Retries} kez", $"Retrying {total:N0} bad sectors, {_options.Retries} tries each")
            : CoreText.L($"3. geçiş: sektör sektör, {total:N0} sektör, her biri en çok {_options.Retries} kez", $"Pass 3: sector by sector, {total:N0} sectors, up to {_options.Retries} tries each"));
        long lba = 0, done = 0, rescued = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            lba = Map.FindNext(lba, target);
            if (lba < 0) break;
            for (int attempt = 0; attempt < Math.Max(1, _options.Retries); attempt++)
            {
                ct.ThrowIfCancellationRequested();
                ReadBlock(lba, 1, out _, failState: target);
                if (Map[lba] != target) break;
                tick();
            }
            if (Map[lba] == SectorState.Ok) rescued++;
            if (Map[lba] == target) Map.Set(lba, SectorState.Bad);
            done++;
            if (done % 100 == 0) Emit(CoreText.L($"  {done:N0}/{total:N0} denendi, {rescued:N0} kurtarıldı", $"  {done:N0}/{total:N0} tried, {rescued:N0} rescued"));
            lba++;
            tick();
        }
        Emit(CoreText.L($"  Bu geçişte {total:N0} sektörden {rescued:N0} tanesi kurtarıldı", $"  {rescued:N0} of {total:N0} sectors rescued in this pass"));
    }

    /// <summary>Reads, verifies and stores a run of sectors. Returns true if all of them are now good.</summary>
    private bool ReadBlock(long lba, int count, out bool readError, SectorState failState = SectorState.NeedsRetry)
    {
        readError = false;
        Stats.CurrentLba = lba;
        int size = Info.SectorSize;
        var data = _buffer.AsSpan(0, count * size);
        var outcome = _device.Read(lba, count, data);

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
