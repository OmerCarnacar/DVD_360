using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Navigation;
using System.Windows.Threading;
using Dvd360.Core;
using Dvd360.Core.Iso9660;
using Dvd360.Core.Native;
using Dvd360.Core.Video;
using Microsoft.Win32;

namespace Dvd360.App;

public partial class MainWindow : Window
{
    // ---- disc / drive state
    private string? _driveLetter;
    private DiscInfo? _disc;
    private int _detectVersion;

    // ---- rescue
    private RescueEngine? _engine;
    private CancellationTokenSource? _rescueCts;
    private bool _nameIsAuto = true;
    private bool _settingName;

    // ---- contents
    private ScsiOpticalDevice? _liveDevice;
    private DiscImage? _image;
    private IsoFileSystem? _fs;
    private string? _mountedIso;
    private bool _contentBusy;
    private FileNode? _rootNode;
    private SectorMap? _previewMap;           // map of an earlier rescue of the inserted disc
    private List<IsoEntry> _filesByExtent = new();
    private int _treeTick;

    /// <summary>The freshest knowledge of which sectors are good: running rescue, opened image, or earlier rescue.</summary>
    private SectorMap? StatusMap => _engine?.Map ?? (_fs?.Source is DiscImage img ? img.Map : _previewMap);

    // ---- video
    private readonly ObservableCollection<VideoItem> _videos = new();
    private CancellationTokenSource? _videoCts;

    private bool _quiet; // inside "one-click": no message boxes, finish actions only at the very end
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _deviceDebounce = new() { Interval = TimeSpan.FromMilliseconds(900) };

    public MainWindow()
    {
        InitializeComponent();
        CoreText.Turkish = !Loc.I.English;
        Title = "DVD 360 — " + Loc.T("AppSubtitle");
        AboutVersion.Text = "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");
        var settings = AppSettings.Load();
        FolderBox.Text = settings.Folder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DVD360");
        VideoFolderBox.Text = settings.VideoFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "DVD360");
        Disc.SectorHovered += Disc_SectorHovered;
        SetAutoName("Disk_" + DateTime.Now.ToString("yyyyMMdd_HHmm"));
        VideoList.ItemsSource = _videos;
        _uiTimer.Tick += (_, _) => UpdateRescueUi();
        _deviceDebounce.Tick += (_, _) => { _deviceDebounce.Stop(); RefreshDrives(); };
        UpdateFfmpegStatus();
        ShowContent(null, "", null);
        Loaded += (_, _) => { RefreshDrives(); _ = CheckForUpdateAsync(); };
        Closing += OnClosing;
    }

    // =====================================================================
    // Drive detection (WM_DEVICECHANGE tells us when a disc goes in or out)
    // =====================================================================

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_DEVICECHANGE = 0x0219, DBT_DEVICEARRIVAL = 0x8000, DBT_DEVICEREMOVECOMPLETE = 0x8004, DBT_DEVTYP_VOLUME = 2;
        if (msg == WM_DEVICECHANGE && (wParam == DBT_DEVICEARRIVAL || wParam == DBT_DEVICEREMOVECOMPLETE) && lParam != IntPtr.Zero
            && Marshal.ReadInt32(lParam, 4) == DBT_DEVTYP_VOLUME)
        {
            uint mask = (uint)Marshal.ReadInt32(lParam, 12);
            bool removed = wParam == DBT_DEVICEREMOVECOMPLETE;
            for (int i = 0; i < 26; i++)
            {
                if ((mask & (1u << i)) == 0) continue;
                string letter = ((char)('A' + i)).ToString();
                if (letter == _driveLetter && removed && _engine == null) OnDiscGone();
            }
            _deviceDebounce.Stop();
            _deviceDebounce.Start();
        }
        return IntPtr.Zero;
    }

    private void RefreshDrives()
    {
        var drives = ScsiOpticalDevice.ListDrives();
        string? keep = _driveLetter;
        DriveBox.SelectionChanged -= DriveBox_SelectionChanged;
        DriveBox.ItemsSource = drives.Select(d => d + ":").ToList();
        DriveBox.SelectedIndex = drives.Count == 0 ? -1 : Math.Max(0, drives.ToList().IndexOf(keep ?? ""));
        DriveBox.SelectionChanged += DriveBox_SelectionChanged;
        if (drives.Count == 0)
        {
            _driveLetter = null;
            DiscStatus.Text = Loc.T("NoDrive");
            Disc.SetMode(DiscViewMode.Empty, Loc.T("NoDrive"));
            return;
        }
        _driveLetter = drives[DriveBox.SelectedIndex];
        _ = DetectDiscAsync();
    }

    private void DriveBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DriveBox.SelectedItem is string s) { _driveLetter = s[..1]; _ = DetectDiscAsync(); }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshDrives();

    private async Task DetectDiscAsync()
    {
        if (_engine != null || _driveLetter == null) return; // never disturb a running rescue
        int version = ++_detectVersion;
        string letter = _driveLetter;
        Disc.SetMode(DiscViewMode.Detecting, Loc.T("Detecting"));
        DiscStatus.Text = Loc.T("Detecting");
        DiscStatus.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
        CloseLiveContent(clearTree: true);

        ScsiOpticalDevice? dev = null;
        IsoFileSystem? fs = null;
        string fsError = "";
        try
        {
            dev = await Task.Run(() => ScsiOpticalDevice.Open(letter));
            var live = new LiveDiscSource(dev);
            fs = await Task.Run(() => IsoFileSystem.TryOpen(live, out fsError));
        }
        catch (NoMediaException)
        {
            dev?.Dispose();
            if (version == _detectVersion) OnDiscGone();
            return;
        }
        catch (Exception ex)
        {
            dev?.Dispose();
            if (version != _detectVersion) return;
            OnDiscGone();
            DiscStatus.Text = ex.Message;
            return;
        }
        if (version != _detectVersion || _engine != null) { dev.Dispose(); return; }

        _disc = dev.Info;
        _liveDevice = dev;
        DiscStatus.Text = "✔ " + Loc.T("DiscDetected");
        DiscStatus.Foreground = (System.Windows.Media.Brush)FindResource("OkBrush");
        InfoMedia.Text = _disc.MediaType + (fs?.HasForm2Files == true ? "  (Video CD)" : "");
        InfoLabel.Text = string.IsNullOrWhiteSpace(_disc.VolumeLabel) ? fs?.VolumeId ?? "—" : _disc.VolumeLabel;
        ShowDiscTexts();
        InfoTracks.Text = string.Join(", ", _disc.Tracks.Select(t => $"{t.Number}:{(t.IsData ? "data" : "audio")}"));
        InfoDrive.Text = _disc.DriveName;
        ExtText.Text = _disc.ImageExtension;

        string label = InfoLabel.Text is "—" or "" ? "Disk" : InfoLabel.Text;
        Disc.TotalSectors = _disc.TotalSectors;
        string? earlier = _nameIsAuto ? FindEarlierRescue(_disc) : null;
        if (_nameIsAuto)
            SetAutoName(earlier != null ? Path.GetFileNameWithoutExtension(earlier) : IsoFileSystem.Sanitize(label) + "_" + DateTime.Now.ToString("yyyyMMdd"));
        if (earlier != null) AddLog(Loc.T("FoundEarlier", Path.GetFileName(earlier)));
        Disc.SetMode(DiscViewMode.Idle, label);

        _fs = fs;
        ShowContent(fs, Loc.T("SourceLive", letter), fs == null ? fsError : null);
        UpdateTarget();
        if (fs != null && fs.HasForm2Files) AddLog(Loc.I.English ? "Video CD detected. After the rescue the videos can be turned into MP4." : "Video CD algılandı. Kurtarmadan sonra videolar MP4'e çevrilebilir.");
    }

    private void OnDiscGone()
    {
        CloseLiveContent(clearTree: true);
        _previewMap = null;
        _disc = null;
        Disc.SetMode(DiscViewMode.Empty, Loc.T("InsertDisc"));
        DiscStatus.Text = Loc.T("InsertDisc");
        DiscStatus.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");
        InfoMedia.Text = InfoLabel.Text = InfoSize.Text = InfoTracks.Text = InfoDrive.Text = "";
        UpdateTarget();
    }

    /// <summary>Releases the drive. The tree can stay on screen (its sector layout is still valid for the rescue).</summary>
    private void CloseLiveContent(bool clearTree)
    {
        if (clearTree && _fs?.Source is LiveDiscSource) { _fs = null; ShowContent(null, "", null); }
        _liveDevice?.Dispose();
        _liveDevice = null;
    }

    /// <summary>Looks in the save folder for an unfinished or finished rescue of this same disc.</summary>
    private string? FindEarlierRescue(DiscInfo disc)
    {
        try
        {
            string folder = FolderBox.Text.Trim();
            if (!Directory.Exists(folder)) return null;
            foreach (var map in new DirectoryInfo(folder).EnumerateFiles("*" + disc.ImageExtension + ".map").OrderByDescending(f => f.LastWriteTimeUtc))
            {
                var h = SectorMap.TryReadHeader(map.FullName);
                if (h is not { } hdr || hdr.Count != disc.TotalSectors || hdr.SectorSize != disc.SectorSize) continue;
                string image = map.FullName[..^4];
                if (!File.Exists(image)) continue;
                string json = image + ".json";
                if (File.Exists(json))
                {
                    var info = System.Text.Json.JsonSerializer.Deserialize<DiscInfo>(File.ReadAllText(json));
                    if (info != null && disc.VolumeLabel.Length > 0 && info.VolumeLabel != disc.VolumeLabel) continue;
                }
                return image;
            }
        }
        catch { }
        return null;
    }

    // =====================================================================
    // Rescue
    // =====================================================================

    private void SetAutoName(string name)
    {
        _settingName = true;
        NameBox.Text = name;
        _settingName = false;
    }

    private string? TargetPath()
    {
        string folder = FolderBox.Text.Trim(), name = NameBox.Text.Trim();
        if (folder.Length == 0 || name.Length == 0) return null;
        return Path.Combine(folder, IsoFileSystem.Sanitize(name) + (_disc?.ImageExtension ?? ".bin"));
    }

    private void Target_Changed(object sender, TextChangedEventArgs e)
    {
        if (sender == NameBox && !_settingName) _nameIsAuto = false;
        if (IsLoaded) UpdateTarget();
    }

    private void UpdateTarget()
    {
        if (_engine != null) return;
        string? path = TargetPath();
        bool resume = path != null && File.Exists(path) && File.Exists(path + ".map");
        StartButton.Content = ModeSelected.IsChecked == true ? Loc.T("RescueSelected") : ModeMedia.IsChecked == true ? Loc.T("RescueMediaButton") : Loc.T(resume ? "Continue" : "Start");
        RetryButton.IsEnabled = false;
        SpaceText.Text = "";
        if (path == null) return;
        try
        {
            string root = Path.GetPathRoot(Path.GetFullPath(path))!;
            long free = new DriveInfo(root).AvailableFreeSpace;
            long need = _disc?.TotalBytes ?? 0;
            if (resume) need = Math.Max(0, need - new FileInfo(path).Length);
            SpaceText.Text = Loc.T("FreeSpace", FormatBytes(free), _disc == null ? "—" : FormatBytes(need));
            SpaceText.Foreground = (System.Windows.Media.Brush)FindResource(free < need ? "BadBrush" : "MutedBrush");
        }
        catch { /* folder may not exist yet */ }

        _previewMap = null;
        if (resume && _disc != null)
        {
            try
            {
                var map = SectorMap.Load(path + ".map");
                if (map.Count == _disc.TotalSectors)
                {
                    _previewMap = map;
                    RetryButton.IsEnabled = map.CountOf(SectorState.Bad) > 0;
                    ShowStats(map, null);
                }
            }
            catch { }
        }
        if (_disc != null && _image == null) Disc.Render(_previewMap, 0);
        RefreshTreeStatus();
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = Loc.T("SaveTo"), InitialDirectory = Directory.Exists(FolderBox.Text) ? FolderBox.Text : null };
        if (dlg.ShowDialog(this) == true) FolderBox.Text = dlg.FolderName;
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (ModeSelected.IsChecked == true) RescueSelected_Click(sender, e);
        else if (ModeMedia.IsChecked == true) _ = RescueMediaAsync();
        else _ = RunRescueAsync(retryBad: false);
    }

    // ---- whole disc vs. picked files/folders

    private List<IsoEntry> PickedFiles() =>
        _rootNode?.SelfAndDescendants().Where(n => n.IsChecked == true && !n.Entry.IsDirectory).Select(n => n.Entry).ToList() ?? [];

    private void UpdateSelectionText()
    {
        var picked = PickedFiles();
        SelectionText.Text = picked.Count == 0 ? (ModeSelected.IsChecked == true ? Loc.T("NothingPicked") : "")
            : Loc.T("PickedCount", picked.Count, FormatBytes(picked.Sum(f => f.DataSize)));
        if (ModeSelected.IsChecked == true && picked.Count > 0) Disc.SetHighlight(picked.Select(f => (f.Extent, f.SectorCount)));
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        StartButton.Content = ModeSelected.IsChecked == true ? Loc.T("RescueSelected") : ModeMedia.IsChecked == true ? Loc.T("RescueMediaButton")
            : Loc.T(StartButton.Content as string == Loc.T("Continue") ? "Continue" : "Start");
        if (ModeMedia.IsChecked == true) Disc.SetHighlight(MediaRanges(includeMetadata: false));
        if (ModeWhole.IsChecked == true) { Disc.SetHighlight([]); UpdateTarget(); }
        UpdateSelectionText();
    }

    private void PickInContents_Click(object sender, RoutedEventArgs e)
    {
        ModeSelected.IsChecked = true;
        Tabs.SelectedIndex = 1;
    }

    // ---- right-click on a file or folder

    private FileNode? _menuNode;

    private void FileTree_PreviewRightClick(object sender, MouseButtonEventArgs e)
    {
        // select the item under the mouse so the menu acts on it
        var item = FindParent<TreeViewItem>(e.OriginalSource as DependencyObject);
        if (item != null) { item.IsSelected = true; item.Focus(); }
    }

    private static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        return d as T;
    }

    private void TreeMenu_Opened(object sender, RoutedEventArgs e)
    {
        _menuNode = FileTree.SelectedItem as FileNode;
        bool has = _menuNode != null;
        MenuRescueThis.IsEnabled = MenuToggleThis.IsEnabled = has && _engine == null;
        MenuOpenThis.IsEnabled = has && _menuNode!.Entry.IsDirectory == false;
        if (!has) { MenuRescueThis.Header = Loc.T("RescueSelected"); return; }
        MenuRescueThis.Header = Loc.T(_menuNode!.Entry.IsDirectory ? "MenuRescueFolder" : "MenuRescueFile", _menuNode.Name);
        MenuToggleThis.Header = Loc.T(_menuNode.IsChecked == true ? "MenuUnpick" : "MenuPick");
    }

    private void MenuRescueThis_Click(object sender, RoutedEventArgs e)
    {
        if (_menuNode == null || _rootNode == null) return;
        foreach (var n in _rootNode.Children) n.IsChecked = false;
        _menuNode.IsChecked = true;
        RescueSelected_Click(sender, e);
    }

    private void MenuToggleThis_Click(object sender, RoutedEventArgs e)
    {
        if (_menuNode != null) _menuNode.IsChecked = _menuNode.IsChecked != true;
        UpdateSelectionText();
    }

    private void MenuOpenThis_Click(object sender, RoutedEventArgs e) => FileTree_DoubleClick(sender, null!);
    private void RetryBad_Click(object sender, RoutedEventArgs e) => _ = RunRescueAsync(retryBad: true, _scopeRanges);
    private List<(long Start, long Count)>? _scopeRanges; // set while only picked files are being rescued
    private void Stop_Click(object sender, RoutedEventArgs e) => _rescueCts?.Cancel();

    private async Task<bool> RunRescueAsync(bool retryBad, List<(long Start, long Count)>? ranges = null)
    {
        if (_engine != null) return false;
        if (_driveLetter == null || _disc == null) { Warn(Loc.T("InsertDisc")); return false; }
        string? path = TargetPath();
        if (path == null) { Warn(Loc.T("ChooseFolderFirst")); return false; }
        try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); }
        catch (Exception ex) { Warn(ex.Message); return false; }

        if (File.Exists(path) && !File.Exists(path + ".map"))
        {
            string q = Loc.I.English ? $"{Path.GetFileName(path)} already exists. Overwrite it?" : $"{Path.GetFileName(path)} zaten var. Üzerine yazılsın mı?";
            if (MessageBox.Show(this, q, "DVD 360", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return false;
            File.Delete(path);
        }
        try
        {
            long free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;
            long need = _disc.TotalBytes - (File.Exists(path) ? new FileInfo(path).Length : 0);
            if (free < need) { Warn(Loc.T("NotEnoughSpace")); return false; }
        }
        catch { }

        _detectVersion++;
        CloseLiveContent(clearTree: false);
        AppSettings.Save(new AppSettings { Folder = FolderBox.Text.Trim(), VideoFolder = VideoFolderBox.Text.Trim() });
        if (_fs?.Source is DiscImage img && string.Equals(img.Path, path, StringComparison.OrdinalIgnoreCase)) { CloseImage(); ShowContent(null, "", null); }

        ScsiOpticalDevice? dev = null;
        try
        {
            string letter = _driveLetter;
            dev = await Task.Run(() => ScsiOpticalDevice.Open(letter));
            _engine = new RescueEngine(dev, path, new RescueOptions
            {
                Retries = (int)RetrySlider.Value,
                SpeedMultiplier = (string)((ComboBoxItem)SpeedBox.SelectedItem).Tag is "auto" ? 0 : int.Parse((string)((ComboBoxItem)SpeedBox.SelectedItem).Tag),
                AutoSlowDown = (string)((ComboBoxItem)SpeedBox.SelectedItem).Tag is "auto",
                VerifyCdSectors = VerifyBox.IsChecked == true,
            });
        }
        catch (Exception ex)
        {
            dev?.Dispose();
            _engine = null;
            Warn(ex.Message);
            _ = DetectDiscAsync();
            return false;
        }

        var engine = _engine;
        _scopeRanges = ranges;
        if (ranges != null) Disc.SetHighlight(ranges);
        engine.Log += m => Dispatcher.BeginInvoke(() => AddLog(m));
        if (engine.IsResume) AddLog(Loc.T("Resuming"));
        _rescueCts = new CancellationTokenSource();
        SetRescueBusy(true);
        Disc.SetMode(DiscViewMode.Reading, _disc.VolumeLabel);
        _uiTimer.Start();
        string? finalMessage = null;
        bool error = false, ok = false;
        try
        {
            await (retryBad ? engine.RetryBadAsync(_rescueCts.Token, ranges) : engine.RunAsync(_rescueCts.Token, ranges));
            ok = true;
            finalMessage = ranges != null ? Loc.T("SelectedRead")
                : engine.BadSectors == 0
                ? Loc.T("DoneAllGood")
                : Loc.T("DoneSomeBad", engine.BadSectors.ToString("N0"), FormatBytes(engine.BadSectors * engine.Info.SectorSize));
        }
        catch (OperationCanceledException) { finalMessage = Loc.T("Stopped"); }
        catch (Exception ex) { finalMessage = ex.Message; error = true; }
        finally
        {
            _uiTimer.Stop();
            UpdateRescueUi();
            Taskbar.ProgressState = System.Windows.Shell.TaskbarItemProgressState.None;
            dev.Dispose();
            _engine = null;
            _rescueCts.Dispose();
            _rescueCts = null;
            SetRescueBusy(false);
        }

        AddLog(finalMessage);
        _previewMap = engine.Map;
        RefreshTreeStatus();
        bool complete = engine.IsComplete;
        if (ranges != null)
        {
            // Only the picked files were read: the caller extracts them. Keep the disc highlight.
            Disc.SetMode(DiscViewMode.Idle, _disc?.VolumeLabel ?? "");
            Disc.Render(engine.Map, engine.Stats.CurrentLba);
            RetryButton.IsEnabled = engine.BadSectors > 0;
            StartButton.Content = Loc.T(complete ? "Start" : "Continue");
            if (error) Warn(finalMessage!);
            return ok;
        }
        Disc.SetMode(complete ? DiscViewMode.Done : DiscViewMode.Idle, _disc?.VolumeLabel ?? "");
        Disc.Render(engine.Map, engine.Stats.CurrentLba);
        RetryButton.IsEnabled = engine.BadSectors > 0;
        StartButton.Content = Loc.T(complete ? "Start" : "Continue");
        if (complete)
        {
            LoadImage(path);
            if (_fs?.HasForm2Files == true) AddDiscVideos(switchTab: false);
            if (AutoIsoBox.IsChecked == true && _fs?.Source is DiscImage done)
            {
                string iso = DefaultIsoPath(done);
                if (await CreateIso(done, iso)) AddLog(Loc.T("IsoSavedTo", iso));
            }
        }
        if (ok) WriteReport(path, engine.Info, engine.Map, open: false);
        if (_quiet) return ok;
        if (error) { Warn(finalMessage); return ok; }
        if (ok) await FinishedAsync(Loc.T("RescueDoneTitle"), finalMessage!, discJob: true);
        else MessageBox.Show(this, finalMessage, "DVD 360", MessageBoxButton.OK, MessageBoxImage.Information);
        return ok;
    }

    private async void RescueSaveIso_Click(object sender, RoutedEventArgs e)
    {
        if (_engine != null) { Warn(Loc.T("StopFirst")); return; }
        string? path = TargetPath();
        if (path == null || !File.Exists(path)) { Warn(Loc.T("ReadFirst")); return; }

        // A partial image is still useful: unread parts are left empty.
        try
        {
            var map = File.Exists(path + ".map") ? SectorMap.Load(path + ".map") : null;
            if (map != null && map.CountOf(SectorState.Ok) + map.CountOf(SectorState.Skipped) < map.Count)
            {
                double pct = 100.0 * map.CountOf(SectorState.Ok) / Math.Max(1, map.Count - map.CountOf(SectorState.Skipped));
                if (MessageBox.Show(this, Loc.T("IsoPartial", pct), "DVD 360", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            }
        }
        catch { }

        if (_fs?.Source is not DiscImage img || !string.Equals(img.Path, path, StringComparison.OrdinalIgnoreCase))
        {
            CloseLiveContent(clearTree: true);
            LoadImage(path);
        }
        if (_fs?.Source is not DiscImage image) return;
        var dlg = new SaveFileDialog
        {
            Filter = "ISO (*.iso)|*.iso",
            FileName = Path.GetFileName(DefaultIsoPath(image)),
            InitialDirectory = Path.GetDirectoryName(image.Path),
        };
        if (dlg.ShowDialog(this) != true) return;
        Tabs.SelectedIndex = 1; // the contents tab shows the progress and the files
        if (await CreateIso(image, dlg.FileName))
        {
            AddLog(Loc.T("IsoSavedTo", dlg.FileName));
            Process.Start("explorer.exe", $"/select,\"{dlg.FileName}\"");
        }
    }

    private void SetRescueBusy(bool busy)
    {
        OneClickButton.IsEnabled = !busy && !_quiet;
        ReportButton.IsEnabled = !busy;
        RescueSelectedButton.IsEnabled = !busy;
        RescueIsoButton.IsEnabled = !busy;
        StartButton.IsEnabled = !busy;
        RetryButton.IsEnabled = false;
        StopButton.IsEnabled = busy;
        FolderBox.IsEnabled = NameBox.IsEnabled = DriveBox.IsEnabled = SpeedBox.IsEnabled = RetrySlider.IsEnabled = VerifyBox.IsEnabled = !busy;
    }

    private void UpdateRescueUi()
    {
        var engine = _engine;
        if (engine == null) return;
        long useful = Math.Max(1, engine.Map.Count - engine.SkippedSectors);
        Taskbar.ProgressState = engine.Map.CountOf(SectorState.NeedsRetry) + engine.BadSectors > useful / 10
            ? System.Windows.Shell.TaskbarItemProgressState.Paused   // yellow: many errors
            : System.Windows.Shell.TaskbarItemProgressState.Normal;
        Taskbar.ProgressValue = (double)engine.OkSectors / useful;
        ShowStats(engine.Map, engine.Stats);
        if (_scopeRanges != null)
        {
            long sOk = 0, sErr = 0, sUnread = 0;
            foreach (var (start, count) in _scopeRanges)
            {
                var t = engine.Map.Summarize(start, count);
                sOk += t.Ok; sErr += t.Bad + t.Retry; sUnread += t.Pending;
            }
            PassText.Text = Loc.T("Pass_" + engine.Stats.Pass) + "  •  " + Loc.T("SelectedOnly");
            ShowFillAndErrors(sOk, sErr, sUnread, Math.Max(1, sOk + sErr + sUnread), engine.Map.SectorSize);
        }
        Disc.Render(engine.Map, engine.Stats.CurrentLba);
        if (++_treeTick % 5 == 0) RefreshTreeStatus();
    }

    private void ShowStats(SectorMap map, RescueStats? stats)
    {
        long total = map.Count, ok = map.CountOf(SectorState.Ok), skipped = map.CountOf(SectorState.Skipped);
        long errors = map.CountOf(SectorState.Bad) + map.CountOf(SectorState.NeedsRetry);
        long unread = map.CountOf(SectorState.Untried);
        long pending = unread + map.CountOf(SectorState.NeedsRetry);
        ShowFillAndErrors(ok, errors, unread, Math.Max(1, total - skipped), map.SectorSize);
        if (stats == null) return;
        PassText.Text = Loc.T("Pass_" + stats.Pass);
        double bps = stats.BytesPerSecond;
        double oneX = map.SectorSize == CdSector.RawSize ? 176400 : 1385000;
        StatSpeed.Text = bps > 0 ? $"{bps / 1048576:N1} MB/s ({bps / oneX:N1}x)" : "—";
        StatElapsed.Text = stats.Elapsed.ToString(@"hh\:mm\:ss");
        StatEta.Text = bps > 0 && stats.Pass == RescuePass.FastCopy ? TimeSpan.FromSeconds(pending * map.SectorSize / bps).ToString(@"hh\:mm\:ss") : "—";
    }

    /// <summary>Fill (read) and error area are shown separately: two numbers, a three-colour bar and two rings.</summary>
    private void ShowFillAndErrors(long ok, long errors, long unread, long useful, int sectorSize)
    {
        double fill = 100.0 * ok / useful, err = 100.0 * errors / useful, rest = Math.Max(0, 100 - fill - err);
        SegOk.Width = new GridLength(fill, GridUnitType.Star);
        SegErr.Width = new GridLength(err, GridUnitType.Star);
        SegRest.Width = new GridLength(rest, GridUnitType.Star);
        PercentText.Text = Loc.T("FillLine", fill);
        StatOk.Text = Loc.T("Pct", fill);
        StatBad.Text = Loc.T("Pct", err);
        StatPending.Text = $"{unread:N0}";
        BarLegend.Inlines.Clear();
        void Part(string text, string brush)
        {
            BarLegend.Inlines.Add(new System.Windows.Documents.Run("■ ") { Foreground = (System.Windows.Media.Brush)FindResource(brush) });
            BarLegend.Inlines.Add(new System.Windows.Documents.Run(text + "     "));
        }
        Part(Loc.T("BarFilled", fill) + $" ({FormatBytes(ok * sectorSize)})", "OkBrush");
        Part(Loc.T("BarErrors", err) + $" ({errors:N0} {Loc.T("Sectors")})", "RetryBrush");
        Part(Loc.T("BarUnread", rest), "MutedBrush");
    }

    private void AddLog(string message)
    {
        LogList.Items.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        if (LogList.Items.Count > 2000) LogList.Items.RemoveAt(0);
        LogList.ScrollIntoView(LogList.Items[^1]);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        string folder = FolderBox.Text.Trim();
        if (!Directory.Exists(folder)) return;
        string? path = TargetPath();
        if (path != null && File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
        else Process.Start("explorer.exe", $"\"{folder}\"");
    }

    // =====================================================================
    // Contents: browse, extract, ISO, virtual drive
    // =====================================================================

    private void ShowContent(IsoFileSystem? fs, string source, string? error)
    {
        FileTree.ItemsSource = null;
        _rootNode = null;
        _filesByExtent = new();
        FileInfoCard.Visibility = Visibility.Collapsed;
        Disc.SetHighlight([]);
        ContentSource.Text = source;
        ContentSummary.Text = "";
        if (fs == null)
        {
            ContentEmpty.Visibility = Visibility.Visible;
            if (error != null) { ContentEmpty.Text = Loc.T("NoFs", error); }
            else ContentEmpty.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding("[NoContent]") { Source = Loc.I });
            return;
        }
        ContentEmpty.Visibility = Visibility.Collapsed;
        var root = new FileNode(fs.Root, fs.Source.KnowsDamage);
        _rootNode = root;
        foreach (var n in root.SelfAndDescendants())
            n.PropertyChanged += (_, a) => { if (a.PropertyName == nameof(FileNode.IsChecked)) Dispatcher.BeginInvoke(UpdateSelectionText, DispatcherPriority.Background); };
        _filesByExtent = fs.Files.Where(f => f.SectorCount > 0).OrderBy(f => f.Extent).ToList();
        root.Refresh(StatusMap);
        FileTree.ItemsSource = root.Children;
        var files = fs.Files.ToList();
        int dirs = fs.Root.Descendants().Count(d => d.IsDirectory);
        ContentSummary.Text = $"{fs.VolumeId}  •  " + Loc.T("Files", files.Count, dirs, FormatBytes(files.Sum(f => f.DataSize)))
            + (fs.Source.KnowsDamage && files.Any(f => f.BadSectors > 0) ? Loc.T("DamagedFiles", files.Count(f => f.BadSectors > 0)) : "");
    }

    private void LoadImage(string path)
    {
        try
        {
            CloseImage();
            _image = DiscImage.Open(path);
            _fs = IsoFileSystem.TryOpen(_image, out string err);
            ShowContent(_fs, Loc.T("SourceImage", Path.GetFileName(path)), _fs == null ? err : null);
            if (_image.Map != null && _engine == null && _liveDevice == null) Disc.Render(_image.Map, 0);
        }
        catch (Exception ex) { Warn(ex.Message); }
    }

    private void CloseImage()
    {
        if (_fs?.Source is DiscImage) _fs = null;
        _image?.Dispose();
        _image = null;
    }

    private void OpenImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Disc images (*.bin;*.iso)|*.bin;*.iso|All files|*.*", InitialDirectory = Directory.Exists(FolderBox.Text) ? FolderBox.Text : null };
        if (dlg.ShowDialog(this) == true)
        {
            CloseLiveContent(clearTree: true);
            LoadImage(dlg.FileName);
        }
    }

    private bool RequireImage()
    {
        if (_fs?.Source is DiscImage) return true;
        if (_image != null && _fs == null) { Warn(Loc.T("NoFs", "")); return false; }
        MessageBox.Show(this, Loc.T("RescueFirst"), "DVD 360", MessageBoxButton.OK, MessageBoxImage.Information);
        Tabs.SelectedIndex = 0;
        return false;
    }

    private async Task RunContentTask(Func<IProgress<(long Done, long Total, string Name)>, CancellationToken, string> work)
    {
        if (_contentBusy) return;
        _contentBusy = true;
        ExtractButton.IsEnabled = IsoButton.IsEnabled = MountButton.IsEnabled = false;
        ContentProgress.Visibility = Visibility.Visible;
        ContentProgress.Value = 0;
        ContentStatus.Text = Loc.T("Working");
        var progress = new Progress<(long Done, long Total, string Name)>(p =>
        {
            ContentProgress.Value = 1000.0 * p.Done / Math.Max(1, p.Total);
            if (p.Name.Length > 0) ContentStatus.Text = p.Name;
        });
        try
        {
            string result = await Task.Run(() => work(progress, CancellationToken.None));
            ContentStatus.Text = result;
        }
        catch (Exception ex)
        {
            ContentStatus.Text = ex.Message;
            Warn(ex.Message);
        }
        finally
        {
            _contentBusy = false;
            ContentProgress.Visibility = Visibility.Collapsed;
            ExtractButton.IsEnabled = IsoButton.IsEnabled = MountButton.IsEnabled = true;
        }
    }

    private async void Extract_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireImage()) return;
        var dlg = new OpenFolderDialog { Title = Loc.T("Extract"), InitialDirectory = FolderBox.Text };
        if (dlg.ShowDialog(this) != true) return;
        var fs = _fs!;
        string target = Path.Combine(dlg.FolderName, IsoFileSystem.Sanitize(string.IsNullOrWhiteSpace(fs.VolumeId) ? "Disk" : fs.VolumeId));
        await RunContentTask((p, ct) =>
        {
            var r = fs.ExtractAll(target, p, ct);
            return Loc.T("Extracted", r.Files, r.Damaged.Count) + "  →  " + target;
        });
        if (Directory.Exists(target)) Process.Start("explorer.exe", $"\"{target}\"");
    }

    private string DefaultIsoPath(DiscImage img) =>
        img.IsRawCd ? Path.ChangeExtension(img.Path, ".iso") : img.Path;

    private async void SaveIso_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireImage()) return;
        var img = (DiscImage)_fs!.Source;
        var dlg = new SaveFileDialog
        {
            Filter = "ISO (*.iso)|*.iso",
            FileName = Path.GetFileName(DefaultIsoPath(img)),
            InitialDirectory = Path.GetDirectoryName(img.Path),
        };
        if (dlg.ShowDialog(this) != true) return;
        await CreateIso(img, dlg.FileName);
    }

    private async Task<bool> CreateIso(DiscImage img, string isoPath)
    {
        var fs = _fs;
        bool ok = false;
        await RunContentTask((p, ct) =>
        {
            var method = IsoExport.Create(img, fs, isoPath, p, ct);
            ok = true;
            return Loc.T(method switch { IsoExport.Method.Exact => "IsoExact", IsoExport.Method.Rebuilt => "IsoRebuilt", _ => "IsoCopy" }) + "  →  " + isoPath;
        });
        return ok;
    }

    private async void Mount_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireImage()) return;
        var img = (DiscImage)_fs!.Source;
        string iso = DefaultIsoPath(img);
        if (!File.Exists(iso) && !await CreateIso(img, iso)) return;
        try
        {
            ContentStatus.Text = Loc.T("Working");
            string? letter = await VirtualDrive.MountAsync(iso);
            _mountedIso = iso;
            UnmountButton.IsEnabled = true;
            ContentStatus.Text = Loc.T("Mounted", letter ?? "?");
            if (letter != null) Process.Start("explorer.exe", letter + ":\\");
        }
        catch (Exception ex) { Warn(ex.Message); }
    }

    private async void Unmount_Click(object sender, RoutedEventArgs e)
    {
        if (_mountedIso == null) return;
        try { await VirtualDrive.DismountAsync(_mountedIso); } catch (Exception ex) { Warn(ex.Message); }
        _mountedIso = null;
        UnmountButton.IsEnabled = false;
        ContentStatus.Text = "";
    }

    private async void FileTree_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileTree.SelectedItem is not FileNode { Entry.IsDirectory: false } node || _fs == null || _contentBusy) return;
        if (_fs.Source is LiveDiscSource && _liveDevice == null) { MessageBox.Show(this, Loc.T("RescueFirst"), "DVD 360"); return; }
        var fs = _fs;
        string dir = Path.Combine(Path.GetTempPath(), "DVD360", Guid.NewGuid().ToString("N")[..8]);
        string file = Path.Combine(dir, node.Entry.OutputName);
        await RunContentTask((p, ct) =>
        {
            Directory.CreateDirectory(dir);
            using (var o = File.Create(file)) fs.CopyFile(node.Entry, o, ct);
            return file;
        });
        if (File.Exists(file)) Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
    }

    // ---- rescue only videos and music

    private static bool IsMediaFile(IsoEntry f) => f.IsForm2 || IsVideo(f.Name) || IsAudio(f.Name);
    internal static bool IsAudio(string name) => Path.GetExtension(name).ToLowerInvariant() is ".mp3" or ".wav" or ".wma" or ".flac" or ".m4a" or ".ogg" or ".aac" or ".mp2";

    /// <summary>Sectors of all video/music files and audio-CD tracks (plus the file system, so files can be found later).</summary>
    private List<(long Start, long Count)> MediaRanges(bool includeMetadata)
    {
        var ranges = new List<(long Start, long Count)>();
        if (_fs != null)
        {
            ranges.AddRange(_fs.Files.Where(IsMediaFile).Select(f => (f.Extent, f.SectorCount)));
            if (includeMetadata && ranges.Count > 0) ranges.AddRange(_fs.MetadataRanges());
        }
        if (_disc != null) ranges.AddRange(AudioTracks.Find(_disc).Select(t => (t.StartLba, t.Sectors)));
        return ranges;
    }

    private async Task RescueMediaAsync()
    {
        if (_disc == null) { Warn(Loc.T("InsertDisc")); return; }
        var ranges = MediaRanges(includeMetadata: true);
        if (ranges.Count == 0) { Warn(Loc.T("NoMedia")); return; }
        AddLog(Loc.T("MediaStart", FormatBytes(ranges.Sum(r => r.Count) * _disc.SectorSize)));
        bool quiet = _quiet;
        _quiet = true;
        string summary;
        try
        {
            if (!await RunRescueAsync(retryBad: false, ranges)) return;
            string image = TargetPath()!;
            LoadImage(image);
            string baseDir = Path.Combine(Path.GetDirectoryName(image)!, Path.GetFileNameWithoutExtension(image));
            int songs = 0;
            AddDiscVideos(switchTab: false);
            if (_image != null && AudioTracks.Find(_image.Info).Count > 0)
                songs = await ExportAudioAsync(_image, Path.Combine(baseDir, Loc.T("MusicFolder")), SelectedAudioFormat());
            if (_fs != null)
            {
                var musicFiles = _fs.Files.Where(f => IsAudio(f.Name)).ToList();
                if (musicFiles.Count > 0)
                {
                    var fs = _fs;
                    await RunContentTask((p, ct) => { fs.Extract(musicFiles, Path.Combine(baseDir, Loc.T("MusicFolder")), p, ct); return ""; });
                    songs += musicFiles.Count;
                }
            }
            int videos = _videos.Count(v => !v.Done);
            Tabs.SelectedIndex = videos > 0 ? 2 : 0;
            summary = Loc.T("MediaDone", videos, songs);
        }
        finally { _quiet = quiet; }
        if (!_quiet) await FinishedAsync(Loc.T("RescueDoneTitle"), summary, discJob: true);
    }

    private AudioFormat SelectedAudioFormat() => (AudioFormat)Math.Max(0, AudioFormatBox.SelectedIndex);

    private async void AudioOnly_Click(object sender, RoutedEventArgs e)
    {
        if (_videoCts != null) return;
        string? ffmpeg = VideoConverter.FindFfmpeg();
        if (ffmpeg == null) { Warn(Loc.T("FfmpegMissing")); return; }
        var items = VideoList.SelectedItem is VideoItem sel ? [sel] : _videos.ToList();
        if (items.Count == 0) { Warn(Loc.T("NoVideosInList")); return; }
        string outDir = VideoFolderBox.Text.Trim();
        Directory.CreateDirectory(outDir);
        _videoCts = new CancellationTokenSource();
        AudioOnlyButton.IsEnabled = ConvertButton.IsEnabled = false;
        StopVideoButton.IsEnabled = true;
        int ok = 0;
        try
        {
            foreach (var v in items)
            {
                string? temp = null;
                try
                {
                    string input = v.FilePath ?? "";
                    var parts = v.Parts ?? (v.Entry != null ? [v.Entry] : null);
                    if (parts != null && v.FileSystem != null)
                    {
                        v.Status = Loc.T("Preparing", "");
                        temp = Path.Combine(outDir, "~" + Guid.NewGuid().ToString("N")[..6] + (v.Parts != null ? ".vob" : Path.GetExtension(parts[0].OutputName)));
                        var fs = v.FileSystem; var ct = _videoCts.Token;
                        await Task.Run(() => { using var o = File.Create(temp); foreach (var part in parts) fs.CopyFile(part, o, ct); }, ct);
                        input = temp;
                    }
                    string output = UniquePath(Path.Combine(outDir, IsoFileSystem.Sanitize(Path.GetFileNameWithoutExtension(v.Name)) + ".mp3"));
                    v.Status = Loc.T("Converting", "MP3");
                    await VideoConverter.ConvertAudioAsync(ffmpeg, input, output, AudioFormat.Mp3, _videoCts.Token);
                    v.Status = "♪ " + Path.GetFileName(output);
                    ok++;
                }
                catch (OperationCanceledException) { v.Status = Loc.T("Stopped"); throw; }
                catch (Exception ex) { v.Status = Loc.T("VideoFailed"); AddLog(ex.Message); }
                finally { if (temp != null) try { File.Delete(temp); } catch { } }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _videoCts.Dispose();
            _videoCts = null;
            AudioOnlyButton.IsEnabled = ConvertButton.IsEnabled = true;
            StopVideoButton.IsEnabled = false;
        }
        AddLog(Loc.T("Mp3Done", ok, outDir));
        if (ok > 0 && Directory.Exists(outDir)) Process.Start("explorer.exe", $"\"{outDir}\"");
    }

    // ---- rescue only the picked files

    private void SelectAll_Click(object sender, RoutedEventArgs e) { foreach (var n in _rootNode?.Children ?? []) n.IsChecked = true; }
    private void SelectNone_Click(object sender, RoutedEventArgs e) { foreach (var n in _rootNode?.Children ?? []) n.IsChecked = false; }

    private async void RescueSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_rootNode == null || _fs == null) { Warn(Loc.T("NoContent")); return; }
        var picked = _rootNode.SelfAndDescendants().Where(n => n.IsChecked == true && !n.Entry.IsDirectory).Select(n => n.Entry).ToList();
        if (picked.Count == 0) { Warn(Loc.T("PickFiles")); return; }
        var dlg = new OpenFolderDialog { Title = Loc.T("RescueSelected"), InitialDirectory = Directory.Exists(FolderBox.Text) ? FolderBox.Text : null };
        if (dlg.ShowDialog(this) != true) return;
        string outDir = dlg.FolderName;
        var paths = picked.Select(f => f.FullPath).ToList();
        var fs = _fs;

        // Everything already read (e.g. an opened image)? Then just copy the files out.
        bool needDisc = !(fs.Source is DiscImage img0 && img0.Map != null && picked.All(f => img0.Map.Summarize(f.Extent, f.SectorCount) is var t && t.Pending + t.Retry == 0));
        if (needDisc)
        {
            if (_disc == null) { Warn(Loc.T("InsertDisc")); return; }
            var ranges = picked.Select(f => (f.Extent, f.SectorCount)).Concat(fs.MetadataRanges()).ToList();
            AddLog(Loc.T("SelectedStart", picked.Count, FormatBytes(picked.Sum(f => f.DataSize))));
            Tabs.SelectedIndex = 0;
            if (!await RunRescueAsync(retryBad: false, ranges)) return;
            string? path = TargetPath();
            if (path == null) return;
            LoadImage(path);
            fs = _fs!;
            Tabs.SelectedIndex = 1;
        }

        var entries = paths.Select(p => fs.Find(p)).Where(x => x != null).Cast<IsoEntry>().ToList();
        string target = Path.Combine(outDir, IsoFileSystem.Sanitize(string.IsNullOrWhiteSpace(fs.VolumeId) ? "Disk" : fs.VolumeId));
        IsoFileSystem.ExtractReport? report = null;
        await RunContentTask((p, ct) =>
        {
            report = fs.Extract(entries, target, p, ct);
            return Loc.T("Extracted", report.Files, report.Damaged.Count) + "  →  " + target;
        });
        if (report == null) return;
        AddLog(Loc.T("Extracted", report.Files, report.Damaged.Count) + "  →  " + target);
        if (Directory.Exists(target)) Process.Start("explorer.exe", $"\"{target}\"");
        if (report.Damaged.Count > 0)
            MessageBox.Show(this, Loc.T("SelectedDamaged", string.Join("\n", report.Damaged.Select(d => $"• {d.OutputName} ({d.BadSectors})"))), "DVD 360", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // ---- content status on the disc

    private void RefreshTreeStatus()
    {
        if (_rootNode == null) return;
        _rootNode.Refresh(StatusMap);
        if (FileTree.SelectedItem is FileNode n) ShowFileInfo(n);
    }

    private void FileTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is FileNode n)
        {
            Disc.SetHighlight(n.Ranges());
            ShowFileInfo(n);
        }
        else
        {
            Disc.SetHighlight([]);
            FileInfoCard.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowFileInfo(FileNode n)
    {
        FileInfoCard.Visibility = Visibility.Visible;
        FileInfoName.Text = n.Icon + "  " + (n.Entry.IsDirectory ? n.Entry.FullPath : n.Name);
        string where = n.Entry.SectorCount > 0 && !n.Entry.IsDirectory ? $"  •  LBA {n.Entry.Extent:N0}–{n.Entry.Extent + n.Entry.SectorCount - 1:N0}" : "";
        FileInfoState.Text = (n.StateText.Length > 0 ? n.StateText : Loc.T("StateUnknown")) + where;
        FileInfoState.Foreground = n.StateBrush;
    }

    private IsoEntry? FileAt(long lba)
    {
        int lo = 0, hi = _filesByExtent.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_filesByExtent[mid].Extent <= lba) { found = mid; lo = mid + 1; } else hi = mid - 1;
        }
        if (found < 0) return null;
        var f = _filesByExtent[found];
        return lba < f.Extent + f.SectorCount ? f : null;
    }

    private void Disc_SectorHovered(long? lba)
    {
        if (lba == null || (_disc == null && _image == null)) { Disc.HoverContent = null; return; }
        var file = FileAt(lba.Value);
        string head = file != null ? (file.Parent?.Parent == null ? file.OutputName : file.Parent.FullPath + "\\" + file.OutputName) : Loc.T("NoFileHere");
        string state = "";
        var map = StatusMap;
        if (file != null && map != null)
        {
            var t = map.Summarize(file.Extent, file.SectorCount);
            long total = Math.Max(1, t.Ok + t.Bad + t.Retry + t.Pending);
            long errors = t.Bad + t.Retry;
            double pct = 100.0 * t.Ok / total;
            state = "\n" + (errors > 0 ? Loc.T("FileFillErr", pct, errors) : Loc.T("FileFill", pct));
        }
        else if (map != null && lba.Value < map.Count)
        {
            state = "\n" + Loc.T("SectorState_" + map[lba.Value]);
        }
        Disc.HoverContent = $"{head}\nLBA {lba:N0}{state}";
    }

    // =====================================================================
    // Video → MP4
    // =====================================================================

    internal static bool IsVideo(string name) => VideoConverter.VideoExtensions.Contains(Path.GetExtension(name).ToLowerInvariant());
    internal static bool IsImage(string name) => Path.GetExtension(name).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif" or ".tif" or ".tiff";

    private void UpdateFfmpegStatus()
    {
        string? ff = VideoConverter.FindFfmpeg();
        FfmpegText.Text = ff != null ? "✔ " + Loc.T("FfmpegFound") + "  (" + ff + ")" : Loc.T("FfmpegMissing");
        FfmpegButton.Visibility = ff != null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void DownloadFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        FfmpegButton.IsEnabled = false;
        try
        {
            await VideoConverter.DownloadFfmpegAsync(new Progress<double>(p => FfmpegText.Text = Loc.T("Downloading", p * 100)), CancellationToken.None);
        }
        catch (Exception ex) { Warn(ex.Message); }
        FfmpegButton.IsEnabled = true;
        UpdateFfmpegStatus();
    }

    private void AddDiscVideos_Click(object sender, RoutedEventArgs e)
    {
        if (!RequireImage()) { Tabs.SelectedIndex = 0; return; }
        AddDiscVideos(switchTab: false);
    }

    private void AddDiscVideos(bool switchTab)
    {
        if (_fs == null) return;
        // DVD-Video: each title is VTS_xx_1.VOB, VTS_xx_2.VOB... (VTS_xx_0 is the menu) -> one video per title
        var vobParts = new HashSet<IsoEntry>();
        var titles = _fs.Files
            .Select(f => (File: f, M: System.Text.RegularExpressions.Regex.Match(f.Name, @"^VTS_(\d\d)_([1-9])\.VOB$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            .Where(x => x.M.Success)
            .GroupBy(x => x.M.Groups[1].Value)
            .OrderBy(g => g.Key);
        foreach (var t in titles)
        {
            var parts = t.OrderBy(x => int.Parse(x.M.Groups[2].Value)).Select(x => x.File).ToList();
            vobParts.UnionWith(_fs.Files.Where(f => f.Name.StartsWith($"VTS_{t.Key}_", StringComparison.OrdinalIgnoreCase)));
            string name = Loc.T("DvdTitle", int.Parse(t.Key));
            if (_videos.Any(v => v.Parts != null && v.Name == name)) continue;
            _videos.Add(new VideoItem
            {
                Name = name, Parts = parts, FileSystem = _fs,
                Detail = $"{_fs.VolumeId} — {string.Join(" + ", parts.Select(p => p.Name))} — {FormatBytes(parts.Sum(p => p.DataSize))}",
            });
        }
        foreach (var f in _fs.Files.Where(f => (f.IsForm2 || IsVideo(f.Name)) && !vobParts.Contains(f) && !f.Name.Equals("VIDEO_TS.VOB", StringComparison.OrdinalIgnoreCase)))
        {
            if (_videos.Any(v => v.Entry == f)) continue;
            if (_videos.Any(v => v.Entry != null && v.Name == f.OutputName && v.Detail.StartsWith(_fs.VolumeId))) continue;
            _videos.Add(new VideoItem
            {
                Name = f.OutputName, Entry = f, FileSystem = _fs,
                Detail = $"{_fs.VolumeId} — {f.FullPath} — {FormatBytes(f.DataSize)}" + (f.BadSectors > 0 ? $" — ⚠ {f.BadSectors}" : ""),
            });
        }
        if (switchTab) Tabs.SelectedIndex = 2;
    }

    private void AddVideos_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Multiselect = true, Filter = "Video|" + string.Join(";", VideoConverter.VideoExtensions.Select(x => "*" + x)) + "|*.*|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        foreach (var f in dlg.FileNames)
            _videos.Add(new VideoItem { Name = Path.GetFileName(f), FilePath = f, Detail = $"{f} — {FormatBytes(new FileInfo(f).Length)}" });
    }

    private void RemoveVideo_Click(object sender, RoutedEventArgs e)
    {
        if (VideoList.SelectedItem is VideoItem v && _videoCts == null) _videos.Remove(v);
    }

    private void ChooseVideoFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { InitialDirectory = Directory.Exists(VideoFolderBox.Text) ? VideoFolderBox.Text : null };
        if (dlg.ShowDialog(this) == true) VideoFolderBox.Text = dlg.FolderName;
    }

    private void OpenVideoFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(VideoFolderBox.Text)) Process.Start("explorer.exe", $"\"{VideoFolderBox.Text}\"");
    }

    private void StopVideo_Click(object sender, RoutedEventArgs e) => _videoCts?.Cancel();

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        string outDir = VideoFolderBox.Text.Trim();
        var (done, total, cancelled) = await ConvertVideosAsync(outDir);
        if (!cancelled && total > 0 && !_quiet)
            await FinishedAsync(Loc.T("VideosDoneTitle"), Loc.T("VideosDone", done, total, outDir), discJob: false);
    }

    /// <summary>Converts every waiting video in the list. Returns how many succeeded.</summary>
    private async Task<(int Done, int Total, bool Cancelled)> ConvertVideosAsync(string outDir)
    {
        if (_videoCts != null) return (0, 0, true);
        string? ffmpeg = VideoConverter.FindFfmpeg();
        if (ffmpeg == null) { Warn(Loc.T("FfmpegMissing")); return (0, 0, true); }
        var todo = _videos.Where(v => !v.Done).ToList();
        if (todo.Count == 0) return (0, 0, false);
        try { Directory.CreateDirectory(outDir); } catch (Exception ex) { Warn(ex.Message); return (0, 0, true); }

        var quality = (VideoQuality)QualityBox.SelectedIndex;
        bool hevc = CodecBox.SelectedIndex == 1;
        _videoCts = new CancellationTokenSource();
        ConvertButton.IsEnabled = false;
        StopVideoButton.IsEnabled = true;
        string suffix = quality switch { VideoQuality.Hd720 => "_720p", VideoQuality.FullHd1080 => "_1080p", _ => "" };
        int ok = 0, index = 0;
        bool cancelled = false;
        Taskbar.ProgressState = System.Windows.Shell.TaskbarItemProgressState.Normal;
        try
        {
            foreach (var v in todo)
            {
                string? temp = null;
                int i = index++;
                try
                {
                    string input = v.FilePath ?? "";
                    var parts = v.Parts ?? (v.Entry != null ? [v.Entry] : null);
                    if (parts != null && v.FileSystem != null)
                    {
                        v.Status = Loc.T("Preparing", "");
                        string ext = v.Parts != null ? ".vob" : Path.GetExtension(parts[0].OutputName);
                        temp = Path.Combine(outDir, "~" + Guid.NewGuid().ToString("N")[..6] + "_" + Path.GetFileNameWithoutExtension(v.Name) + ext);
                        var fs = v.FileSystem; var ct = _videoCts.Token;
                        await Task.Run(() => { using var o = File.Create(temp); foreach (var part in parts) fs.CopyFile(part, o, ct); }, ct);
                        input = temp;
                    }
                    string output = UniquePath(Path.Combine(outDir, IsoFileSystem.Sanitize(Path.GetFileNameWithoutExtension(v.Name)) + suffix + ".mp4"));
                    v.Status = Loc.T("Converting", "");
                    await VideoConverter.ConvertAsync(ffmpeg, new VideoJob { Input = input, Output = output, Quality = quality, UseHevc = hevc },
                        new Progress<double>(p =>
                        {
                            v.Progress = p; v.Status = $"{p * 100:N0}%";
                            Taskbar.ProgressValue = (i + p) / todo.Count;
                        }), m => Dispatcher.BeginInvoke(() => AddLog(m)), _videoCts.Token);
                    v.Done = true;
                    v.Progress = 1;
                    v.Status = "✔ " + Path.GetFileName(output);
                    ok++;
                }
                catch (OperationCanceledException) { v.Status = Loc.T("Stopped"); throw; }
                catch (Exception ex) { v.Status = Loc.T("VideoFailed"); AddLog(ex.Message); }
                finally { if (temp != null) try { File.Delete(temp); } catch { } }
            }
        }
        catch (OperationCanceledException) { cancelled = true; }
        finally
        {
            _videoCts.Dispose();
            _videoCts = null;
            ConvertButton.IsEnabled = true;
            StopVideoButton.IsEnabled = false;
            Taskbar.ProgressState = System.Windows.Shell.TaskbarItemProgressState.None;
        }
        return (ok, todo.Count, cancelled);
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 2; ; i++) { string p = Path.Combine(dir, $"{stem} ({i}){ext}"); if (!File.Exists(p)) return p; }
    }

    // =====================================================================
    // misc
    // =====================================================================

    /// <summary>Disc texts that code writes (XAML-bound texts follow the language by themselves).</summary>
    private void ShowDiscTexts()
    {
        if (_disc == null) { DiscStatus.Text = Loc.T(_driveLetter == null ? "NoDrive" : "InsertDisc"); return; }
        DiscStatus.Text = "✔ " + Loc.T("DiscDetected");
        InfoSize.Text = $"{FormatBytes(_disc.TotalBytes)}  ({_disc.TotalSectors:N0} {Loc.T("Sectors")})";
    }

    // =====================================================================
    // Long jobs: notification, eject, sleep / shutdown
    // =====================================================================

    private async Task FinishedAsync(string title, string message, bool discJob)
    {
        FinishActions.Notify(title, message);
        AddLog(message);
        var power = (PowerAction)FinishBox.SelectedIndex;
        if (discJob && EjectBox.IsChecked == true && _driveLetter != null)
        {
            string letter = _driveLetter;
            CloseLiveContent(clearTree: false);
            if (await Task.Run(() => ScsiOpticalDevice.Eject(letter))) AddLog(Loc.T("Ejected"));
        }
        if (power != PowerAction.None) FinishActions.RunPowerAction(this, power);
        else if (!discJob || EjectBox.IsChecked != true)
            MessageBox.Show(this, message, "DVD 360", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // =====================================================================
    // Report
    // =====================================================================

    private string? WriteReport(string imagePath, DiscInfo info, SectorMap map, bool open)
    {
        try
        {
            string html = Path.Combine(Path.GetDirectoryName(imagePath)!, Path.GetFileNameWithoutExtension(imagePath) + "_" + Loc.T("ReportFile") + ".html");
            string title = $"DVD 360 — {(string.IsNullOrWhiteSpace(info.VolumeLabel) ? Path.GetFileNameWithoutExtension(imagePath) : info.VolumeLabel)}";
            RescueReport.WriteHtml(html, info, map, _fs, title);
            if (open) Process.Start(new ProcessStartInfo(html) { UseShellExecute = true });
            return html;
        }
        catch (Exception ex) { AddLog(ex.Message); return null; }
    }

    private void Report_Click(object sender, RoutedEventArgs e)
    {
        string? path = TargetPath();
        var map = StatusMap;
        if (path == null || map == null || !File.Exists(path)) { Warn(Loc.T("ReadFirst")); return; }
        var info = _disc ?? (_fs?.Source as DiscImage)?.Info;
        if (info == null) { Warn(Loc.T("ReadFirst")); return; }
        string? html = WriteReport(path, info, map, open: true);
        if (html != null) AddLog(Loc.T("ReportSaved", html));
    }

    // =====================================================================
    // Audio CD -> WAV / FLAC
    // =====================================================================

    private async void SaveAudio_Click(object sender, RoutedEventArgs e)
    {
        var info = (_fs?.Source as DiscImage)?.Info ?? _image?.Info ?? _disc;
        if (info == null || AudioTracks.Find(info).Count == 0) { Warn(Loc.T("NoAudio")); return; }
        var img = _image ?? (_fs?.Source as DiscImage);
        if (img == null)
        {
            string? path = TargetPath();
            if (path == null || !File.Exists(path)) { Warn(Loc.T("RescueFirst")); return; }
            LoadImage(path);
            img = _image;
            if (img == null) return;
        }
        var dlg = new OpenFolderDialog { Title = Loc.T("SaveAudio"), InitialDirectory = Path.GetDirectoryName(img.Path) };
        if (dlg.ShowDialog(this) != true) return;
        var format = SelectedAudioFormat();
        if (format != AudioFormat.Wav && VideoConverter.FindFfmpeg() == null) { Warn(Loc.T("FfmpegMissing")); format = AudioFormat.Wav; }
        await ExportAudioAsync(img, Path.Combine(dlg.FolderName, Loc.T("MusicFolder")), format);
    }

    private async Task<int> ExportAudioAsync(DiscImage img, string folder, AudioFormat format)
    {
        List<string> files = [];
        await RunContentTask((p, ct) =>
        {
            files = AudioTracks.ExportWav(img, folder, p, ct);
            return Loc.T("AudioSaved", files.Count, folder);
        });
        string? ffmpeg = VideoConverter.FindFfmpeg();
        if (format != AudioFormat.Wav && ffmpeg != null)
        {
            foreach (var wav in files)
            {
                ContentStatus.Text = Loc.T("Converting", Path.GetFileName(wav));
                try
                {
                    await VideoConverter.ConvertAudioAsync(ffmpeg, wav, Path.ChangeExtension(wav, VideoConverter.Extension(format)), format, CancellationToken.None);
                    File.Delete(wav);
                }
                catch (Exception ex) { AddLog(ex.Message); }
            }
            ContentStatus.Text = Loc.T("AudioSaved", files.Count, folder);
        }
        if (Directory.Exists(folder)) Process.Start("explorer.exe", $"\"{folder}\"");
        return files.Count;
    }

    // =====================================================================
    // One click: rescue -> files -> videos -> music -> report -> finish
    // =====================================================================

    private async void OneClick_Click(object sender, RoutedEventArgs e)
    {
        if (_engine != null || _videoCts != null) return;
        if (_disc == null) { Warn(Loc.T("InsertDisc")); return; }
        if (MessageBox.Show(this, Loc.T("OneClickConfirm", FolderBox.Text.Trim()), "DVD 360", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK) return;

        bool hasVideo = _fs != null && _fs.Files.Any(f => f.IsForm2 || IsVideo(f.Name));
        if (hasVideo && VideoConverter.FindFfmpeg() == null &&
            MessageBox.Show(this, Loc.T("AskFfmpeg"), "DVD 360", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            try { await VideoConverter.DownloadFfmpegAsync(new Progress<double>(p => FfmpegText.Text = Loc.T("Downloading", p * 100)), CancellationToken.None); }
            catch (Exception ex) { AddLog(ex.Message); }
            UpdateFfmpegStatus();
        }

        ModeWhole.IsChecked = true;
        _quiet = true;
        OneClickButton.IsEnabled = false;
        var info = _disc;
        string? image = null;
        string summary;
        try
        {
            AddLog(Loc.T("OneClickStep", 1, Loc.T("TabRescue")));
            if (!await RunRescueAsync(retryBad: false)) return;
            image = TargetPath()!;
            LoadImage(image);
            string baseDir = Path.Combine(Path.GetDirectoryName(image)!, Path.GetFileNameWithoutExtension(image));
            int files = 0, videos = 0, songs = 0;

            if (_fs != null)
            {
                AddLog(Loc.T("OneClickStep", 2, Loc.T("Extract")));
                var fs = _fs;
                IsoFileSystem.ExtractReport? r = null;
                await RunContentTask((p, ct) => { r = fs.ExtractAll(Path.Combine(baseDir, Loc.T("FilesFolder")), p, ct); return ""; });
                files = r?.Files ?? 0;

                AddDiscVideos(switchTab: false);
                if (_videos.Any(v => !v.Done) && VideoConverter.FindFfmpeg() != null)
                {
                    AddLog(Loc.T("OneClickStep", 3, Loc.T("TabVideo")));
                    Tabs.SelectedIndex = 2;
                    var res = await ConvertVideosAsync(Path.Combine(baseDir, Loc.T("VideosFolder")));
                    videos = res.Done;
                }
            }
            if (_image != null && AudioTracks.Find(_image.Info).Count > 0)
            {
                AddLog(Loc.T("OneClickStep", 4, Loc.T("SaveAudio")));
                songs = await ExportAudioAsync(_image, Path.Combine(baseDir, Loc.T("MusicFolder")), VideoConverter.FindFfmpeg() != null ? SelectedAudioFormat() : AudioFormat.Wav);
            }
            var map = (_fs?.Source as DiscImage)?.Map ?? _image?.Map;
            if (map != null) WriteReport(image, info, map, open: false);
            summary = Loc.T("OneClickDone", files, videos, songs, baseDir);
            Tabs.SelectedIndex = 0;
            if (Directory.Exists(baseDir)) Process.Start("explorer.exe", $"\"{baseDir}\"");
        }
        finally
        {
            _quiet = false;
            OneClickButton.IsEnabled = true;
        }
        await FinishedAsync(Loc.T("RescueDoneTitle"), summary, discJob: true);
    }

    // =====================================================================
    // Drag & drop, updates
    // =====================================================================

    private static readonly string[] ImageExtensions = [".bin", ".iso"];

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        foreach (var path in paths)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ImageExtensions.Contains(ext) && _engine == null)
            {
                CloseLiveContent(clearTree: true);
                LoadImage(path);
                Tabs.SelectedIndex = 1;
            }
            else if (VideoConverter.VideoExtensions.Contains(ext))
            {
                _videos.Add(new VideoItem { Name = Path.GetFileName(path), FilePath = path, Detail = $"{path} — {FormatBytes(new FileInfo(path).Length)}" });
                Tabs.SelectedIndex = 2;
            }
        }
    }

    private string? _updateUrl;

    private async Task CheckForUpdateAsync()
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DVD360");
            string json = await http.GetStringAsync("https://api.github.com/repos/OmerCarnacar/DVD_360/releases/latest");
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            var current = Assembly.GetExecutingAssembly().GetName().Version;
            if (Version.TryParse(tag.TrimStart('v', 'V'), out var latest) && current != null && latest > new Version(current.Major, current.Minor, current.Build))
            {
                _updateUrl = doc.RootElement.GetProperty("html_url").GetString();
                UpdateButton.Content = Loc.T("UpdateAvailable", tag);
                UpdateButton.Visibility = Visibility.Visible;
            }
        }
        catch { /* offline: no problem */ }
    }

    private void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_updateUrl != null) Process.Start(new ProcessStartInfo(_updateUrl) { UseShellExecute = true });
    }

    private void Lang_Click(object sender, RoutedEventArgs e)
    {
        Loc.I.Toggle();
        CoreText.Turkish = !Loc.I.English;
        Title = "DVD 360 — " + Loc.T("AppSubtitle");
        UpdateFfmpegStatus();
        ShowDiscTexts();
        UpdateSelectionText();
        UpdateTarget();
        if (_fs != null) ShowContent(_fs, _fs.Source is LiveDiscSource ? Loc.T("SourceLive", _driveLetter ?? "?") : Loc.T("SourceImage", Path.GetFileName(((DiscImage)_fs.Source).Path)), null);
    }

    private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_engine != null)
        {
            string q = Loc.I.English ? "A rescue is running. Stop it and exit? (Progress is saved.)" : "Kurtarma devam ediyor. Durdurup çıkılsın mı? (İlerleme kaydedilir.)";
            if (MessageBox.Show(this, q, "DVD 360", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) { e.Cancel = true; return; }
            _rescueCts?.Cancel();
        }
        _videoCts?.Cancel();
        AppSettings.Save(new AppSettings { Folder = FolderBox.Text.Trim(), VideoFolder = VideoFolderBox.Text.Trim() });
        _liveDevice?.Dispose();
        _image?.Dispose();
    }

    private void Warn(string message) => MessageBox.Show(this, message, "DVD 360", MessageBoxButton.OK, MessageBoxImage.Warning);

    internal static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{v:N1} {units[u]}";
    }
}
