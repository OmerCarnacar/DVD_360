using Dvd360.Core;
using Dvd360.Core.Iso9660;

namespace Dvd360.Tests;

/// <summary>A pretend DVD: sector i is filled with a pattern derived from i, with configurable damage.</summary>
internal sealed class FakeDevice : ISectorDevice
{
    private readonly Dictionary<long, int> _readsUntilGood;
    public HashSet<long> Dead { get; } = new();
    public HashSet<long> Missing { get; } = new();
    public int ReadCalls { get; private set; }
    public long? EjectAtLba { get; set; }

    public FakeDevice(long sectors, Dictionary<long, int>? flaky = null)
    {
        Info = new DiscInfo { TotalSectors = sectors, SectorSize = 2048, MediaType = "DVD-R", Tracks = [new TrackInfo(1, 0, true)] };
        _readsUntilGood = flaky ?? new();
    }

    public DiscInfo Info { get; }
    public int MaxSectorsPerRead => 16;
    public uint ReadTimeoutSeconds { get; set; } = 20;
    public byte[]? Backing { get; set; }

    public static byte Pattern(long lba, int i) => (byte)(lba * 31 + i);

    /// <summary>Called on every read, e.g. to cancel at an exact point.</summary>
    public Action<int>? OnRead { get; set; }

    public List<long> ReadLbas { get; } = new();

    public ReadOutcome Read(long lba, int count, Span<byte> dest)
    {
        ReadCalls++;
        ReadLbas.Add(lba);
        OnRead?.Invoke(ReadCalls);
        if (EjectAtLba is { } e && lba <= e && e < lba + count) { EjectAtLba = null; return ReadOutcome.NoMedia; }
        for (long s = lba; s < lba + count; s++)
        {
            if (Missing.Contains(s)) return ReadOutcome.NotPresent;
            if (Dead.Contains(s)) return ReadOutcome.MediaError;
            if (_readsUntilGood.TryGetValue(s, out int left) && left > 0)
            {
                if (count == 1) _readsUntilGood[s] = left - 1;
                return ReadOutcome.MediaError;
            }
        }
        for (int k = 0; k < count; k++)
            for (int i = 0; i < 2048; i++)
                dest[k * 2048 + i] = Backing != null ? Backing[(lba + k) * 2048 + i] : Pattern(lba + k, i);
        return ReadOutcome.Ok;
    }

    public List<int> SpeedRequests { get; } = new();
    public bool SetReadSpeed(int multiplier) { SpeedRequests.Add(multiplier); return true; }
    public void Dispose() { }
}

public sealed class RescueEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dvd360-tests-" + Guid.NewGuid().ToString("N"));

    public RescueEngineTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Img => Path.Combine(_dir, "disc.iso");

    private void AssertSectorIntact(byte[] image, long lba)
    {
        for (int i = 0; i < 2048; i++) Assert.Equal(FakeDevice.Pattern(lba, i), image[lba * 2048 + i]);
    }

    [Fact]
    public async Task Clean_disc_is_copied_exactly()
    {
        var dev = new FakeDevice(1000);
        var engine = new RescueEngine(dev, Img);
        await engine.RunAsync(CancellationToken.None);

        Assert.Equal(1000, engine.OkSectors);
        Assert.True(engine.IsComplete);
        var bytes = File.ReadAllBytes(Img);
        Assert.Equal(1000 * 2048, bytes.Length);
        for (long l = 0; l < 1000; l += 97) AssertSectorIntact(bytes, l);
    }

    [Fact]
    public async Task Dead_sectors_are_isolated_and_everything_else_is_saved()
    {
        var dev = new FakeDevice(5000);
        for (long l = 2000; l < 2100; l++) dev.Dead.Add(l);  // a long scratch
        dev.Dead.Add(4321);
        var engine = new RescueEngine(dev, Img, new RescueOptions { Retries = 3 });
        await engine.RunAsync(CancellationToken.None);

        Assert.Equal(101, engine.BadSectors);
        Assert.Equal(5000 - 101, engine.OkSectors);
        Assert.Equal(0, engine.PendingSectors);
        var bytes = File.ReadAllBytes(Img);
        AssertSectorIntact(bytes, 1999);
        AssertSectorIntact(bytes, 2100);
        AssertSectorIntact(bytes, 4320);
        AssertSectorIntact(bytes, 4322);
        Assert.Equal(SectorState.Bad, engine.Map[2050]);
    }

    [Fact]
    public async Task Weak_sectors_are_recovered_by_retrying_one_at_a_time()
    {
        var flaky = new Dictionary<long, int> { [10] = 2, [500] = 4, [501] = 1 };
        var dev = new FakeDevice(1000, flaky);
        var engine = new RescueEngine(dev, Img, new RescueOptions { Retries = 5 });
        await engine.RunAsync(CancellationToken.None);

        Assert.Equal(0, engine.BadSectors);
        Assert.Equal(1000, engine.OkSectors);
        AssertSectorIntact(File.ReadAllBytes(Img), 500);
    }

    [Fact]
    public async Task Retry_bad_gets_sectors_that_need_more_tries()
    {
        var flaky = new Dictionary<long, int> { [300] = 5 }; // 3 tries fail in pass 3, 2 more fail on retry, then it reads
        var dev = new FakeDevice(1000, flaky);
        var engine = new RescueEngine(dev, Img, new RescueOptions { Retries = 3 });
        await engine.RunAsync(CancellationToken.None);
        Assert.Equal(1, engine.BadSectors);

        // e.g. the user cleaned the disc and pressed "retry bad sectors"
        var again = new RescueEngine(dev, Img, new RescueOptions { Retries = 3 });
        Assert.True(again.IsResume);
        await again.RetryBadAsync(CancellationToken.None);
        Assert.Equal(0, again.BadSectors);
        AssertSectorIntact(File.ReadAllBytes(Img), 300);
    }

    [Fact]
    public async Task Missing_addresses_are_skipped_not_retried()
    {
        var dev = new FakeDevice(1000);
        for (long l = 600; l < 700; l++) dev.Missing.Add(l); // e.g. gap between sessions
        var engine = new RescueEngine(dev, Img, new RescueOptions { Retries = 10 });
        await engine.RunAsync(CancellationToken.None);

        Assert.Equal(100, engine.SkippedSectors);
        Assert.Equal(0, engine.BadSectors);
        Assert.Equal(900, engine.OkSectors);
        Assert.True(dev.ReadCalls < 400, $"too many reads: {dev.ReadCalls}");
    }

    [Fact]
    public async Task Ejected_disc_stops_and_resume_finishes_without_rereading()
    {
        var dev = new FakeDevice(2000) { EjectAtLba = 1200 };
        var engine = new RescueEngine(dev, Img);
        await Assert.ThrowsAsync<NoMediaException>(() => engine.RunAsync(CancellationToken.None));
        long okBefore = engine.OkSectors;
        Assert.InRange(okBefore, 1000, 1999);

        int callsBefore = dev.ReadCalls;
        var resumed = new RescueEngine(dev, Img);
        Assert.Equal(okBefore, resumed.OkSectors);
        await resumed.RunAsync(CancellationToken.None);
        Assert.Equal(2000, resumed.OkSectors);
        Assert.True(dev.ReadCalls - callsBefore <= (2000 - okBefore) / 16 + 2);
        var bytes = File.ReadAllBytes(Img);
        AssertSectorIntact(bytes, 0);
        AssertSectorIntact(bytes, 1999);
    }

    [Fact]
    public async Task Cancel_saves_map_for_later()
    {
        var dev = new FakeDevice(100_000);
        var engine = new RescueEngine(dev, Img);
        using var cts = new CancellationTokenSource();
        dev.OnRead = n => { if (n == 50) cts.Cancel(); }; // deterministic: stop after exactly 50 reads
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.RunAsync(cts.Token));
        Assert.InRange(engine.OkSectors, 1, 99_999);
        Assert.True(File.Exists(engine.MapPath));
        var loaded = SectorMap.Load(engine.MapPath);
        Assert.Equal(engine.OkSectors, loaded.CountOf(SectorState.Ok));
    }

    [Fact]
    public async Task Selected_ranges_only_read_those_sectors()
    {
        var dev = new FakeDevice(10_000);
        dev.Dead.Add(5_005);
        var engine = new RescueEngine(dev, Img, new RescueOptions { Retries = 2 });
        (long, long)[] picked = [(5_000, 100), (9_000, 50), (5_050, 100)]; // overlapping on purpose
        await engine.RunAsync(CancellationToken.None, picked);

        Assert.Equal(150 - 1 + 50, engine.OkSectors); // 5000..5149 merged, minus the dead one, plus 9000..9049
        Assert.Equal(1, engine.BadSectors);
        Assert.Equal(SectorState.Untried, engine.Map[4_999]);
        Assert.Equal(SectorState.Untried, engine.Map[5_150]);
        Assert.Equal(SectorState.Untried, engine.Map[0]);
        Assert.Equal(0, engine.PendingIn(picked));
        AssertSectorIntact(File.ReadAllBytes(Img), 9_049);

        // A later full rescue continues from there without re-reading the picked files.
        await new RescueEngine(dev, Img).RunAsync(CancellationToken.None);
        Assert.Equal(SectorState.Ok, SectorMap.Load(engine.MapPath)[0]);
    }

    [Fact]
    public async Task Drive_is_slowed_down_when_most_reads_fail()
    {
        var dev = new FakeDevice(20_000);
        for (long l = 1_000; l < 9_000; l += 3) dev.Dead.Add(l); // a long, badly scratched stretch
        var engine = new RescueEngine(dev, Img, new RescueOptions { Retries = 1 });
        await engine.RunAsync(CancellationToken.None);
        Assert.True(engine.SlowedDown);
        Assert.Contains(4, dev.SpeedRequests);

        var clean = new FakeDevice(5_000);
        var e2 = new RescueEngine(clean, Path.Combine(_dir, "clean.iso"));
        await e2.RunAsync(CancellationToken.None);
        Assert.False(e2.SlowedDown);
    }

    [Fact]
    public void Map_from_another_disc_is_refused()
    {
        new SectorMap(10, 2048).Save(Img + ".map");
        File.WriteAllBytes(Img, new byte[10 * 2048]);
        Assert.Throws<InvalidOperationException>(() => new RescueEngine(new FakeDevice(20), Img));
    }
}

public sealed class CdSectorTests
{
    private static byte[] MakeMode1(long lba)
    {
        var s = new byte[2352];
        s[0] = 0; for (int i = 1; i <= 10; i++) s[i] = 0xFF; s[11] = 0;
        long a = lba + 150;
        static byte Bcd(long v) => (byte)((v / 10) << 4 | v % 10);
        s[12] = Bcd(a / 4500); s[13] = Bcd(a / 75 % 60); s[14] = Bcd(a % 75); s[15] = 1;
        for (int i = 16; i < 2064; i++) s[i] = (byte)(i * 7);
        BitConverter.GetBytes(CdSector.ComputeEdc(s.AsSpan(0, 2064))).CopyTo(s, 2064);
        return s;
    }

    [Fact]
    public void Good_mode1_sector_verifies()
    {
        var s = MakeMode1(12345);
        Assert.Equal(SectorKind.Mode1, CdSector.GetKind(s));
        Assert.True(CdSector.Verify(s, 12345));
    }

    [Fact]
    public void Corrupted_byte_or_wrong_address_fails()
    {
        var s = MakeMode1(100);
        Assert.False(CdSector.Verify(s, 101));
        s[500] ^= 1;
        Assert.False(CdSector.Verify(s, 100));
    }

    [Fact]
    public void Msf_matches_red_book()
    {
        Assert.Equal("00:02:00", CdSector.ToMsf(150));
        Assert.Equal("26:31:35", CdSector.ToMsf(26 * 4500 + 31 * 75 + 35));
    }
}

public sealed class IsoBuilderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dvd360-iso-" + Guid.NewGuid().ToString("N"));
    public IsoBuilderTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Built_iso_reads_back_with_same_tree_and_contents()
    {
        string src = Path.Combine(_dir, "src");
        Directory.CreateDirectory(Path.Combine(src, "Fotoğraflar", "2007 Yaz"));
        Directory.CreateDirectory(Path.Combine(src, "MPEGAV"));
        File.WriteAllText(Path.Combine(src, "okubeni.txt"), "Merhaba dünya");
        var big = new byte[5000 * 3 + 17];
        new Random(1).NextBytes(big);
        File.WriteAllBytes(Path.Combine(src, "MPEGAV", "AVSEQ01.mpg"), big);
        File.WriteAllBytes(Path.Combine(src, "Fotoğraflar", "2007 Yaz", "çok uzun bir dosya adı ile resim numarası 001.jpg"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(src, "bos.dat"), []);
        for (int i = 0; i < 60; i++) File.WriteAllText(Path.Combine(src, $"dosya_{i:000}_isim_uzunlugu_test.txt"), i.ToString());

        string iso = Path.Combine(_dir, "out.iso");
        IsoBuilder.Write(BuildNode.FromFolder(src), "Test Disk", iso);

        using var img = DiscImage.Open(iso);
        var fs = IsoFileSystem.TryOpen(img, out string err);
        Assert.True(fs != null, err);
        Assert.True(fs!.IsJoliet);
        Assert.Equal("Test Disk", fs.VolumeId);
        var files = fs.Files.ToDictionary(f => f.FullPath, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(@"Fotoğraflar\2007 Yaz\çok uzun bir dosya adı ile resim numarası 001.jpg", files.Keys);
        Assert.Equal(64, files.Count);

        string outDir = Path.Combine(_dir, "extracted");
        var report = fs.ExtractAll(outDir);
        Assert.Empty(report.Damaged);
        Assert.Equal(big, File.ReadAllBytes(Path.Combine(outDir, "MPEGAV", "AVSEQ01.mpg")));
        Assert.Equal("Merhaba dünya", File.ReadAllText(Path.Combine(outDir, "okubeni.txt")));
        Assert.Equal(0, new FileInfo(Path.Combine(outDir, "bos.dat")).Length);
        Assert.Equal("59", File.ReadAllText(Path.Combine(outDir, "dosya_059_isim_uzunlugu_test.txt")));
    }

    [Fact]
    public void Preview_copies_only_the_start_of_a_big_file()
    {
        string src = Path.Combine(_dir, "big");
        Directory.CreateDirectory(src);
        var data = new byte[2048 * 50 + 300];
        new Random(5).NextBytes(data);
        File.WriteAllBytes(Path.Combine(src, "film.mpg"), data);
        string iso = Path.Combine(_dir, "big.iso");
        IsoBuilder.Write(BuildNode.FromFolder(src), "B", iso);
        using var img = DiscImage.Open(iso);
        var fs = IsoFileSystem.TryOpen(img, out _)!;
        var ms = new MemoryStream();
        fs.CopyFile(fs.Files.Single(), ms, 10_000);
        Assert.Equal(10_000, ms.Length);
        Assert.Equal(data.AsSpan(0, 10_000).ToArray(), ms.ToArray());
    }

    [Fact]
    public async Task Damaged_sectors_are_reported_per_file()
    {
        string src = Path.Combine(_dir, "src2");
        Directory.CreateDirectory(src);
        File.WriteAllBytes(Path.Combine(src, "a.bin"), new byte[2048 * 10]);
        File.WriteAllBytes(Path.Combine(src, "b.bin"), new byte[2048 * 10]);
        string iso = Path.Combine(_dir, "src.iso");
        IsoBuilder.Write(BuildNode.FromFolder(src), "X", iso);

        // "Rescue" the ISO through a fake drive that cannot read the last sector of the disc (inside b.bin).
        var bytes = File.ReadAllBytes(iso);
        long sectors = bytes.Length / 2048;
        var dev = new FakeDevice(sectors) { Backing = bytes };
        dev.Dead.Add(sectors - 1);
        string rescued = Path.Combine(_dir, "rescued.iso");
        await new RescueEngine(dev, rescued, new RescueOptions { Retries = 1 }).RunAsync(CancellationToken.None);

        using var img = DiscImage.Open(rescued);
        var fs = IsoFileSystem.TryOpen(img, out _)!;
        var b = fs.Files.Single(f => f.Name == "b.bin");
        var a = fs.Files.Single(f => f.Name == "a.bin");
        Assert.Equal(1, b.BadSectors);
        Assert.Equal(0, a.BadSectors);
    }
}

public sealed class VideoConverterTests
{
    [Fact]
    public void Rescued_mpeg_is_read_as_program_stream_with_one_audio_track()
    {
        var args = Dvd360.Core.Video.VideoConverter.BuildArguments(new Dvd360.Core.Video.VideoJob { Input = @"C:\x\AVSEQ02.mpg", Output = @"C:\x\a.mp4" }).ToList();
        int input = args.IndexOf("-i");
        int format = args.IndexOf("-f");
        Assert.True(format >= 0 && format < input && args[format + 1] == "mpeg", "MPEG input must force the format before -i");
        Assert.Contains("200M", args);
        Assert.Contains("0:a:0?", args);
        Assert.DoesNotContain("0:a?", args);
    }

    [Fact]
    public void Mp4_input_is_left_to_autodetect()
    {
        var args = Dvd360.Core.Video.VideoConverter.BuildArguments(new Dvd360.Core.Video.VideoJob { Input = @"C:\x\clip.mp4", Output = @"C:\x\b.mp4" }).ToList();
        Assert.True(args.IndexOf("-f") < 0 || args.IndexOf("-f") > args.IndexOf("-i"));
    }
}


/// <summary>A pretend Video CD: Mode 2 Form 2 sectors with a correct EDC; "noisy" sectors come back with a few
/// different wrong bytes on every read, as a scratched VCD does (Form 2 has no error correction).</summary>
internal sealed class FakeVcd : ISectorDevice
{
    private readonly Random _rng = new(7);
    public HashSet<long> Noisy { get; } = new();

    public FakeVcd(long sectors) =>
        Info = new DiscInfo { TotalSectors = sectors, SectorSize = 2352, IsCd = true, MediaType = "CD-R", Tracks = [new TrackInfo(1, 0, true)] };

    public DiscInfo Info { get; }
    public int MaxSectorsPerRead => 16;
    public uint ReadTimeoutSeconds { get; set; } = 20;
    public bool SetReadSpeed(int multiplier) => true;
    public void Dispose() { }

    public static byte[] Sector(long lba)
    {
        var s = new byte[2352];
        for (int i = 1; i <= 10; i++) s[i] = 0xFF;
        long a = lba + 150;
        static byte Bcd(long v) => (byte)((v / 10) << 4 | v % 10);
        s[12] = Bcd(a / 4500); s[13] = Bcd(a / 75 % 60); s[14] = Bcd(a % 75); s[15] = 2;
        s[16] = s[20] = 1; s[17] = s[21] = 1; s[18] = s[22] = 0x20 | 0x04; // form 2, video
        for (int i = 24; i < 2348; i++) s[i] = (byte)(lba * 13 + i * 7);
        BitConverter.GetBytes(CdSector.ComputeEdc(s.AsSpan(16, 2332))).CopyTo(s, 2348);
        return s;
    }

    public ReadOutcome Read(long lba, int count, Span<byte> dest)
    {
        for (int k = 0; k < count; k++)
        {
            var s = Sector(lba + k);
            if (Noisy.Contains(lba + k))
                for (int j = 0; j < 3; j++) s[_rng.Next(24, 2348)] ^= (byte)_rng.Next(1, 256);
            s.CopyTo(dest.Slice(k * 2352, 2352));
        }
        return ReadOutcome.Ok;
    }
}

public sealed class VotingAndCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dvd360-vote-" + Guid.NewGuid().ToString("N"));
    public VotingAndCacheTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Generated_vcd_sector_verifies()
    {
        Assert.True(CdSector.Verify(FakeVcd.Sector(1234), 1234));
        Assert.Equal(SectorKind.Mode2Form2, CdSector.GetKind(FakeVcd.Sector(1234)));
    }

    [Fact]
    public async Task Noisy_vcd_sectors_are_repaired_by_majority_vote()
    {
        var dev = new FakeVcd(400);
        dev.Noisy.UnionWith([10, 11, 250]);
        string img = Path.Combine(_dir, "vcd.bin");
        var engine = new RescueEngine(dev, img, new RescueOptions { Retries = 5 });
        await engine.RunAsync(CancellationToken.None);

        Assert.Equal(400, engine.OkSectors);
        var bytes = File.ReadAllBytes(img);
        foreach (long lba in new long[] { 10, 11, 250 })
            Assert.True(bytes.AsSpan((int)lba * 2352, 2352).SequenceEqual(FakeVcd.Sector(lba)), $"sector {lba} not repaired");
    }

    [Fact]
    public async Task Without_voting_noisy_sectors_stay_bad()
    {
        var dev = new FakeVcd(100);
        dev.Noisy.Add(42);
        var engine = new RescueEngine(dev, Path.Combine(_dir, "novote.bin"), new RescueOptions { Retries = 5, VoteOnCopies = false });
        await engine.RunAsync(CancellationToken.None);
        Assert.Equal(1, engine.BadSectors);
    }

    [Fact]
    public async Task Retries_read_a_far_sector_first_to_bypass_the_drive_cache()
    {
        var dev = new FakeDevice(1000, new Dictionary<long, int> { [300] = 2 });
        var engine = new RescueEngine(dev, Path.Combine(_dir, "cache.iso"), new RescueOptions { Retries = 5 });
        await engine.RunAsync(CancellationToken.None);
        Assert.Equal(SectorState.Ok, engine.Map[300]);
        var reads = dev.ReadLbas;
        int first = reads.IndexOf(300);
        int second = reads.IndexOf(300, first + 1);
        Assert.True(second > first);
        Assert.Contains(800L, reads.GetRange(first, second - first)); // (300 + 1000/2) % 1000
    }

    [Fact]
    public void Majority_vote_picks_the_common_byte()
    {
        var v = RescueEngine.MajorityVote([[1, 2, 3], [1, 9, 3], [7, 2, 3]]);
        Assert.Equal(new byte[] { 1, 2, 3 }, v);
    }
}

public sealed class ReportAndAudioTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dvd360-rep-" + Guid.NewGuid().ToString("N"));
    public ReportAndAudioTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Audio_tracks_become_wav_files_with_silence_for_unread_sectors()
    {
        // a two-track audio CD image: track 1 = sectors 0..99, track 2 = 100..249
        string bin = Path.Combine(_dir, "audio.bin");
        var data = new byte[250 * 2352];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 251 + 1);
        File.WriteAllBytes(bin, data);
        var info = new DiscInfo { IsCd = true, SectorSize = 2352, TotalSectors = 250, MediaType = "CD-R", Tracks = [new TrackInfo(1, 0, false), new TrackInfo(2, 100, false)] };
        File.WriteAllText(bin + ".json", System.Text.Json.JsonSerializer.Serialize(info));
        var map = new SectorMap(250, 2352);
        for (long l = 0; l < 250; l++) map.Set(l, l == 120 ? SectorState.Bad : SectorState.Ok);
        map.Save(bin + ".map");

        using var img = DiscImage.Open(bin);
        var files = AudioTracks.ExportWav(img, Path.Combine(_dir, "wav"));

        Assert.Equal(2, files.Count);
        Assert.EndsWith("Track 01.wav", files[0]);
        var t2 = File.ReadAllBytes(files[1]);
        Assert.Equal(44 + 150 * 2352, t2.Length);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(t2, 0, 4));
        Assert.Equal(44100, BitConverter.ToInt32(t2, 24));
        Assert.Equal(data[100 * 2352], t2[44]);                                     // first sample of track 2
        Assert.True(t2.AsSpan(44 + 20 * 2352, 2352).IndexOfAnyExcept((byte)0) < 0); // sector 120 is silence
    }

    [Fact]
    public void Report_lists_fill_errors_and_files()
    {
        string src = Path.Combine(_dir, "src");
        Directory.CreateDirectory(src);
        File.WriteAllBytes(Path.Combine(src, "film.mpg"), new byte[2048 * 40]);
        string iso = Path.Combine(_dir, "r.iso");
        Dvd360.Core.Iso9660.IsoBuilder.Write(Dvd360.Core.Iso9660.BuildNode.FromFolder(src), "R", iso);
        long n = new FileInfo(iso).Length / 2048;
        var map = new SectorMap(n, 2048);
        for (long l = 0; l < n; l++) map.Set(l, l == n - 1 ? SectorState.Bad : SectorState.Ok);
        map.Save(iso + ".map");

        using var img = DiscImage.Open(iso);
        var fs = Dvd360.Core.Iso9660.IsoFileSystem.TryOpen(img, out _);
        string html = Path.Combine(_dir, "report.html");
        RescueReport.WriteHtml(html, img.Info, map, fs, "Test");
        string text = File.ReadAllText(html);
        Assert.Contains("film.mpg", text);
        Assert.Contains("<!doctype html>", text);
        Assert.Contains("var(--dead)", text);
    }
}

public sealed class AudioArgumentTests
{
    [Fact]
    public void Mp3_from_rescued_vcd_drops_video_and_forces_mpeg()
    {
        var a = Dvd360.Core.Video.VideoConverter.BuildAudioArguments(@"C:\x\AVSEQ01.mpg", @"C:\x\a.mp3", Dvd360.Core.Video.AudioFormat.Mp3).ToList();
        Assert.Contains("-vn", a);
        Assert.Contains("libmp3lame", a);
        Assert.True(a.IndexOf("-f") < a.IndexOf("-i"));
        Assert.Equal(@"C:\x\a.mp3", a[^1]);
    }

    [Fact]
    public void Flac_from_wav_is_lossless()
    {
        var a = Dvd360.Core.Video.VideoConverter.BuildAudioArguments(@"C:\x\Track 01.wav", @"C:\x\t.flac", Dvd360.Core.Video.AudioFormat.Flac).ToList();
        Assert.Contains("flac", a);
        Assert.DoesNotContain("mpeg", a);
    }
}

public sealed class AudioTrackEntryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dvd360-atr-" + Guid.NewGuid().ToString("N"));
    public AudioTrackEntryTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Audio_cd_tracks_show_up_as_wav_files_that_match_the_export()
    {
        string bin = Path.Combine(_dir, "cd.bin");
        var data = new byte[300 * 2352];
        new Random(3).NextBytes(data);
        File.WriteAllBytes(bin, data);
        var info = new DiscInfo { IsCd = true, SectorSize = 2352, TotalSectors = 300, MediaType = "CD-ROM", Tracks = [new TrackInfo(1, 0, false), new TrackInfo(2, 120, false)] };
        File.WriteAllText(bin + ".json", System.Text.Json.JsonSerializer.Serialize(info));
        var map = new SectorMap(300, 2352);
        for (long l = 0; l < 300; l++) map.Set(l, l is 200 or 201 ? SectorState.Bad : SectorState.Ok);
        map.Save(bin + ".map");

        using var img = DiscImage.Open(bin);
        var fs = Dvd360.Core.Iso9660.IsoFileSystem.ForAudioOnly(img, "Audio CD");
        Assert.Equal(2, fs.AddAudioTracks("Tracks"));
        var t2 = fs.Files.Single(f => f.Name == "Track 02.wav");
        Assert.Equal(2, t2.BadSectors);
        Assert.Equal(44 + 180 * 2352, t2.DataSize);

        var ms = new MemoryStream();
        fs.CopyFile(t2, ms);
        var exported = AudioTracks.ExportWav(img, Path.Combine(_dir, "wav"));
        Assert.Equal(File.ReadAllBytes(exported[1]), ms.ToArray());

        var part = new MemoryStream();
        fs.CopyFile(t2, part, 44 + 10 * 2352);              // preview: just the start
        Assert.Equal(44 + 10 * 2352, part.Length);
        Assert.Equal(10 * 2352, BitConverter.ToInt32(part.ToArray(), 40)); // WAV header says the true length
    }
}
