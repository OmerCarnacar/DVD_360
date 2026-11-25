using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Dvd360.Core;
using Dvd360.Core.Iso9660;

namespace Dvd360.App;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public enum FileCategory { Folder, Video, Image, Audio, Document, Archive, Other }

public sealed class FileNode : Observable
{
    private double _okWidth, _errWidth;
    private string _pctText = "", _chipText = "";
    private Brush _chipBack = Brushes.Transparent, _chipFore = MutedBrush;
    private const double BarWidth = 120;
    private bool _expanded;
    private string _stateText = "";
    private Brush _stateBrush = MutedBrush;
    private bool? _checked = false;

    public FileNode(IsoEntry entry, bool knowsDamage, FileNode? parent = null)
    {
        Entry = entry;
        KnowsDamage = knowsDamage;
        Parent = parent;
        foreach (var c in entry.Children) Children.Add(new FileNode(c, knowsDamage, this));
        _expanded = entry.IsDirectory && entry.Parent?.Parent == null;
        Refresh(null);
    }

    public static readonly Brush OkBrush = Frozen(0x2E, 0xD5, 0x73), BadBrush = Frozen(0xFF, 0x4D, 0x5E),
        RetryBrush = Frozen(0xFF, 0x4D, 0x5E), MutedBrush = Frozen(0x8C, 0x97, 0xAB), PendingBrush = Frozen(0xC9, 0xD1, 0xDC);

    private static Brush Frozen(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }

    public IsoEntry Entry { get; }
    public bool KnowsDamage { get; }

    public FileCategory Category => Entry.IsDirectory ? FileCategory.Folder
        : MainWindow.IsVideoFile(Entry) ? FileCategory.Video
        : CategoryOf(Entry.Name, form2: false) is var c && c == FileCategory.Video ? FileCategory.Other : CategoryOf(Entry.Name, form2: false);

    public static FileCategory CategoryOf(string name, bool form2 = false)
    {
        if (form2) return FileCategory.Video;
        string ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
        if (MainWindow.IsVideo(name)) return FileCategory.Video;
        if (MainWindow.IsImage(name) || ext is ".heic" or ".webp" or ".raw" or ".cr2" or ".nef") return FileCategory.Image;
        if (MainWindow.IsAudio(name) || ext == ".cda") return FileCategory.Audio;
        if (ext is ".pdf" or ".doc" or ".docx" or ".txt" or ".rtf" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".odt" or ".htm" or ".html" or ".csv") return FileCategory.Document;
        if (ext is ".zip" or ".rar" or ".7z" or ".cab" or ".iso" or ".gz" or ".tar") return FileCategory.Archive;
        return FileCategory.Other;
    }

    /// <summary>Icon glyph (Segoe Fluent Icons) and tile colours per file type.</summary>
    public string Glyph => Category switch
    {
        FileCategory.Folder => "\uE8B7", FileCategory.Video => "\uE714", FileCategory.Image => "\uE91B",
        FileCategory.Audio => "\uE8D6", FileCategory.Document => "\uE8A5", FileCategory.Archive => "\uF012", _ => "\uE7C3",
    };

    private static readonly Dictionary<FileCategory, (Color Back, Color Fore)> TileColors = new()
    {
        [FileCategory.Folder] = (Color.FromArgb(0x33, 0xF5, 0xB7, 0x3B), Color.FromRgb(0xF5, 0xB7, 0x3B)),
        [FileCategory.Video] = (Color.FromArgb(0x33, 0x8B, 0x5C, 0xF6), Color.FromRgb(0xB3, 0x94, 0xFF)),
        [FileCategory.Image] = (Color.FromArgb(0x33, 0xEC, 0x48, 0x99), Color.FromRgb(0xF4, 0x8F, 0xC4)),
        [FileCategory.Audio] = (Color.FromArgb(0x33, 0xF5, 0x9E, 0x0B), Color.FromRgb(0xFB, 0xC0, 0x5A)),
        [FileCategory.Document] = (Color.FromArgb(0x33, 0x3B, 0x82, 0xF6), Color.FromRgb(0x7F, 0xB0, 0xFF)),
        [FileCategory.Archive] = (Color.FromArgb(0x33, 0x14, 0xB8, 0xA6), Color.FromRgb(0x5E, 0xE0, 0xD0)),
        [FileCategory.Other] = (Color.FromArgb(0x33, 0x94, 0xA3, 0xB8), Color.FromRgb(0xB8, 0xC2, 0xD1)),
    };
    private static readonly Dictionary<Color, Brush> BrushCache = new();
    private static Brush B(Color c) { if (!BrushCache.TryGetValue(c, out var b)) { b = new SolidColorBrush(c); b.Freeze(); BrushCache[c] = b; } return b; }
    public Brush TileBrush => B(TileColors[Category].Back);
    public Brush TileFore => B(TileColors[Category].Fore);

    /// <summary>Folder shown under the name in the card list.</summary>
    public string FolderText => Entry.Parent?.Parent == null ? "\\" : "\\" + Entry.Parent.FullPath;

    public double OkWidth { get => _okWidth; private set => Set(ref _okWidth, value); }
    public double ErrWidth { get => _errWidth; private set => Set(ref _errWidth, value); }
    public string PctText { get => _pctText; private set => Set(ref _pctText, value); }
    public string ChipText { get => _chipText; private set => Set(ref _chipText, value); }
    public Brush ChipBack { get => _chipBack; private set => Set(ref _chipBack, value); }
    public Brush ChipFore { get => _chipFore; private set => Set(ref _chipFore, value); }
    /// <summary>True when the file has errors or unread parts.</summary>
    public bool HasProblem { get; private set; }
    public FileNode? Parent { get; }

    /// <summary>Picked for "rescue selected". Folders show a mixed state when only some of their files are picked.</summary>
    public bool? IsChecked
    {
        get => _checked;
        set
        {
            bool v = value ?? false; // a click on a mixed folder selects all of it
            SetChecked(v, updateParent: true);
        }
    }

    private void SetChecked(bool? value, bool updateParent)
    {
        if (_checked == value) return;
        _checked = value;
        OnPropertyChanged(nameof(IsChecked));
        if (value.HasValue)
            foreach (var c in Children) c.SetChecked(value, updateParent: false);
        if (updateParent) Parent?.SyncFromChildren();
    }

    private void SyncFromChildren()
    {
        bool? state = Children.Count == 0 ? _checked
            : Children.All(c => c._checked == true) ? true
            : Children.All(c => c._checked == false) ? false : null;
        if (_checked != state) { _checked = state; OnPropertyChanged(nameof(IsChecked)); }
        Parent?.SyncFromChildren();
    }

    public IEnumerable<FileNode> SelfAndDescendants()
    {
        yield return this;
        foreach (var c in Children) foreach (var d in c.SelfAndDescendants()) yield return d;
    }
    public ObservableCollection<FileNode> Children { get; } = new();
    public string Name => Entry.IsDirectory ? Entry.Name : Entry.OutputName;
    public string Icon => Entry.IsDirectory ? "📁" : Category == FileCategory.Video ? "🎬"
        : MainWindow.IsImage(Entry.Name) ? "🖼️" : "📄";
    public string SizeText => Entry.IsDirectory ? "" : MainWindow.FormatBytes(Entry.DataSize);
    public string StateText { get => _stateText; private set => Set(ref _stateText, value); }
    public Brush StateBrush { get => _stateBrush; private set => Set(ref _stateBrush, value); }
    public bool IsExpanded { get => _expanded; set => Set(ref _expanded, value); }

    /// <summary>Totals of the last refresh, in sectors.</summary>
    public (long Ok, long Bad, long Retry, long Pending) Totals { get; private set; }

    /// <summary>Recomputes this node's state from the sector map (null = use what the file system knows).</summary>
    public (long Ok, long Bad, long Retry, long Pending) Refresh(SectorMap? map)
    {
        (long Ok, long Bad, long Retry, long Pending) t = default;
        if (Entry.IsDirectory)
        {
            foreach (var c in Children)
            {
                var x = c.Refresh(map);
                t = (t.Ok + x.Ok, t.Bad + x.Bad, t.Retry + x.Retry, t.Pending + x.Pending);
            }
        }
        else if (map != null) t = map.Summarize(Entry.Extent, Entry.SectorCount);
        else if (KnowsDamage) t = (Entry.SectorCount - Entry.BadSectors, Entry.BadSectors, 0, 0);
        Totals = t;

        long total = t.Ok + t.Bad + t.Retry + t.Pending;
        UpdateVisuals(t, total, known: map != null || KnowsDamage);
        if (map == null && !KnowsDamage) { StateText = Entry.IsDirectory ? "" : Loc.T("StateUnknown"); StateBrush = MutedBrush; }
        else if (total == 0) { StateText = Entry.IsDirectory ? "" : "✔ " + Loc.T("StateOk"); StateBrush = OkBrush; }
        else if (t.Ok == total) { StateText = "✔ " + Loc.T("StateOk"); StateBrush = OkBrush; }
        else
        {
            double pct = 100.0 * t.Ok / total;
            long errors = t.Bad + t.Retry;
            StateText = errors > 0 ? Loc.T("FileFillErr", pct, errors) : Loc.T("FileFill", pct);
            StateBrush = t.Bad > 0 ? BadBrush : t.Retry > 0 ? RetryBrush : PendingBrush;
        }
        return t;
    }

    private static readonly Brush ChipOkBack = B(Color.FromArgb(0x2A, 0x2E, 0xD5, 0x73)), ChipOkFore = B(Color.FromRgb(0x4A, 0xE5, 0x8A)),
        ChipErrBack = B(Color.FromArgb(0x2E, 0xFF, 0x4D, 0x5E)), ChipErrFore = B(Color.FromRgb(0xFF, 0x7A, 0x88)),
        ChipWaitBack = B(Color.FromArgb(0x22, 0xC9, 0xD1, 0xDC)), ChipWaitFore = B(Color.FromRgb(0xC9, 0xD1, 0xDC));

    private void UpdateVisuals((long Ok, long Bad, long Retry, long Pending) t, long total, bool known)
    {
        long errors = t.Bad + t.Retry;
        double fill = total == 0 ? (known ? 1 : 0) : (double)t.Ok / total;
        double err = total == 0 ? 0 : (double)errors / total;
        OkWidth = BarWidth * fill;
        ErrWidth = BarWidth * err;
        HasProblem = known && (errors > 0 || t.Pending > 0);
        if (!known) { PctText = ""; ChipText = "—"; ChipBack = ChipWaitBack; ChipFore = ChipWaitFore; return; }
        PctText = errors > 0 ? Loc.T("FileFillErr", fill * 100, errors) : Loc.T("FileFill", fill * 100);
        if (errors == 0 && t.Pending == 0) { ChipText = "✓ " + Loc.T("StateOk"); ChipBack = ChipOkBack; ChipFore = ChipOkFore; }
        else if (errors > 0) { ChipText = Loc.T("ChipErrors", errors); ChipBack = ChipErrBack; ChipFore = ChipErrFore; }
        else { ChipText = Loc.T("ChipUnread"); ChipBack = ChipWaitBack; ChipFore = ChipWaitFore; }
    }

    /// <summary>Screen readers and UI automation read this as the item's name.</summary>
    public override string ToString() => Name;

    /// <summary>Sector ranges this node covers on the disc (a folder covers all its files).</summary>
    public IEnumerable<(long Start, long Count)> Ranges() =>
        Entry.IsDirectory
            ? Entry.Descendants().Where(e => !e.IsDirectory && e.SectorCount > 0).Select(e => (e.Extent, e.SectorCount))
            : Entry.SectorCount > 0 ? [(Entry.Extent, Entry.SectorCount)] : [];
}

public sealed class VideoItem : Observable
{
    private double _progress;
    private string _status = Loc.T("VideoWaiting");

    public required string Name { get; init; }
    public required string Detail { get; init; }
    public string? FilePath { get; init; }
    public IsoEntry? Entry { get; init; }
    /// <summary>A DVD title is split into VTS_xx_1..n.VOB parts; they are joined into one video.</summary>
    public List<IsoEntry>? Parts { get; init; }
    public IsoFileSystem? FileSystem { get; init; }
    public bool Done { get; set; }

    public double Progress { get => _progress; set => Set(ref _progress, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
}
