using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace Dvd360.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        if (AppSettings.IsPortable)
            Dvd360.Core.Video.VideoConverter.ToolsFolderOverride = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
        base.OnStartup(e);
    }

    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            string log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DVD360", "error.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.AppendAllText(log, $"{DateTime.Now:u}\n{e.Exception}\n\n");
        }
        catch { }
        MessageBox.Show(e.Exception.Message, "DVD 360", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
