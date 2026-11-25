using System.IO;
using System.Text.Json;

namespace Dvd360.App;

/// <summary>Remembers the save folders between runs.</summary>
public sealed class AppSettings
{
    public string? Folder { get; set; }
    public string? VideoFolder { get; set; }

    /// <summary>Portable mode: a file named "portable.txt" next to the program keeps everything beside it (USB stick).</summary>
    public static bool IsPortable => File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt"));

    private static string FilePath => IsPortable
        ? Path.Combine(AppContext.BaseDirectory, "settings.json")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DVD360", "settings.json");

    public static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public static void Save(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(s));
        }
        catch { }
    }
}
