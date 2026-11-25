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

    public ReadOutcome Read(long lba, int count, Span<byte> dest)
    {
        ReadCalls++;
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

    public bool SetReadSpeed(int multiplier) => true;
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
