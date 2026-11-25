namespace Dvd360.Core;

/// <summary>Something sectors can be read from: a rescued image file or the disc itself.</summary>
public interface ISectorSource
{
    DiscInfo Info { get; }
    long SectorCount { get; }
    bool IsRawCd { get; }
    /// <summary>False when the source has no cheap way to know (live disc): damage is then not counted up front.</summary>
    bool KnowsDamage { get; }
    bool IsGood(long lba);
    ReadOnlySpan<byte> ReadRaw(long lba);
    (int Length, SectorKind Kind, bool Good) ReadUserData(long lba, Span<byte> dest, SectorKind expected = SectorKind.Unknown);
}

/// <summary>
/// Reads straight from the drive (through <see cref="ISectorDevice"/>, so with timeouts), used to show a disc's
/// contents the moment it is inserted. Results are cached; not thread-safe.
/// </summary>
public sealed class LiveDiscSource(ISectorDevice device) : ISectorSource
{
    private readonly Dictionary<long, byte[]?> _cache = new();
    private readonly byte[] _empty = new byte[CdSector.RawSize];

    public DiscInfo Info => device.Info;
    public long SectorCount => device.Info.TotalSectors;
    public bool IsRawCd => device.Info.IsCd;
    public bool KnowsDamage => false;

    private byte[]? Get(long lba)
    {
        if (lba < 0 || lba >= SectorCount) return null;
        if (_cache.TryGetValue(lba, out var hit)) return hit;
        var buf = new byte[device.Info.SectorSize];
        byte[]? result = null;
        for (int i = 0; i < 2 && result == null; i++)
        {
            var r = device.Read(lba, 1, buf);
            if (r == ReadOutcome.Ok && (!IsRawCd || CdSector.Verify(buf, lba))) result = buf;
            else if (r is ReadOutcome.NotPresent or ReadOutcome.NoMedia) break;
        }
        if (_cache.Count > 20000) _cache.Clear();
        _cache[lba] = result;
        return result;
    }

    public bool IsGood(long lba) => Get(lba) != null;

    public ReadOnlySpan<byte> ReadRaw(long lba) => Get(lba) ?? _empty.AsSpan(0, device.Info.SectorSize);

    public (int Length, SectorKind Kind, bool Good) ReadUserData(long lba, Span<byte> dest, SectorKind expected = SectorKind.Unknown)
    {
        var raw = Get(lba);
        return SectorData.Extract(raw, IsRawCd, dest, expected);
    }
}

internal static class SectorData
{
    public static (int Length, SectorKind Kind, bool Good) Extract(ReadOnlySpan<byte> raw, bool isRawCd, Span<byte> dest, SectorKind expected)
    {
        bool good = !raw.IsEmpty;
        if (!isRawCd)
        {
            if (good) raw[..2048].CopyTo(dest); else dest[..2048].Clear();
            return (2048, SectorKind.Iso2048, good);
        }
        var kind = good ? CdSector.GetKind(raw) : expected;
        if (kind is SectorKind.Unknown or SectorKind.Audio) kind = expected == SectorKind.Unknown ? SectorKind.Mode1 : expected;
        var (offset, length) = CdSector.UserData(kind);
        if (good) raw.Slice(offset, length).CopyTo(dest);
        else dest[..length].Clear();
        return (length, kind, good);
    }
}
