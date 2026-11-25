using System.Text;

namespace Dvd360.Core.Iso9660;

/// <summary>A file or folder to put into a new ISO.</summary>
public sealed class BuildNode
{
    public required string Name { get; init; }
    public bool IsDirectory { get; init; }
    public long Length { get; init; }
    public DateTime Modified { get; init; } = DateTime.Now;
    public List<BuildNode> Children { get; } = new();
    /// <summary>Writes exactly <see cref="Length"/> bytes.</summary>
    public Action<Stream>? WriteData { get; init; }

    internal BuildNode? Parent;
    internal byte[] IsoId = [], JolietId = [];
    internal long IsoExtent, JolietExtent, IsoDirSize, JolietDirSize, FileExtent;
    internal int IsoNumber, JolietNumber;

    public static BuildNode FromFileSystem(IsoFileSystem fs)
    {
        BuildNode Convert(IsoEntry e)
        {
            var n = e.IsDirectory
                ? new BuildNode { Name = e.Name, IsDirectory = true, Modified = e.Modified ?? DateTime.Now }
                : new BuildNode { Name = e.OutputName, Length = e.DataSize, Modified = e.Modified ?? DateTime.Now, WriteData = s => fs.CopyFile(e, s) };
            foreach (var c in e.Children) n.Children.Add(Convert(c));
            return n;
        }
        return Convert(fs.Root);
    }

    public static BuildNode FromFolder(string folder)
    {
        var root = new BuildNode { Name = "", IsDirectory = true };
        void Fill(BuildNode node, DirectoryInfo dir)
        {
            foreach (var d in dir.EnumerateDirectories())
            {
                var c = new BuildNode { Name = d.Name, IsDirectory = true, Modified = d.LastWriteTime };
                Fill(c, d);
                node.Children.Add(c);
            }
            foreach (var f in dir.EnumerateFiles())
            {
                string p = f.FullName;
                node.Children.Add(new BuildNode
                {
                    Name = f.Name, Length = f.Length, Modified = f.LastWriteTime,
                    WriteData = s => { using var i = File.OpenRead(p); i.CopyTo(s); },
                });
            }
        }
        Fill(root, new DirectoryInfo(folder));
        return root;
    }
}

/// <summary>
/// Writes a standard ISO 9660 image with Joliet long names, which Windows can mount as a virtual drive.
/// </summary>
public static class IsoBuilder
{
    private const int S = 2048;

    public static void Write(BuildNode root, string volumeLabel, string outputPath,
        IProgress<(long Done, long Total, string Name)>? progress = null, CancellationToken ct = default)
    {
        var dirs = new List<BuildNode>();   // breadth-first = path table order
        var files = new List<BuildNode>();
        root.Parent = null;
        AssignNames(root);
        var queue = new Queue<BuildNode>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var d = queue.Dequeue();
            dirs.Add(d);
            foreach (var c in d.Children.OrderBy(c => c.IsoId, ByteOrder.Instance))
                if (c.IsDirectory) queue.Enqueue(c);
        }
        var jdirs = new List<BuildNode>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var d = queue.Dequeue();
            jdirs.Add(d);
            foreach (var c in d.Children.OrderBy(c => c.JolietId, ByteOrder.Instance))
                if (c.IsDirectory) queue.Enqueue(c);
        }
        CollectFiles(root, files);
        for (int i = 0; i < dirs.Count; i++) dirs[i].IsoNumber = i + 1;
        for (int i = 0; i < jdirs.Count; i++) jdirs[i].JolietNumber = i + 1;

        foreach (var d in dirs)
        {
            d.IsoDirSize = DirectorySize(d, joliet: false);
            d.JolietDirSize = DirectorySize(d, joliet: true);
        }

        int isoPt = PathTableSize(dirs, joliet: false), jPt = PathTableSize(jdirs, joliet: true);
        long lba = 19;
        long isoL = lba; lba += Sectors(isoPt);
        long isoM = lba; lba += Sectors(isoPt);
        long jL = lba; lba += Sectors(jPt);
        long jM = lba; lba += Sectors(jPt);
        foreach (var d in dirs) { d.IsoExtent = lba; lba += d.IsoDirSize / S; }
        foreach (var d in jdirs) { d.JolietExtent = lba; lba += d.JolietDirSize / S; }
        foreach (var f in files)
        {
            if (f.Length > uint.MaxValue) throw new NotSupportedException($"File too large for ISO 9660: {f.Name}");
            f.FileExtent = f.Length == 0 ? 0 : lba;
            lba += Sectors(f.Length);
        }
        long totalSectors = lba;

        using var o = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        o.Write(new byte[16 * S]);
        o.Write(VolumeDescriptor(1, root, volumeLabel, totalSectors, isoPt, isoL, isoM, joliet: false));
        o.Write(VolumeDescriptor(2, root, volumeLabel, totalSectors, jPt, jL, jM, joliet: true));
        var term = new byte[S];
        term[0] = 255; "CD001"u8.CopyTo(term.AsSpan(1)); term[6] = 1;
        o.Write(term);

        WritePadded(o, PathTable(dirs, joliet: false, bigEndian: false));
        WritePadded(o, PathTable(dirs, joliet: false, bigEndian: true));
        WritePadded(o, PathTable(jdirs, joliet: true, bigEndian: false));
        WritePadded(o, PathTable(jdirs, joliet: true, bigEndian: true));
        foreach (var d in dirs) o.Write(DirectoryData(d, joliet: false));
        foreach (var d in jdirs) o.Write(DirectoryData(d, joliet: true));

        long total = files.Sum(f => f.Length), done = 0;
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((done, total, f.Name));
            if (f.Length == 0) continue;
            long start = o.Position;
            f.WriteData!(o);
            long written = o.Position - start;
            if (written != f.Length) throw new IOException($"{f.Name}: expected {f.Length} bytes, got {written}.");
            int pad = (int)(Sectors(f.Length) * S - f.Length);
            if (pad > 0) o.Write(new byte[pad]);
            done += f.Length;
        }
        progress?.Report((total, total, ""));
        if (o.Position != totalSectors * S) throw new InvalidOperationException("ISO layout mismatch.");
    }

    private static void CollectFiles(BuildNode d, List<BuildNode> files)
    {
        foreach (var c in d.Children)
        {
            c.Parent = d;
            if (c.IsDirectory) CollectFiles(c, files); else files.Add(c);
        }
    }

    private static long Sectors(long bytes) => (bytes + S - 1) / S;

    // ---------------------------------------------------------------- names

    private static void AssignNames(BuildNode dir)
    {
        var isoUsed = new HashSet<string>();
        var jUsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in dir.Children)
        {
            c.Parent = dir;
            c.IsoId = Encoding.ASCII.GetBytes(IsoName(c, isoUsed));
            c.JolietId = Encoding.BigEndianUnicode.GetBytes(JolietName(c, jUsed));
            if (c.IsDirectory) AssignNames(c);
        }
    }

    private static string DChars(string s)
    {
        var sb = new StringBuilder();
        foreach (char ch in s.ToUpperInvariant())
            sb.Append(ch is >= 'A' and <= 'Z' or >= '0' and <= '9' ? ch : '_');
        return sb.ToString();
    }

    private static string IsoName(BuildNode n, HashSet<string> used)
    {
        string baseName, ext = "";
        if (n.IsDirectory) baseName = DChars(n.Name);
        else
        {
            baseName = DChars(Path.GetFileNameWithoutExtension(n.Name));
            ext = DChars(Path.GetExtension(n.Name).TrimStart('.'));
            if (ext.Length > 3) ext = ext[..3];
        }
        if (baseName.Length == 0) baseName = "_";
        int maxBase = n.IsDirectory ? 31 : 26 - ext.Length;
        if (baseName.Length > maxBase) baseName = baseName[..maxBase];
        for (int i = 1; ; i++)
        {
            string b = i == 1 ? baseName : baseName[..Math.Min(baseName.Length, maxBase - 4)] + "_" + i.ToString("000");
            string full = n.IsDirectory ? b : b + "." + ext + ";1";
            if (used.Add(full)) return full;
        }
    }

    private static string JolietName(BuildNode n, HashSet<string> used)
    {
        string name = n.Name.Replace('*', '_').Replace('/', '_').Replace(':', '_').Replace(';', '_').Replace('?', '_').Replace('\\', '_');
        if (name.Length == 0) name = "_";
        const int max = 62;
        string stem = Path.GetFileNameWithoutExtension(name), ext = n.IsDirectory ? "" : Path.GetExtension(name);
        if (n.IsDirectory) { stem = name; }
        for (int i = 1; ; i++)
        {
            string suffix = i == 1 ? "" : $" ({i})";
            int room = max - ext.Length - suffix.Length;
            string s = (stem.Length > room ? stem[..Math.Max(1, room)] : stem) + suffix + ext;
            if (used.Add(s)) return n.IsDirectory ? s : s + ";1";
        }
    }

    // ---------------------------------------------------------------- directories

    private static int RecordLength(int idLength) => 33 + idLength + (idLength % 2 == 0 ? 1 : 0);

    private static IEnumerable<BuildNode> Sorted(BuildNode d, bool joliet) =>
        d.Children.OrderBy(c => joliet ? c.JolietId : c.IsoId, ByteOrder.Instance);

    private static long DirectorySize(BuildNode d, bool joliet)
    {
        int pos = 34 * 2;
        long sectors = 1;
        foreach (var c in Sorted(d, joliet))
        {
            int len = RecordLength((joliet ? c.JolietId : c.IsoId).Length);
            if (pos + len > S) { sectors++; pos = 0; }
            pos += len;
        }
        return sectors * S;
    }

    private static byte[] DirectoryData(BuildNode d, bool joliet)
    {
        long size = joliet ? d.JolietDirSize : d.IsoDirSize;
        var data = new byte[size];
        var parent = d.Parent ?? d;
        int pos = 0;
        pos += Record(data.AsSpan(pos), Extent(d, joliet), size, d.Modified, true, [0]);
        pos += Record(data.AsSpan(pos), Extent(parent, joliet), joliet ? parent.JolietDirSize : parent.IsoDirSize, parent.Modified, true, [1]);
        foreach (var c in Sorted(d, joliet))
        {
            byte[] id = joliet ? c.JolietId : c.IsoId;
            int len = RecordLength(id.Length);
            if (pos % S + len > S) pos = (pos / S + 1) * S;
            if (c.IsDirectory)
                Record(data.AsSpan(pos), Extent(c, joliet), joliet ? c.JolietDirSize : c.IsoDirSize, c.Modified, true, id);
            else
                Record(data.AsSpan(pos), c.FileExtent, c.Length, c.Modified, false, id);
            pos += len;
        }
        return data;
    }

    private static long Extent(BuildNode d, bool joliet) => joliet ? d.JolietExtent : d.IsoExtent;

    private static int Record(Span<byte> b, long extent, long size, DateTime t, bool dir, ReadOnlySpan<byte> id)
    {
        int len = RecordLength(id.Length);
        b[0] = (byte)len;
        Both32(b, 2, (uint)extent);
        Both32(b, 10, (uint)size);
        Date7(b[18..], t);
        b[25] = (byte)(dir ? 2 : 0);
        Both16(b, 28, 1);
        b[32] = (byte)id.Length;
        id.CopyTo(b[33..]);
        return len;
    }

    // ---------------------------------------------------------------- path tables

    private static int PathTableSize(List<BuildNode> dirs, bool joliet) =>
        dirs.Sum(d => { int l = d.Parent == null ? 1 : (joliet ? d.JolietId : d.IsoId).Length; return 8 + l + (l % 2); });

    private static byte[] PathTable(List<BuildNode> dirs, bool joliet, bool bigEndian)
    {
        var ms = new MemoryStream();
        Span<byte> e = stackalloc byte[8];
        foreach (var d in dirs)
        {
            byte[] id = d.Parent == null ? [0] : joliet ? d.JolietId : d.IsoId;
            uint extent = (uint)Extent(d, joliet);
            ushort parent = (ushort)(d.Parent == null ? 1 : joliet ? d.Parent.JolietNumber : d.Parent.IsoNumber);
            e.Clear();
            e[0] = (byte)id.Length;
            if (bigEndian)
            {
                e[2] = (byte)(extent >> 24); e[3] = (byte)(extent >> 16); e[4] = (byte)(extent >> 8); e[5] = (byte)extent;
                e[6] = (byte)(parent >> 8); e[7] = (byte)parent;
            }
            else
            {
                BitConverter.TryWriteBytes(e[2..], extent);
                BitConverter.TryWriteBytes(e[6..], parent);
            }
            ms.Write(e);
            ms.Write(id);
            if (id.Length % 2 == 1) ms.WriteByte(0);
        }
        return ms.ToArray();
    }

    private static void WritePadded(Stream o, byte[] data)
    {
        o.Write(data);
        int pad = (int)(Sectors(data.Length) * S - data.Length);
        if (pad > 0) o.Write(new byte[pad]);
    }

    // ---------------------------------------------------------------- volume descriptors

    private static byte[] VolumeDescriptor(byte type, BuildNode root, string label, long totalSectors, int ptSize, long ptL, long ptM, bool joliet)
    {
        var b = new byte[S];
        b[0] = type;
        "CD001"u8.CopyTo(b.AsSpan(1));
        b[6] = 1;
        void Text(int offset, int length, string value)
        {
            var span = b.AsSpan(offset, length);
            if (joliet)
            {
                for (int i = 0; i + 1 < length; i += 2) { span[i] = 0; span[i + 1] = 0x20; }
                if (length % 2 == 1) span[^1] = 0x20;
                var bytes = Encoding.BigEndianUnicode.GetBytes(value);
                bytes.AsSpan(0, Math.Min(bytes.Length, length & ~1)).CopyTo(span);
            }
            else
            {
                span.Fill(0x20);
                Encoding.ASCII.GetBytes(value.Length > length ? value[..length] : value).CopyTo(span);
            }
        }
        string vol = joliet ? (label.Length > 16 ? label[..16] : label) : DChars(label.Length > 32 ? label[..32] : label);
        Text(8, 32, "WIN32");
        Text(40, 32, vol.Length == 0 ? "DVD360" : vol);
        Both32(b, 80, (uint)totalSectors);
        if (joliet) { b[88] = (byte)'%'; b[89] = (byte)'/'; b[90] = (byte)'E'; }
        Both16(b, 120, 1);
        Both16(b, 124, 1);
        Both16(b, 128, S);
        Both32(b, 132, (uint)ptSize);
        BitConverter.TryWriteBytes(b.AsSpan(140), (uint)ptL);
        b[148] = (byte)(ptM >> 24); b[149] = (byte)(ptM >> 16); b[150] = (byte)(ptM >> 8); b[151] = (byte)ptM;
        Record(b.AsSpan(156), joliet ? root.JolietExtent : root.IsoExtent, joliet ? root.JolietDirSize : root.IsoDirSize, root.Modified, true, [0]);
        Text(190, 128, "");
        Text(318, 128, "");
        Text(446, 128, "");
        Text(574, 128, "DVD 360 - https://github.com/OmerCarnacar/DVD_360");
        Text(702, 37, ""); Text(739, 37, ""); Text(776, 37, "");
        Date17(b.AsSpan(813), DateTime.Now);
        Date17(b.AsSpan(830), DateTime.Now);
        Date17(b.AsSpan(847), null);
        Date17(b.AsSpan(864), null);
        b[881] = 1;
        return b;
    }

    // ---------------------------------------------------------------- encoding helpers

    private static void Both16(Span<byte> b, int o, ushort v)
    {
        b[o] = (byte)v; b[o + 1] = (byte)(v >> 8);
        b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
    }

    private static void Both32(Span<byte> b, int o, uint v)
    {
        BitConverter.TryWriteBytes(b[o..], v);
        b[o + 4] = (byte)(v >> 24); b[o + 5] = (byte)(v >> 16); b[o + 6] = (byte)(v >> 8); b[o + 7] = (byte)v;
    }

    private static sbyte TzQuarters(DateTime t) =>
        (sbyte)Math.Clamp(TimeZoneInfo.Local.GetUtcOffset(t).TotalMinutes / 15, -48, 52);

    private static void Date7(Span<byte> b, DateTime t)
    {
        if (t.Year < 1900 || t.Year > 2155) t = DateTime.Now;
        b[0] = (byte)(t.Year - 1900); b[1] = (byte)t.Month; b[2] = (byte)t.Day;
        b[3] = (byte)t.Hour; b[4] = (byte)t.Minute; b[5] = (byte)t.Second;
        b[6] = (byte)TzQuarters(t);
    }

    private static void Date17(Span<byte> b, DateTime? t)
    {
        string s = t is { } v ? v.ToString("yyyyMMddHHmmss") + "00" : "0000000000000000";
        Encoding.ASCII.GetBytes(s).CopyTo(b);
        b[16] = t is { } w ? (byte)TzQuarters(w) : (byte)0;
    }

    private sealed class ByteOrder : IComparer<byte[]>
    {
        public static readonly ByteOrder Instance = new();
        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }
}

public static class IsoExport
{
    public enum Method { Copy, Exact, Rebuilt }

    /// <summary>
    /// Makes a mountable .iso from a rescued image. Data CDs are converted sector for sector (exact copy).
    /// Video CDs and other discs with Mode 2 Form 2 files cannot fit in 2048-byte ISO sectors, so a new ISO is
    /// built that contains every file in full (videos as .mpg).
    /// </summary>
    public static Method Create(DiscImage image, IsoFileSystem? fs, string isoPath,
        IProgress<(long Done, long Total, string Name)>? progress = null, CancellationToken ct = default)
    {
        if (!image.IsRawCd)
        {
            if (!string.Equals(Path.GetFullPath(image.Path), Path.GetFullPath(isoPath), StringComparison.OrdinalIgnoreCase))
                File.Copy(image.Path, isoPath, overwrite: true);
            return Method.Copy;
        }
        if (fs == null) throw new InvalidOperationException("No readable file system on this disc, so no ISO can be made. Use the .bin/.cue image instead.");
        if (!fs.HasForm2Files && fs.VolumeSpaceSize > 0 && fs.VolumeSpaceSize <= image.SectorCount)
        {
            using var o = new FileStream(isoPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            var buf = new byte[CdSector.Form2DataSize];
            long n = fs.VolumeSpaceSize;
            for (long lba = 0; lba < n; lba++)
            {
                if ((lba & 255) == 0) { ct.ThrowIfCancellationRequested(); progress?.Report((lba, n, "")); }
                image.ReadUserData(lba, buf, SectorKind.Mode1);
                o.Write(buf, 0, 2048);
            }
            progress?.Report((n, n, ""));
            return Method.Exact;
        }
        IsoBuilder.Write(BuildNode.FromFileSystem(fs), fs.VolumeId, isoPath, progress, ct);
        return Method.Rebuilt;
    }
}
