namespace Dvd360.Core;

public enum SectorKind { Unknown, Audio, Mode0, Mode1, Mode2Form1, Mode2Form2, Iso2048 }

/// <summary>Helpers for raw 2352-byte CD sectors: layout, header and EDC checks.</summary>
public static class CdSector
{
    public const int RawSize = 2352;
    public const int Form2DataSize = 2324;

    private static readonly uint[] EdcTable = BuildEdcTable();

    private static uint[] BuildEdcTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint e = i;
            for (int k = 0; k < 8; k++) e = (e >> 1) ^ ((e & 1) != 0 ? 0xD8018001u : 0u);
            t[i] = e;
        }
        return t;
    }

    public static uint ComputeEdc(ReadOnlySpan<byte> data)
    {
        uint edc = 0;
        foreach (byte b in data) edc = (edc >> 8) ^ EdcTable[(edc ^ b) & 0xFF];
        return edc;
    }

    public static bool HasSync(ReadOnlySpan<byte> s)
    {
        if (s.Length < 16 || s[0] != 0 || s[11] != 0) return false;
        for (int i = 1; i <= 10; i++) if (s[i] != 0xFF) return false;
        return true;
    }

    private static byte ToBcd(long v) => (byte)(((v / 10) << 4) | (v % 10));

    public static SectorKind GetKind(ReadOnlySpan<byte> s)
    {
        if (!HasSync(s)) return SectorKind.Unknown;
        return s[15] switch
        {
            0 => SectorKind.Mode0,
            1 => SectorKind.Mode1,
            2 => (s[18] & 0x20) != 0 ? SectorKind.Mode2Form2 : SectorKind.Mode2Form1,
            _ => SectorKind.Unknown,
        };
    }

    /// <summary>
    /// Checks a raw data sector returned by the drive: header address must match and the EDC must be right.
    /// Sectors without a sync pattern (audio, or data the drive could not frame) are accepted as-is.
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> s, long lba)
    {
        if (!HasSync(s)) return true;
        long a = lba + 150;
        if (s[12] != ToBcd(a / 4500) || s[13] != ToBcd(a / 75 % 60) || s[14] != ToBcd(a % 75)) return false;
        switch (GetKind(s))
        {
            case SectorKind.Mode1:
                return ComputeEdc(s[..2064]) == BitConverter.ToUInt32(s.Slice(2064, 4));
            case SectorKind.Mode2Form1:
                return ComputeEdc(s.Slice(16, 2056)) == BitConverter.ToUInt32(s.Slice(2072, 4));
            case SectorKind.Mode2Form2:
                uint stored = BitConverter.ToUInt32(s.Slice(2348, 4));
                return stored == 0 || ComputeEdc(s.Slice(16, 2332)) == stored;
            default:
                return true;
        }
    }

    /// <summary>Where the user data lives in a raw sector.</summary>
    public static (int Offset, int Length) UserData(SectorKind kind) => kind switch
    {
        SectorKind.Mode1 => (16, 2048),
        SectorKind.Mode2Form1 => (24, 2048),
        SectorKind.Mode2Form2 => (24, Form2DataSize),
        SectorKind.Mode0 => (16, 2048),
        _ => (16, 2048),
    };

    public static string ToMsf(long lba)
    {
        return $"{lba / 4500:00}:{lba / 75 % 60:00}:{lba % 75:00}";
    }
}
