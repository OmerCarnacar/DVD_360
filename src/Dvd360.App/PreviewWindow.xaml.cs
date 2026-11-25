using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Dvd360.Core.Iso9660;
using Dvd360.Core.Video;
using Microsoft.Win32;

namespace Dvd360.App;

/// <summary>Plays videos and music, shows photos and text from a disc or a rescued image, without extracting by hand.</summary>
public partial class PreviewWindow : Window
{
    private readonly FileNode _node;
    private readonly IsoFileSystem _fs;
    private readonly bool _live;
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "DVD360", "preview", Guid.NewGuid().ToString("N")[..8]);
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private string? _file;          // the (partial) copy we preview
    private bool _playing, _dragging, _partial;
    private double _clipOffset;     // seconds of the original that the clip starts at

    public sealed record Frame(double Seconds, string Label, ImageSource Image);

    /// <param name="live">Reading straight from the disc: only the start of big files is read, to stay fast.</param>
    public PreviewWindow(FileNode node, IsoFileSystem fs, bool live)
    {
        InitializeComponent();
        _node = node;
        _fs = fs;
        _live = live;
        Title = "DVD 360 — " + node.Name;
        HeaderName.Text = node.Name;
        HeaderInfo.Text = $"{node.FolderText}   •   {node.SizeText}" + (node.PctText.Length > 0 ? "   •   " + node.PctText : "")
            + (node.DateText.Length > 0 ? "   •   " + Loc.T("WrittenOn", node.DateText, node.AgeText) : "");
        HeaderGlyph.Text = node.Glyph;
        HeaderGlyph.Foreground = node.TileFore;
        HeaderTile.Background = node.TileBrush;
        HeaderChipText.Text = node.ChipText;
        HeaderChipText.Foreground = node.ChipFore;
        HeaderChip.Background = node.ChipBack;
        BusyText.Text = Loc.T("PreviewPreparing");
        _clock.Tick += (_, _) => Tick();
        Loaded += async (_, _) => await PrepareAsync();
        Closed += (_, _) => Cleanup();
    }

    // ------------------------------------------------------------------ preparing

    private async Task PrepareAsync()
    {
        var ct = _cts.Token;
        try
        {
            Directory.CreateDirectory(_temp);
            var category = _node.Category;
            bool isText = Path.GetExtension(_node.Name).ToLowerInvariant() is ".txt" or ".log" or ".ini" or ".inf" or ".csv" or ".xml" or ".htm" or ".html" or ".nfo" or ".cue" or ".m3u" or ".json";
            long limit = isText ? 2 << 20
                : _live ? category switch { FileCategory.Video => 48L << 20, _ => 40L << 20 }
                : long.MaxValue;
            _partial = _node.Entry.DataSize > limit;

            _file = Path.Combine(_temp, IsoFileSystem.Sanitize(_node.Name));
            BusyText.Text = Loc.T("PreviewReading");
            await Task.Run(() => { using var o = File.Create(_file); _fs.CopyFile(_node.Entry, o, limit, ct); }, ct);
            OpenButton.IsEnabled = true;

            string? ffmpeg = VideoConverter.FindFfmpeg();
            if (isText) ShowText();
            else switch (category)
            {
                case FileCategory.Image: await ShowImageAsync(ffmpeg, ct); break;
                case FileCategory.Video: await ShowVideoAsync(ffmpeg, ct); break;
                case FileCategory.Audio: await ShowAudioAsync(ffmpeg, ct); break;
                default: ShowNothing(Loc.T("PreviewNotSupported")); break;
            }
            if (_partial) Note.Text = Loc.T("PreviewPartial", MainWindow.FormatBytes(Math.Min(limit, _node.Entry.DataSize)));
            if (_node.HasProblem && Note.Text.Length == 0) Note.Text = Loc.T("PreviewDamaged");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowNothing(ex.Message); }
        finally { Busy.Visibility = Visibility.Collapsed; }
    }

    private void ShowNothing(string text)
    {
        NoPreviewText.Text = text;
        NoPreview.Visibility = Visibility.Visible;
    }

    private void ShowText()
    {
        byte[] bytes = File.ReadAllBytes(_file!);
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { text = Encoding.Latin1.GetString(bytes); }
        TextView.Text = text.Replace("\0", "");
        TextView.Visibility = Visibility.Visible;
    }

    private static BitmapImage? LoadBitmap(string path)
    {
        try
        {
            var b = new BitmapImage();
            b.BeginInit();
            b.CacheOption = BitmapCacheOption.OnLoad;
            b.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            b.UriSource = new Uri(path);
            b.EndInit();
            b.Freeze();
            return b;
        }
        catch { return null; }
    }

    private async Task ShowImageAsync(string? ffmpeg, CancellationToken ct)
    {
        var bmp = LoadBitmap(_file!);
        if (bmp == null && ffmpeg != null)
        {
            // damaged or exotic pictures: FFmpeg decodes whatever is readable
            string png = Path.Combine(_temp, "picture.png");
            if (await VideoConverter.ExtractFrameAsync(ffmpeg, _file!, 0, png, 2400, ct)) bmp = LoadBitmap(png);
        }
        if (bmp == null) { ShowNothing(Loc.T("PreviewImageFailed")); return; }
        Picture.Source = bmp;
        Picture.Visibility = Visibility.Visible;
        HeaderInfo.Text += $"   •   {bmp.PixelWidth}×{bmp.PixelHeight}";
    }

    private async Task ShowVideoAsync(string? ffmpeg, CancellationToken ct)
    {
        string source = _file!;
        if (ffmpeg != null)
        {
            BusyText.Text = Loc.T("PreviewVideo");
            double duration = await VideoConverter.GetDurationAsync(ffmpeg, _file!, ct);
            string clip = Path.Combine(_temp, "preview.mp4");
            await VideoConverter.MakePreviewClipAsync(ffmpeg, _file!, clip, 240, ct);
            source = clip;
            _ = BuildStripAsync(ffmpeg, duration, ct);
        }
        StartMedia(source, video: true);
    }

    private async Task BuildStripAsync(string ffmpeg, double duration, CancellationToken ct)
    {
        if (duration <= 1) return;
        var frames = new List<Frame>();
        double span = Math.Min(duration, 240); // the playable clip
        for (int i = 0; i < 8; i++)
        {
            double t = span * (i + 0.5) / 8;
            string png = Path.Combine(_temp, $"f{i}.png");
            if (await VideoConverter.ExtractFrameAsync(ffmpeg, _file!, t, png, 220, ct) && LoadBitmap(png) is { } img)
                frames.Add(new Frame(t, TimeSpan.FromSeconds(t).ToString(@"m\:ss"), img));
        }
        if (frames.Count == 0) return;
        Strip.ItemsSource = frames;
        Strip.Visibility = Visibility.Visible;
    }

    private async Task ShowAudioAsync(string? ffmpeg, CancellationToken ct)
    {
        string source = _file!;
        AudioView.Visibility = Visibility.Visible;
        if (ffmpeg != null)
        {
            BusyText.Text = Loc.T("PreviewWave");
            string dim = Path.Combine(_temp, "wave.png"), lit = Path.Combine(_temp, "wave-lit.png");
            if (await VideoConverter.WaveformAsync(ffmpeg, _file!, dim, 1600, 260, "0x8C97AB", ct)) Waveform.Source = LoadBitmap(dim);
            if (await VideoConverter.WaveformAsync(ffmpeg, _file!, lit, 1600, 260, "0x2ED573", ct)) WavePlayed.Source = LoadBitmap(lit);
            // Windows plays mp3 / wav / wma / m4a / flac itself; anything else becomes a short MP3
            if (Path.GetExtension(_file!).ToLowerInvariant() is not (".mp3" or ".wav" or ".wma" or ".m4a" or ".aac" or ".flac"))
            {
                string mp3 = Path.Combine(_temp, "preview.mp3");
                await VideoConverter.AudioPreviewAsync(ffmpeg, _file!, mp3, ct);
                source = mp3;
            }
        }
        StartMedia(source, video: false);
    }

    // ------------------------------------------------------------------ playback

    private void StartMedia(string path, bool video)
    {
        Player.Source = new Uri(path);
        Player.Volume = Volume.Value;
        Player.Visibility = video ? Visibility.Visible : Visibility.Collapsed;
        Transport.Visibility = Visibility.Visible;
        Player.Play();
        _playing = true;
        UpdatePlayButton();
        _clock.Start();
    }

    private void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (Player.NaturalDuration.HasTimeSpan) Seek.Maximum = Player.NaturalDuration.TimeSpan.TotalSeconds;
    }

    private void Player_MediaEnded(object sender, RoutedEventArgs e)
    {
        Player.Stop();
        _playing = false;
        UpdatePlayButton();
    }

    private void Player_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        Transport.Visibility = Visibility.Collapsed;
        Player.Visibility = Visibility.Collapsed;
        AudioView.Visibility = Visibility.Collapsed;
        ShowNothing(Loc.T(VideoConverter.FindFfmpeg() == null ? "PreviewNeedsFfmpeg" : "PreviewPlayFailed"));
    }

    private void Tick()
    {
        if (!Player.NaturalDuration.HasTimeSpan) return;
        double pos = Player.Position.TotalSeconds, len = Player.NaturalDuration.TimeSpan.TotalSeconds;
        if (!_dragging) Seek.Value = pos;
        TimeText.Text = $"{TimeSpan.FromSeconds(pos + _clipOffset):m\\:ss} / {TimeSpan.FromSeconds(len + _clipOffset):m\\:ss}";
        if (AudioView.Visibility == Visibility.Visible && len > 0)
        {
            double x = WaveHost.ActualWidth * pos / len;
            WaveClip.Rect = new Rect(0, 0, x, WaveHost.ActualHeight);
            WaveHead.Margin = new Thickness(Math.Max(0, x - 1), 0, 0, 0);
        }
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_playing) Player.Pause(); else Player.Play();
        _playing = !_playing;
        UpdatePlayButton();
    }

    private void UpdatePlayButton()
    {
        Ico.SetGlyph(PlayButton, _playing ? "" : "");
        PlayButton.Content = Loc.T(_playing ? "Pause" : "Play");
    }

    private void Seek_DragStarted(object sender, DragStartedEventArgs e) => _dragging = true;
    private void Seek_DragCompleted(object sender, DragCompletedEventArgs e) { _dragging = false; Player.Position = TimeSpan.FromSeconds(Seek.Value); }
    private void Seek_MouseUp(object sender, MouseButtonEventArgs e) { if (!_dragging) Player.Position = TimeSpan.FromSeconds(Seek.Value); }
    private void Volume_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (Player != null) Player.Volume = Volume.Value; }

    private void StripFrame_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: double t })
        {
            Player.Position = TimeSpan.FromSeconds(t);
            if (!_playing) { Player.Play(); _playing = true; UpdatePlayButton(); }
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        else if (e.Key == Key.Space && Transport.Visibility == Visibility.Visible) { Play_Click(this, e); e.Handled = true; }
    }

    // ------------------------------------------------------------------ footer

    private void OpenExternally_Click(object sender, RoutedEventArgs e)
    {
        if (_file != null && File.Exists(_file)) Process.Start(new ProcessStartInfo(_file) { UseShellExecute = true });
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog { FileName = _node.Name, Filter = "*" + Path.GetExtension(_node.Name) + "|*" + Path.GetExtension(_node.Name) + "|*.*|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        SaveButton.IsEnabled = false;
        Note.Text = Loc.T("Working");
        try
        {
            string target = dlg.FileName;
            await Task.Run(() => { using var o = File.Create(target); _fs.CopyFile(_node.Entry, o, _cts.Token); }, _cts.Token);
            Note.Text = Loc.T("SavedTo", target);
        }
        catch (Exception ex) { Note.Text = ex.Message; }
        finally { SaveButton.IsEnabled = true; }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Cleanup()
    {
        _cts.Cancel();
        _clock.Stop();
        try { Player.Stop(); Player.Close(); } catch { }
        Picture.Source = null;
        Strip.ItemsSource = null;
        try { Directory.Delete(_temp, true); } catch { /* a player may still hold a file; the temp folder is swept on next start */ }
    }

    /// <summary>Removes preview leftovers from earlier runs.</summary>
    public static void SweepOld()
    {
        try
        {
            string root = Path.Combine(Path.GetTempPath(), "DVD360", "preview");
            if (!Directory.Exists(root)) return;
            foreach (var d in Directory.GetDirectories(root))
                if (Directory.GetLastWriteTimeUtc(d) < DateTime.UtcNow.AddHours(-1)) try { Directory.Delete(d, true); } catch { }
        }
        catch { }
    }
}
