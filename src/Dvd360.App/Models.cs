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

public sealed class FileNode : Observable
{
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
    public string Icon => Entry.IsDirectory ? "📁" : Entry.IsForm2 || MainWindow.IsVideo(Entry.Name) ? "🎬"
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
