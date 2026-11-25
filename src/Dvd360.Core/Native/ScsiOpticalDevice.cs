using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Dvd360.Core.Native;

/// <summary>
/// Reads an optical drive sector by sector using SCSI pass-through (MMC commands), bypassing the
/// Windows file system. Every command has its own timeout, so a scratched area cannot hang the app,
/// and the drive's sense data tells us whether a sector is damaged or simply does not exist.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class ScsiOpticalDevice : ISectorDevice
{
    private const uint IoctlScsiPassThroughDirect = 0x4D014;
    private const int BufferSize = 64 * CdSector.RawSize + 4096;

    private readonly SafeFileHandle _handle;
    private byte* _buffer;

    public DiscInfo Info { get; private set; } = new();
    public int MaxSectorsPerRead => Info.IsCd ? 26 : 32; // stays under 64 KiB, which every USB bridge accepts
    /// <summary>Seconds the drive may spend on one read before we give up on it.</summary>
    public uint ReadTimeoutSeconds { get; set; } = 20;

    private ScsiOpticalDevice(SafeFileHandle handle)
    {
        _handle = handle;
        _buffer = (byte*)NativeMemory.AlignedAlloc(BufferSize, 4096);
    }

    /// <summary>Optical drive letters present on this PC, e.g. "E".</summary>
    public static IReadOnlyList<string> ListDrives() =>
        DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.CDRom)
            .Select(d => d.Name[..1]).ToList();

    public static ScsiOpticalDevice Open(string driveLetter)
    {
        string letter = driveLetter.TrimEnd(':', '\\').ToUpperInvariant();
        var h = CreateFile($@"\\.\{letter}:", 0xC0000000 /* read|write, needed for pass-through */, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            h.Dispose();
            if (err == 5) throw new UnauthorizedAccessException("Access to the drive was denied. Run the program as administrator.");
            throw new IOException($"Cannot open drive {letter}: (Win32 error {err}).");
        }
        var dev = new ScsiOpticalDevice(h);
        try
        {
            dev.Probe(letter);
            return dev;
        }
        catch
        {
            dev.Dispose();
            throw;
        }
    }

    // ---------------------------------------------------------------- probing

    private void Probe(string letter)
    {
        var info = new DiscInfo { DriveLetter = letter };

        if (Exec([0x12, 0, 0, 0, 36, 0], 36, out _).Success)
        {
            string vendor = Ascii(8, 8), product = Ascii(16, 16);
            info.DriveName = $"{vendor} {product}".Trim();
        }

        WaitReady();

        if (Exec([0x46, 0x00, 0, 0, 0, 0, 0, 0, 8, 0], 8, out _).Success)
            info.Profile = (ushort)(_buffer[6] << 8 | _buffer[7]);
        info.MediaType = ProfileName(info.Profile);

        var tracks = ReadToc(out long leadOut);
        bool isCd = info.Profile is >= 0x08 and <= 0x0A;
        if (info.Profile == 0 && tracks.Count > 0)
            isCd = Exec(ReadCdCdb(0, 1), CdSector.RawSize, out _).Success;

        info.IsCd = isCd;
        if (isCd)
        {
            info.SectorSize = CdSector.RawSize;
            info.Tracks = tracks;
            info.TotalSectors = leadOut > 0 ? leadOut : ReadCapacity();
        }
        else
        {
            info.SectorSize = 2048;
            info.TotalSectors = ReadCapacity();
            info.Tracks = tracks.Count > 0 ? tracks : [new TrackInfo(1, 0, true)];
        }
        if (info.TotalSectors <= 0) throw new IOException("Could not read the disc size.");

        try { info.VolumeLabel = new DriveInfo(letter).VolumeLabel; } catch { /* unreadable file system is fine */ }
        Info = info;
    }

    private void WaitReady()
    {
        for (int i = 0; i < 20; i++)
        {
            var r = Exec([0, 0, 0, 0, 0, 0], 0, out _);
            if (r.Success) return;
            if (r.SenseKey == 0x02 && r.Asc == 0x3A) throw new NoMediaException("No disc in the drive.");
            Thread.Sleep(500);
        }
        throw new NoMediaException("The drive is not ready.");
    }

    private List<TrackInfo> ReadToc(out long leadOut)
    {
        leadOut = 0;
        var list = new List<TrackInfo>();
        const int alloc = 4 + 100 * 8;
        if (!Exec([0x43, 0, 0, 0, 0, 0, 1, alloc >> 8, alloc & 0xFF, 0], alloc, out _).Success) return list;
        int len = Math.Min(alloc, (_buffer[0] << 8 | _buffer[1]) + 2);
        for (int o = 4; o + 8 <= len; o += 8)
        {
            int ctrl = _buffer[o + 1] & 0x0F, number = _buffer[o + 2];
            long lba = BigEndian32(o + 4);
            if (number == 0xAA) leadOut = lba;
            else list.Add(new TrackInfo(number, lba, (ctrl & 0x04) != 0));
        }
        return list;
    }

    private long ReadCapacity() =>
        Exec([0x25, 0, 0, 0, 0, 0, 0, 0, 0, 0], 8, out _).Success ? BigEndian32(0) + 1 : 0;

    private static string ProfileName(ushort p) => p switch
    {
        0x08 => "CD-ROM", 0x09 => "CD-R", 0x0A => "CD-RW",
        0x10 => "DVD-ROM", 0x11 => "DVD-R", 0x12 => "DVD-RAM", 0x13 or 0x14 => "DVD-RW",
        0x15 or 0x16 => "DVD-R DL", 0x1A => "DVD+RW", 0x1B => "DVD+R", 0x2A => "DVD+RW DL", 0x2B => "DVD+R DL",
        0x40 => "BD-ROM", 0x41 or 0x42 => "BD-R", 0x43 => "BD-RE",
        0x50 or 0x51 or 0x52 => "HD DVD",
        _ => p == 0 ? "?" : $"0x{p:X4}",
    };

    // ---------------------------------------------------------------- reading

    private static byte[] ReadCdCdb(long lba, int count) =>
    [
        0xBE, 0x00,                               // READ CD, any sector type
        (byte)(lba >> 24), (byte)(lba >> 16), (byte)(lba >> 8), (byte)lba,
        (byte)(count >> 16), (byte)(count >> 8), (byte)count,
        0xF8,                                     // sync + all headers + user data + EDC/ECC = 2352 bytes
        0x00, 0x00,
    ];

    private static byte[] Read12Cdb(long lba, int count) =>
    [
        0xA8, 0x00,
        (byte)(lba >> 24), (byte)(lba >> 16), (byte)(lba >> 8), (byte)lba,
        (byte)(count >> 24), (byte)(count >> 16), (byte)(count >> 8), (byte)count,
        0x00, 0x00,
    ];

    public ReadOutcome Read(long lba, int count, Span<byte> destination)
    {
        int len = count * Info.SectorSize;
        if (count <= 0 || len > BufferSize || destination.Length < len) throw new ArgumentOutOfRangeException(nameof(count));
        byte[] cdb = Info.IsCd ? ReadCdCdb(lba, count) : Read12Cdb(lba, count);

        for (int attempt = 0; attempt < 4; attempt++)
        {
            var r = Exec(cdb, len, out int transferred, ReadTimeoutSeconds);
            if (r.Success || r.SenseKey == 0x01 /* recovered error: data is good */)
            {
                if (transferred < len) return ReadOutcome.MediaError;
                new ReadOnlySpan<byte>(_buffer, len).CopyTo(destination);
                return ReadOutcome.Ok;
            }
            if (!r.IoctlOk)
            {
                if (r.Win32Error is 21 /* not ready */ or 1112 /* no media */) return ReadOutcome.NoMedia;
                return ReadOutcome.MediaError; // timeouts, CRC errors, I/O device errors
            }
            switch (r.SenseKey)
            {
                case 0x06: continue;                                    // unit attention: just retry
                case 0x02 when r.Asc == 0x3A: return ReadOutcome.NoMedia;
                case 0x02 when r.Asc == 0x04: Thread.Sleep(500); continue; // becoming ready
                case 0x05: return ReadOutcome.NotPresent;               // illegal request: LBA out of range / gap
                case 0x00 when r.ScsiStatus == 0x08: Thread.Sleep(100); continue; // busy
                default: return ReadOutcome.MediaError;                // 0x03 medium error, 0x04 hardware, 0x0B aborted
            }
        }
        return ReadOutcome.MediaError;
    }

    public bool SetReadSpeed(int multiplier)
    {
        int kbPerSecond = multiplier <= 0 ? 0xFFFF
            : Math.Min(0xFFFE, multiplier * (Info.IsCd ? 176 : Info.Profile >= 0x40 ? 4495 : 1385));
        return Exec([0xBB, 0, (byte)(kbPerSecond >> 8), (byte)kbPerSecond, 0xFF, 0xFF, 0, 0, 0, 0, 0, 0], 0, out _).Success;
    }

    // ---------------------------------------------------------------- pass-through plumbing

    private readonly record struct ScsiResult(bool IoctlOk, int Win32Error, byte ScsiStatus, byte SenseKey, byte Asc, byte Ascq)
    {
        public bool Success => IoctlOk && ScsiStatus == 0;
    }

    private ScsiResult Exec(ReadOnlySpan<byte> cdb, int dataLength, out int transferred, uint timeoutSeconds = 10)
    {
        var s = new SptdWithSense();
        s.Sptd.Length = (ushort)sizeof(ScsiPassThroughDirect);
        s.Sptd.CdbLength = (byte)cdb.Length;
        s.Sptd.SenseInfoLength = 32;
        s.Sptd.DataIn = (byte)(dataLength == 0 ? 2 : 1);
        s.Sptd.DataTransferLength = (uint)dataLength;
        s.Sptd.TimeOutValue = timeoutSeconds;
        s.Sptd.DataBuffer = dataLength == 0 ? IntPtr.Zero : (IntPtr)_buffer;
        s.Sptd.SenseInfoOffset = (uint)((byte*)s.Sense - (byte*)&s);
        for (int i = 0; i < cdb.Length; i++) s.Sptd.Cdb[i] = cdb[i];

        int size = sizeof(SptdWithSense);
        bool ok = DeviceIoControl(_handle, IoctlScsiPassThroughDirect, &s, size, &s, size, out _, IntPtr.Zero);
        int err = ok ? 0 : Marshal.GetLastWin32Error();
        transferred = (int)s.Sptd.DataTransferLength;

        byte key = 0, asc = 0, ascq = 0;
        if (ok && s.Sptd.ScsiStatus == 0x02)
        {
            byte code = (byte)(s.Sense[0] & 0x7F);
            if (code is 0x72 or 0x73) { key = (byte)(s.Sense[1] & 0x0F); asc = s.Sense[2]; ascq = s.Sense[3]; }
            else { key = (byte)(s.Sense[2] & 0x0F); asc = s.Sense[12]; ascq = s.Sense[13]; }
        }
        return new ScsiResult(ok, err, s.Sptd.ScsiStatus, key, asc, ascq);
    }

    private string Ascii(int offset, int length) =>
        new string((sbyte*)_buffer, offset, length).Trim();

    private long BigEndian32(int o) =>
        (uint)(_buffer[o] << 24 | _buffer[o + 1] << 16 | _buffer[o + 2] << 8 | _buffer[o + 3]);

    public void Dispose()
    {
        _handle.Dispose();
        if (_buffer != null)
        {
            NativeMemory.AlignedFree(_buffer);
            _buffer = null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScsiPassThroughDirect
    {
        public ushort Length;
        public byte ScsiStatus, PathId, TargetId, Lun, CdbLength, SenseInfoLength, DataIn;
        public uint DataTransferLength;
        public uint TimeOutValue;
        public IntPtr DataBuffer;
        public uint SenseInfoOffset;
        public fixed byte Cdb[16];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SptdWithSense
    {
        public ScsiPassThroughDirect Sptd;
        public uint Filler;
        public fixed byte Sense[32];
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, void* inBuffer, int inSize, void* outBuffer, int outSize, out int returned, IntPtr overlapped);
}
