using System.Text.Json;

namespace Dvd360.Core;

/// <summary>A rescued image on disk (.bin raw CD or .iso), with its sector map if there is one.</summary>
public sealed class DiscImage : ISectorSource, IDisposable
{
    private readonly FileStream _fs;
    private readonly byte[] _raw;

    private DiscImage(string path, int sectorSize, SectorMap? map, DiscInfo? info)
    {
        Path = path;
        SectorSize = sectorSize;
        Map = map;
        _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        SectorCount = _fs.Length / sectorSize;
        _raw = new byte[sectorSize];
        Info = info ?? new DiscInfo
        {
            SectorSize = sectorSize,
            IsCd = sectorSize == CdSector.RawSize,
            TotalSectors = SectorCount,
            Tracks = [new TrackInfo(1, 0, true)],
            VolumeLabel = System.IO.Path.GetFileNameWithoutExtension(path),
        };
    }

    public string Path { get; }
    public int SectorSize { get; }
    public long SectorCount { get; }
    public SectorMap? Map { get; }
    public DiscInfo Info { get; }
    public bool IsRawCd => SectorSize == CdSector.RawSize;
    public bool KnowsDamage => true;

    public static DiscImage Open(string path)
    {
        SectorMap? map = File.Exists(path + ".map") ? SectorMap.Load(path + ".map") : null;
        DiscInfo? info = null;
        if (File.Exists(path + ".json"))
        {
            try { info = JsonSerializer.Deserialize<DiscInfo>(File.ReadAllText(path + ".json")); } catch { /* optional */ }
        }
        int size = map?.SectorSize ?? info?.SectorSize ?? GuessSectorSize(path);
        return new DiscImage(path, size, map, info);
    }

    private static int GuessSectorSize(string path)
    {
        if (path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) return 2048;
        long len = new FileInfo(path).Length;
        if (len % CdSector.RawSize == 0)
        {
            using var fs = File.OpenRead(path);
            var head = new byte[16];
            fs.ReadExactly(head);
            if (CdSector.HasSync(head) || len % 2048 != 0) return CdSector.RawSize;
        }
        return 2048;
    }

    /// <summary>True if the sector was read successfully (or there is no map to say otherwise).</summary>
    public bool IsGood(long lba) => lba >= 0 && lba < SectorCount && (Map == null || Map[lba] == SectorState.Ok);

    public SectorState StateOf(long lba) => Map == null ? SectorState.Ok : lba < Map.Count ? Map[lba] : SectorState.Untried;

    public ReadOnlySpan<byte> ReadRaw(long lba)
    {
        _fs.Position = lba * SectorSize;
        _fs.ReadExactly(_raw);
        return _raw;
    }

    /// <summary>Copies the user data of a sector (2048 bytes, or 2324 for Mode 2 Form 2) into <paramref name="dest"/>.</summary>
    public (int Length, SectorKind Kind, bool Good) ReadUserData(long lba, Span<byte> dest, SectorKind expected = SectorKind.Unknown)
    {
        if (lba < 0 || lba >= SectorCount || !IsGood(lba)) return SectorData.Extract(default, IsRawCd, dest, expected);
        return SectorData.Extract(ReadRaw(lba), IsRawCd, dest, expected);
    }

    public void Dispose() => _fs.Dispose();
}
