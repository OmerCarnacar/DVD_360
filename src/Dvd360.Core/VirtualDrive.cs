using System.Diagnostics;

namespace Dvd360.Core;

/// <summary>Mounts an .iso as a virtual drive using Windows' built-in support (no extra driver).</summary>
public static class VirtualDrive
{
    public static async Task<string?> MountAsync(string isoPath)
    {
        string p = Quote(Path.GetFullPath(isoPath));
        string script = $"$i = Get-DiskImage -ImagePath {p}; if (-not $i.Attached) {{ $i = Mount-DiskImage -ImagePath {p} -PassThru }}; " +
                        $"Start-Sleep -Milliseconds 500; ($i | Get-Volume).DriveLetter";
        string output = await RunPowerShell(script);
        string letter = output.Trim().Split('\n').LastOrDefault()?.Trim() ?? "";
        return letter.Length == 1 && char.IsLetter(letter[0]) ? letter : null;
    }

    public static Task DismountAsync(string isoPath) =>
        RunPowerShell($"Dismount-DiskImage -ImagePath {Quote(Path.GetFullPath(isoPath))} | Out-Null");

    private static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

    private static async Task<string> RunPowerShell(string script)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", "$ErrorActionPreference='Stop'; " + script })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        string err = (await stderr).Trim();
        if (p.ExitCode != 0) throw new IOException(err.Length > 0 ? err : $"PowerShell exited with {p.ExitCode}");
        return await stdout;
    }
}
