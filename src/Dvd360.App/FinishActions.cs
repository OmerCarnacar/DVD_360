using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Dvd360.App;

public enum PowerAction { None, Sleep, Hibernate, Shutdown }

/// <summary>What to do when a long job (rescue, conversion) ends: notify, eject the disc, sleep or shut down.</summary>
public static class FinishActions
{
    private static System.Windows.Forms.NotifyIcon? _tray;

    /// <summary>Windows notification (shown as a toast on Windows 10/11).</summary>
    public static void Notify(string title, string text)
    {
        try
        {
            if (_tray == null)
            {
                _tray = new System.Windows.Forms.NotifyIcon
                {
                    Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
                    Text = "DVD 360",
                    Visible = true,
                };
                Application.Current.Exit += (_, _) => { _tray.Visible = false; _tray.Dispose(); };
            }
            _tray.ShowBalloonTip(8000, title, text, System.Windows.Forms.ToolTipIcon.Info);
        }
        catch { /* notifications are a nicety */ }
        System.Media.SystemSounds.Asterisk.Play();
    }

    /// <summary>Shows a countdown the user can cancel, then performs the power action.</summary>
    public static void RunPowerAction(Window owner, PowerAction action)
    {
        if (action == PowerAction.None) return;
        if (!Countdown(owner, action, seconds: action == PowerAction.Shutdown ? 60 : 30)) return;
        switch (action)
        {
            case PowerAction.Sleep: SetSuspendState(false, false, false); break;
            case PowerAction.Hibernate: SetSuspendState(true, false, false); break;
            case PowerAction.Shutdown:
                Process.Start(new ProcessStartInfo("shutdown.exe", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false });
                break;
        }
    }

    private static bool Countdown(Window owner, PowerAction action, int seconds)
    {
        string what = Loc.T("Power_" + action);
        var text = new TextBlock { FontSize = 15, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
        var cancel = new Button { Content = Loc.T("Cancel"), Style = (Style)Application.Current.FindResource("Primary"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0) };
        var now = new Button { Content = Loc.T("DoItNow"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 8, 0) };
        var win = new Window
        {
            Title = "DVD 360", Width = 440, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = owner.IsVisible ? owner : null, Topmost = true,
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("Bg"),
            Content = new StackPanel
            {
                Margin = new Thickness(22),
                Children = { text, new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { now, cancel } } },
            },
        };
        bool go = false;
        int left = seconds;
        void Show() => text.Text = Loc.T("PowerCountdown", what, left);
        Show();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => { if (--left <= 0) { go = true; win.Close(); } else Show(); };
        cancel.Click += (_, _) => win.Close();
        now.Click += (_, _) => { go = true; win.Close(); };
        win.Closed += (_, _) => timer.Stop();
        timer.Start();
        win.ShowDialog();
        return go;
    }

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);
}
