using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using Dvd360.Core.Iso9660;

namespace Dvd360.App;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
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

    public FileNode(IsoEntry entry, bool knowsDamage)
    {
        Entry = entry;
        foreach (var c in entry.Children) Children.Add(new FileNode(c, knowsDamage));
        if (entry.IsDirectory)
        {
            int bad = entry.Descendants().Count(d => !d.IsDirectory && d.BadSectors > 0);
            StateText = !knowsDamage ? "" : bad == 0 ? "✔" : $"⚠ {bad}";
            StateBrush = bad == 0 ? OkBrush : BadBrush;
            _expanded = entry.Parent?.Parent == null;
        }
        else
        {
            StateText = !knowsDamage ? Loc.T("StateUnknown") : entry.BadSectors == 0 ? "✔ " + Loc.T("StateOk") : "⚠ " + Loc.T("StateBad", entry.BadSectors);
            StateBrush = !knowsDamage ? MutedBrush : entry.BadSectors == 0 ? OkBrush : BadBrush;
        }
    }

    private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0xD5, 0x73));
    private static readonly Brush BadBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x4D, 0x5E));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x8C, 0x97, 0xAB));

    public IsoEntry Entry { get; }
    public ObservableCollection<FileNode> Children { get; } = new();
    public string Name => Entry.IsDirectory ? Entry.Name : Entry.OutputName;
    public string Icon => Entry.IsDirectory ? "📁" : Entry.IsForm2 || MainWindow.IsVideo(Entry.Name) ? "🎬"
        : MainWindow.IsImage(Entry.Name) ? "🖼️" : "📄";
    public string SizeText => Entry.IsDirectory ? "" : MainWindow.FormatBytes(Entry.DataSize);
    public string StateText { get; }
    public Brush StateBrush { get; }
    public bool IsExpanded { get => _expanded; set => Set(ref _expanded, value); }
}

public sealed class VideoItem : Observable
{
    private double _progress;
    private string _status = Loc.T("VideoWaiting");

    public required string Name { get; init; }
    public required string Detail { get; init; }
    public string? FilePath { get; init; }
    public IsoEntry? Entry { get; init; }
    public IsoFileSystem? FileSystem { get; init; }
    public bool Done { get; set; }

    public double Progress { get => _progress; set => Set(ref _progress, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
}
