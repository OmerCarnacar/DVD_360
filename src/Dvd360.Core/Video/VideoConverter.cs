using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Dvd360.Core.Video;

public enum AudioFormat { Mp3, Flac, Wav }

public enum VideoQuality
{
    /// <summary>Same resolution, cleaned up (deinterlace + light denoise).</summary>
    Original,
    /// <summary>Upscaled to 720p with denoise and sharpening.</summary>
    Hd720,
    /// <summary>Upscaled to 1080p with stronger cleanup and sharpening.</summary>
    FullHd1080,
}

public sealed class VideoJob
{
    public required string Input { get; init; }
    public required string Output { get; init; }
    public VideoQuality Quality { get; init; } = VideoQuality.Hd720;
    /// <summary>H.265 makes files about half the size; H.264 plays everywhere.</summary>
    public bool UseHevc { get; init; }
}

/// <summary>Turns old VCD/DVD video (MPEG-1/2) into a modern, upscaled MP4 using FFmpeg.</summary>
public static partial class VideoConverter
{
    public const string FfmpegDownloadUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

    /// <summary>Set by the app in portable mode, so FFmpeg lives next to the program.</summary>
    public static string? ToolsFolderOverride { get; set; }

    public static string ToolsFolder => ToolsFolderOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DVD360", "ffmpeg");

    /// <summary>Finds ffmpeg.exe next to the program, in our tools folder, or on PATH.</summary>
    public static string? FindFfmpeg()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
            Path.Combine(ToolsFolder, "ffmpeg.exe"),
        };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(dir.Trim(), "ffmpeg.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Downloads a static FFmpeg build into the user's tools folder.</summary>
    public static async Task<string> DownloadFfmpegAsync(IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(ToolsFolder);
        string zip = Path.Combine(ToolsFolder, "ffmpeg.zip");
        using (var http = new HttpClient())
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DVD360/1.0");
            using var resp = await http.GetAsync(FfmpegDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(zip);
            var buf = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report((double)done / total.Value);
            }
        }
        using (var archive = ZipFile.OpenRead(zip))
        {
            foreach (var e in archive.Entries.Where(e => e.Name is "ffmpeg.exe" or "ffprobe.exe"))
                e.ExtractToFile(Path.Combine(ToolsFolder, e.Name), overwrite: true);
        }
        File.Delete(zip);
        return Path.Combine(ToolsFolder, "ffmpeg.exe");
    }

    /// <summary>The FFmpeg filter chain for a quality level.</summary>
    public static string BuildFilter(VideoQuality q)
    {
        // 1) bwdif: modern motion-adaptive deinterlacer (only touches interlaced frames)
        // 2) fix non-square VCD/DVD pixels so the picture has the right shape
        // 3) denoise: hqdn3d (fast) / nlmeans (best, slower)
        // 4) lanczos upscale + unsharp to bring back edges
        var f = new List<string> { "bwdif=mode=send_frame:parity=auto:deint=interlaced", "scale=trunc(ih*dar/2)*2:ih:flags=lanczos", "setsar=1" };
        switch (q)
        {
            case VideoQuality.Original:
                f.Add("hqdn3d=1.5:1.5:4:4");
                break;
            case VideoQuality.Hd720:
                f.Add("hqdn3d=2:1.5:5:5");
                f.Add("scale=-2:720:flags=lanczos");
                f.Add("unsharp=5:5:0.7:3:3:0.0");
                break;
            case VideoQuality.FullHd1080:
                f.Add("nlmeans=s=2.5:p=5:r=11");
                f.Add("scale=-2:1080:flags=lanczos");
                f.Add("unsharp=7:7:0.8:3:3:0.0");
                break;
        }
        f.Add("format=yuv420p");
        return string.Join(",", f);
    }

    public static IReadOnlyList<string> BuildArguments(VideoJob job)
    {
        var a = new List<string>
        {
            "-hide_banner", "-y", "-nostdin",
            // tolerate the damage that bad sectors leave in the stream
            "-fflags", "+genpts+discardcorrupt", "-err_detect", "ignore_err",
            // a rescued file may start with unread (zeroed) sectors: look far ahead for the first real packets
            "-probesize", "200M", "-analyzeduration", "200M",
        };
        // VCD/DVD video is an MPEG program stream; say so, because the zeroed start defeats format detection
        if (Path.GetExtension(job.Input).ToLowerInvariant() is ".mpg" or ".mpeg" or ".dat" or ".vob")
            a.AddRange(["-f", "mpeg"]);
        a.AddRange(
        [
            "-i", job.Input,
            // only the first video and audio track: damaged sectors can look like extra bogus streams
            "-map", "0:v:0", "-map", "0:a:0?",
            "-vf", BuildFilter(job.Quality),
        ]);
        if (job.UseHevc) a.AddRange(["-c:v", "libx265", "-preset", "slow", "-crf", "20", "-tag:v", "hvc1"]);
        else a.AddRange(["-c:v", "libx264", "-preset", "slow", "-crf", "18", "-profile:v", "high"]);
        a.AddRange(["-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-movflags", "+faststart", "-progress", "pipe:1", "-nostats", job.Output]);
        return a;
    }

    /// <summary>Runs the conversion. Progress is 0..1.</summary>
    public static async Task ConvertAsync(string ffmpeg, VideoJob job, IProgress<double>? progress, Action<string>? log, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var arg in BuildArguments(job)) psi.ArgumentList.Add(arg);
        using var p = new Process { StartInfo = psi };
        double duration = 0;
        var errTail = new Queue<string>();
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            var m = DurationRegex().Match(e.Data);
            if (m.Success && duration == 0)
                duration = TimeSpan.ParseExact(m.Groups[1].Value, @"hh\:mm\:ss\.ff", CultureInfo.InvariantCulture).TotalSeconds;
            lock (errTail) { errTail.Enqueue(e.Data); if (errTail.Count > 15) errTail.Dequeue(); }
        };
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null || duration <= 0) return;
            if (e.Data.StartsWith("out_time_us=") && long.TryParse(e.Data.AsSpan(12), out long us))
                progress?.Report(Math.Clamp(us / 1e6 / duration, 0, 1));
        };
        p.Start();
        p.BeginErrorReadLine();
        p.BeginOutputReadLine();
        using (ct.Register(() => { try { p.Kill(true); } catch { } }))
            await p.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        if (p.ExitCode != 0)
        {
            string tail;
            lock (errTail) tail = string.Join(Environment.NewLine, errTail);
            log?.Invoke(tail);
            throw new IOException($"FFmpeg failed (exit code {p.ExitCode}).");
        }
        progress?.Report(1);
    }

    public static string Extension(AudioFormat f) => f switch { AudioFormat.Mp3 => ".mp3", AudioFormat.Flac => ".flac", _ => ".wav" };

    public static IReadOnlyList<string> BuildAudioArguments(string input, string output, AudioFormat format)
    {
        var a = new List<string> { "-hide_banner", "-y", "-nostdin", "-fflags", "+genpts+discardcorrupt", "-err_detect", "ignore_err", "-probesize", "200M", "-analyzeduration", "200M" };
        if (Path.GetExtension(input).ToLowerInvariant() is ".mpg" or ".mpeg" or ".dat" or ".vob") a.AddRange(["-f", "mpeg"]);
        a.AddRange(["-i", input, "-vn", "-map", "0:a:0"]);
        a.AddRange(format switch
        {
            AudioFormat.Mp3 => ["-c:a", "libmp3lame", "-q:a", "0"],            // VBR, ~245 kbit/s
            AudioFormat.Flac => ["-c:a", "flac", "-compression_level", "8"],     // lossless
            _ => ["-c:a", "pcm_s16le"],
        });
        a.Add(output);
        return a;
    }

    /// <summary>Saves only the sound of a video, or converts a WAV, to MP3 / FLAC / WAV.</summary>
    public static async Task ConvertAudioAsync(string ffmpeg, string input, string output, AudioFormat format, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var arg in BuildAudioArguments(input, output, format)) psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi)!;
        _ = p.StandardError.ReadToEndAsync(CancellationToken.None);
        using (ct.Register(() => { try { p.Kill(true); } catch { } }))
            await p.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        if (p.ExitCode != 0) throw new IOException($"FFmpeg failed on {Path.GetFileName(input)} (exit code {p.ExitCode}).");
    }

    [GeneratedRegex(@"Duration:\s*(\d{2}:\d{2}:\d{2}\.\d{2})")]
    private static partial Regex DurationRegex();

    public static readonly string[] VideoExtensions = [".mpg", ".mpeg", ".dat", ".vob", ".m2v", ".avi", ".wmv", ".mov", ".mkv", ".mp4", ".m2ts", ".mts", ".3gp", ".flv"];
}
