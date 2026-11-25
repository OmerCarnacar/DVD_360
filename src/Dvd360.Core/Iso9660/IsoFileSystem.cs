using System.Text;

namespace Dvd360.Core.Iso9660;

public sealed class IsoEntry
{
    public required string Name { get; init; }
    public required bool IsDirectory { get; init; }
    public long Extent { get; init; }
    /// <summary>Size as recorded in the directory (sectors × 2048 for Mode 2 Form 2 files).</summary>
    public long RecordedSize { get; init; }
    public DateTime? Modified { get; init; }
    /// <summary>Video CD style file stored in 2324-byte Mode 2 Form 2 sectors.</summary>
    public bool IsForm2 { get; set; }
    public IsoEntry? Parent { get; set; }
    public List<IsoEntry> Children { get; } = new();

    public long SectorCount => (RecordedSize + 2047) / 2048;
    /// <summary>Real number of bytes when the file is extracted.</summary>
    public long DataSize => IsForm2 ? SectorCount * CdSector.Form2DataSize : RecordedSize;
    /// <summary>Name used on extraction: VCD .DAT video files become .mpg so players open them.</summary>
    public string OutputName => IsForm2 && Name.EndsWith(".DAT", StringComparison.OrdinalIgnoreCase)
        ? Name[..^4] + ".mpg" : Name;
    public int BadSectors { get; set; }

    public string FullPath => Parent == null ? "" : Parent.Parent == null ? Name : Parent.FullPath + "\\" + Name;

    public IEnumerable<IsoEntry> Descendants()
    {
        foreach (var c in Children)
        {
            yield return c;
            if (c.IsDirectory) foreach (var d in c.Descendants()) yield return d;
        }
    }
}

/// <summary>Reads the ISO 9660 / Joliet file system of a (possibly damaged) image.</summary>
public sealed class IsoFileSystem
{
    private readonly ISectorSource _image;
    private readonly byte[] _sector = new byte[CdSector.RawSize];

    private IsoFileSystem(ISectorSource image, IsoEntry root, string volumeId, bool joliet)
    {
        _image = image;
        Root = root;
        VolumeId = volumeId;
        IsJoliet = joliet;
    }

    public IsoEntry Root { get; }
    public string VolumeId { get; }
    public bool IsJoliet { get; }
    /// <summary>Volume size in 2048-byte blocks from the primary volume descriptor.</summary>
    public long VolumeSpaceSize { get; private init; }
    public ISectorSource Source => _image;
    public List<string> Warnings { get; } = new();
    /// <summary>First volume descriptor sector (session start + 16).</summary>
    public long DescriptorLba { get; private init; }

    /// <summary>
    /// Sectors that hold the file system itself (volume descriptors, path tables, directories). A partial rescue
    /// must include these, otherwise the rescued files could not be found in the image afterwards.
    /// </summary>
    public IEnumerable<(long Start, long Count)> MetadataRanges()
    {
        yield return (Math.Max(0, DescriptorLba - 16), 16 + 48); // system area + descriptors + (usually) path tables
        yield return (Root.Extent, Root.SectorCount);
        foreach (var d in Root.Descendants().Where(e => e.IsDirectory)) yield return (d.Extent, d.SectorCount);
    }

    /// <summary>Finds the entry with the same path (e.g. map a file picked on the live disc to the rescued image).</summary>
    public IsoEntry? Find(string fullPath) =>
        Root.Descendants().FirstOrDefault(e => string.Equals(e.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<IsoEntry> Files => Root.Descendants().Where(e => !e.IsDirectory);
    public bool HasForm2Files => Files.Any(f => f.IsForm2);

    /// <summary>Opens the file system, preferring the last session and Joliet names. Returns null if none is readable.</summary>
    public static IsoFileSystem? TryOpen(ISectorSource image, out string error)
    {
        error = "";
        var starts = image.Info.Tracks.Where(t => t.IsData).Select(t => t.StartLba).Append(0).Distinct().OrderByDescending(x => x);
        var buf = new byte[CdSector.RawSize];
        foreach (long start in starts)
        {
            long? pvdRoot = null, pvdSize = null, jRoot = null, jSize = null;
            long volumeSpace = 0;
            string volume = "";
            bool any = false;
            for (long lba = start + 16; lba < start + 16 + 64 && lba < image.SectorCount; lba++)
            {
                // The descriptor set must start exactly at start+16; if it is not there, this track has none.
                if (!image.IsGood(lba)) { if (lba == start + 16) break; continue; }
                image.ReadUserData(lba, buf, SectorKind.Mode2Form1);
                if (buf[1] != 'C' || buf[2] != 'D' || buf[3] != '0' || buf[4] != '0' || buf[5] != '1') { if (any || lba == start + 16) break; continue; }
                any = true;
                byte type = buf[0];
                if (type == 255) break;
                if (type == 1 && pvdRoot == null)
                {
                    pvdRoot = Le32(buf, 156 + 2); pvdSize = Le32(buf, 156 + 10);
                    volumeSpace = Le32(buf, 80);
                    volume = Encoding.ASCII.GetString(buf, 40, 32).Trim();
                }
                else if (type == 2 && buf[88] == '%' && buf[89] == '/' && buf[90] is (byte)'@' or (byte)'C' or (byte)'E')
                {
                    jRoot = Le32(buf, 156 + 2); jSize = Le32(buf, 156 + 10);
                    string jv = Encoding.BigEndianUnicode.GetString(buf, 40, 32).Trim('\0', ' ');
                    if (jv.Length > 0) volume = jv;
                }
            }
            if (pvdRoot == null && jRoot == null) continue;
            bool joliet = jRoot != null;
            var root = new IsoEntry { Name = "", IsDirectory = true, Extent = joliet ? jRoot!.Value : pvdRoot!.Value, RecordedSize = joliet ? jSize!.Value : pvdSize!.Value };
            var fs = new IsoFileSystem(image, root, volume, joliet) { VolumeSpaceSize = volumeSpace, DescriptorLba = start + 16 };
            fs.ReadTree(root);
            if (joliet && pvdRoot != null) fs.CopyXaFlagsFromIsoTree(pvdRoot.Value, pvdSize!.Value);
            foreach (var f in fs.Files) fs.FinishFile(f);
            return fs;
        }
        error = "No readable ISO 9660 file system was found (the disc may be audio, UDF-only, or the directory area is damaged).";
        return null;
    }

    private void ReadTree(IsoEntry root)
    {
        var visited = new HashSet<long>();
        var stack = new Stack<(IsoEntry Dir, int Depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Pop();
            if (depth > 64 || !visited.Add(dir.Extent)) continue;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rec in ReadDirectory(dir.Extent, dir.RecordedSize, IsJoliet))
            {
                string name = UniqueName(Sanitize(rec.Name), names);
                var e = new IsoEntry
                {
                    Name = name, IsDirectory = rec.IsDir, Extent = rec.Extent, RecordedSize = rec.Size,
                    Modified = rec.Date, IsForm2 = rec.Form2, Parent = dir,
                };
                dir.Children.Add(e);
                if (e.IsDirectory) stack.Push((e, depth + 1));
            }
            dir.Children.Sort((a, b) => a.IsDirectory != b.IsDirectory ? (a.IsDirectory ? -1 : 1) : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Joliet records often lack the XA attributes; take them from the ISO tree by extent.</summary>
    private void CopyXaFlagsFromIsoTree(long rootExtent, long rootSize)
    {
        var form2Extents = new HashSet<long>();
        var visited = new HashSet<long>();
        var queue = new Queue<(long, long, int)>();
        queue.Enqueue((rootExtent, rootSize, 0));
        while (queue.Count > 0)
        {
            var (ext, size, depth) = queue.Dequeue();
            if (depth > 64 || !visited.Add(ext)) continue;
            foreach (var r in ReadDirectory(ext, size, joliet: false))
            {
                if (r.IsDir) queue.Enqueue((r.Extent, r.Size, depth + 1));
                else if (r.Form2) form2Extents.Add(r.Extent);
            }
        }
        foreach (var f in Files) if (form2Extents.Contains(f.Extent)) f.IsForm2 = true;
    }

    private void FinishFile(IsoEntry f)
    {
        // Fall back to the sector headers when the directory did not say.
        if (!f.IsForm2 && _image.IsRawCd && f.SectorCount > 0 && _image.IsGood(f.Extent))
            f.IsForm2 = CdSector.GetKind(_image.ReadRaw(f.Extent)) == SectorKind.Mode2Form2;
        if (!_image.KnowsDamage) return;
        int bad = 0;
        for (long i = 0; i < f.SectorCount; i++) if (!_image.IsGood(f.Extent + i)) bad++;
        f.BadSectors = bad;
    }

    private readonly record struct DirRecord(string Name, bool IsDir, long Extent, long Size, DateTime? Date, bool Form2);

    private IEnumerable<DirRecord> ReadDirectory(long extent, long size, bool joliet)
    {
        long sectors = Math.Min((size + 2047) / 2048, 4096);
        for (long s = 0; s < sectors; s++)
        {
            long lba = extent + s;
            if (!_image.IsGood(lba))
            {
                Warnings.Add($"Directory sector {lba} is damaged; some names may be missing.");
                continue;
            }
            _image.ReadUserData(lba, _sector, SectorKind.Mode2Form1);
            int pos = 0;
            while (pos < 2048)
            {
                int len = _sector[pos];
                if (len == 0) break;
                if (len < 34 || pos + len > 2048) break;
                int nameLen = _sector[pos + 32];
                if (33 + nameLen > len) break;
                bool self = nameLen == 1 && (_sector[pos + 33] == 0 || _sector[pos + 33] == 1);
                if (!self)
                {
                    byte flags = _sector[pos + 25];
                    string name = joliet
                        ? Encoding.BigEndianUnicode.GetString(_sector, pos + 33, nameLen & ~1)
                        : Encoding.Latin1.GetString(_sector, pos + 33, nameLen);
                    int semi = name.IndexOf(';');
                    if (semi >= 0) name = name[..semi];
                    if (!joliet && name.EndsWith('.')) name = name[..^1];
                    bool form2 = false;
                    int su = pos + 33 + nameLen + (nameLen % 2 == 0 ? 1 : 0);
                    if (su + 14 <= pos + len && _sector[su + 6] == 'X' && _sector[su + 7] == 'A')
                        form2 = (((_sector[su + 4] << 8) | _sector[su + 5]) & 0x1000) != 0;
                    yield return new DirRecord(name, (flags & 0x02) != 0, Le32(_sector, pos + 2), Le32(_sector, pos + 10), Date(_sector, pos + 18), form2);
                }
                pos += len;
            }
        }
    }

    private static DateTime? Date(byte[] b, int o)
    {
        try
        {
            if (b[o] == 0) return null;
            var local = new DateTime(1900 + b[o], b[o + 1], b[o + 2], b[o + 3], b[o + 4], b[o + 5], DateTimeKind.Unspecified);
            return local;
        }
        catch { return null; }
    }

    private static long Le32(byte[] b, int o) => (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24);

    public static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (char c in name) sb.Append(c < 32 || invalid.Contains(c) ? '_' : c);
        string s = sb.ToString().Trim().TrimEnd('.');
        return s.Length == 0 || s == "." || s == ".." ? "_" : s;
    }

    private static string UniqueName(string name, HashSet<string> used)
    {
        string candidate = name;
        for (int i = 2; !used.Add(candidate); i++)
            candidate = Path.GetFileNameWithoutExtension(name) + "_" + i + Path.GetExtension(name);
        return candidate;
    }

    // ---------------------------------------------------------------- extraction

    /// <summary>Copies a file's data to <paramref name="output"/>. Damaged sectors become zeros.</summary>
    public void CopyFile(IsoEntry file, Stream output, CancellationToken ct = default)
    {
        var expected = file.IsForm2 ? SectorKind.Mode2Form2 : SectorKind.Mode2Form1;
        long remaining = file.IsForm2 ? file.DataSize : file.RecordedSize;
        var buf = new byte[CdSector.Form2DataSize];
        for (long i = 0; i < file.SectorCount && remaining > 0; i++)
        {
            if ((i & 63) == 0) ct.ThrowIfCancellationRequested();
            var (len, _, _) = _image.ReadUserData(file.Extent + i, buf, expected);
            if (file.IsForm2 && len < CdSector.Form2DataSize)
            {
                buf.AsSpan(len).Clear();
                len = CdSector.Form2DataSize;
            }
            int n = (int)Math.Min(len, remaining);
            output.Write(buf, 0, n);
            remaining -= n;
        }
    }

    public sealed record ExtractReport(int Files, int Directories, long Bytes, List<IsoEntry> Damaged);

    public ExtractReport ExtractAll(string outputDir, IProgress<(long Done, long Total, string Name)>? progress = null, CancellationToken ct = default) =>
        Extract(Files, outputDir, progress, ct);

    /// <summary>Extracts just the given files (keeping their folders).</summary>
    public ExtractReport Extract(IEnumerable<IsoEntry> selection, string outputDir, IProgress<(long Done, long Total, string Name)>? progress = null, CancellationToken ct = default)
    {
        string root = Path.GetFullPath(outputDir);
        Directory.CreateDirectory(root);
        var files = selection.Where(f => !f.IsDirectory).Distinct().ToList();
        var wanted = new HashSet<IsoEntry>(files.SelectMany(f => Ancestors(f)));
        long total = files.Sum(f => f.DataSize), done = 0;
        int dirs = 0;
        var damaged = new List<IsoEntry>();
        foreach (var d in Root.Descendants().Where(e => e.IsDirectory && wanted.Contains(e)))
        {
            Directory.CreateDirectory(SafeJoin(root, d));
            dirs++;
        }
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((done, total, f.FullPath));
            string path = SafeJoin(root, f, f.OutputName);
            using (var o = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                CopyFile(f, o, ct);
            if (f.Modified is { } m) { try { File.SetLastWriteTime(path, m); } catch { } }
            if (f.BadSectors > 0) damaged.Add(f);
            done += f.DataSize;
        }
        progress?.Report((total, total, ""));
        WriteReport(Path.Combine(root, "_DVD360_rapor.txt"), files, damaged);
        return new ExtractReport(files.Count, dirs, total, damaged);
    }

    private static IEnumerable<IsoEntry> Ancestors(IsoEntry e)
    {
        for (var p = e.Parent; p != null; p = p.Parent) yield return p;
    }

    private static string SafeJoin(string root, IsoEntry e, string? leafName = null)
    {
        string rel = e.Parent == null || e.Parent.Parent == null ? "" : e.Parent.FullPath;
        string full = Path.GetFullPath(Path.Combine(root, rel, leafName ?? e.Name));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe path in image: " + e.FullPath);
        return full;
    }

    private void WriteReport(string path, List<IsoEntry> files, List<IsoEntry> damaged)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"DVD 360 — {VolumeId}  ({DateTime.Now:g})");
        sb.AppendLine($"Files: {files.Count}, damaged: {damaged.Count}");
        sb.AppendLine();
        foreach (var f in files)
            sb.AppendLine($"{(f.BadSectors == 0 ? "OK     " : "HASARLI")}  {f.FullPath}  ({f.DataSize:N0} bytes{(f.BadSectors > 0 ? $", {f.BadSectors} bad sectors" : "")})");
        foreach (var w in Warnings.Distinct()) sb.AppendLine("! " + w);
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }
}
