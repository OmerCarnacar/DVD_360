using System.Runtime.Versioning;
using Dvd360.Core;
using Dvd360.Core.Iso9660;
using Dvd360.Core.Native;
using Dvd360.Core.Video;

[assembly: SupportedOSPlatform("windows")]

// DVD 360 command line — the same engine as the desktop app, for power users and scripts.
const string Usage = """
DVD 360 — CD/DVD rescue (command line)

  dvd360 drives                         list optical drives
  dvd360 info <E>                       show disc information
  dvd360 rescue <E> <image> [--retries N] [--speed X]
                                        rescue the disc sector by sector into <image> (.bin for CD, .iso for DVD).
                                        Run again to continue; add --retry-bad to retry bad sectors
  dvd360 list <image>                   list files in a rescued image
  dvd360 extract <image> <folder>       copy all files out of the image
  dvd360 iso <image> <out.iso>          make a mountable ISO
  dvd360 mkiso <folder> <out.iso> [label]   make an ISO from a folder
  dvd360 mp4 <video> <out.mp4> [--quality original|720|1080] [--hevc]
""";

if (args.Length == 0) { Console.WriteLine(Usage); return 1; }
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); Console.WriteLine("\nStopping (progress is saved)..."); };

string Opt(string name, string def) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def; }
bool Flag(string name) => args.Contains(name);

try
{
    switch (args[0].ToLowerInvariant())
    {
        case "drives":
            foreach (var d in ScsiOpticalDevice.ListDrives()) Console.WriteLine(d + ":");
            return 0;

        case "info":
        {
            using var dev = ScsiOpticalDevice.Open(args[1]);
            var i = dev.Info;
            Console.WriteLine($"Drive:   {i.DriveName}\nMedia:   {i.MediaType} (profile 0x{i.Profile:X4})\nLabel:   {i.VolumeLabel}");
            Console.WriteLine($"Sectors: {i.TotalSectors:N0} x {i.SectorSize} = {i.TotalBytes / 1048576.0:N1} MB");
            foreach (var t in i.Tracks) Console.WriteLine($"  Track {t.Number,2}: LBA {t.StartLba,8}  {(t.IsData ? "data" : "audio")}");
            return 0;
        }

        case "rescue":
        {
            using var dev = ScsiOpticalDevice.Open(args[1]);
            var engine = new RescueEngine(dev, args[2], new RescueOptions
            {
                Retries = int.Parse(Opt("--retries", "5")),
                SpeedMultiplier = int.Parse(Opt("--speed", "0")),
            });
            engine.Log += m => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {m}");
            if (engine.IsResume) Console.WriteLine("Continuing a previous rescue.");
            var task = Flag("--retry-bad") ? engine.RetryBadAsync(cts.Token) : engine.RunAsync(cts.Token);
            while (!task.Wait(1000))
            {
                long total = engine.Map.Count;
                Console.Write($"\r{engine.Stats.Pass,-15} {100.0 * engine.OkSectors / total,6:N2}% ok  bad {engine.BadSectors,6}  pending {engine.PendingSectors,8}  LBA {engine.Stats.CurrentLba,8}  {engine.Stats.BytesPerSecond / 1024,7:N0} KB/s ");
            }
            Console.WriteLine();
            task.GetAwaiter().GetResult();
            return engine.BadSectors == 0 ? 0 : 2;
        }

        case "list":
        {
            using var img = DiscImage.Open(args[1]);
            var fs = IsoFileSystem.TryOpen(img, out string err) ?? throw new InvalidDataException(err);
            Console.WriteLine($"Volume: {fs.VolumeId} (Joliet: {fs.IsJoliet})");
            foreach (var e in fs.Root.Descendants())
                Console.WriteLine($"{(e.IsDirectory ? "<DIR>" : e.DataSize.ToString("N0")),15}  {(e.BadSectors > 0 ? $"[{e.BadSectors} bad] " : "")}{e.FullPath}{(e.IsForm2 ? "  (Mode2/Form2 video)" : "")}");
            return 0;
        }

        case "extract":
        {
            using var img = DiscImage.Open(args[1]);
            var fs = IsoFileSystem.TryOpen(img, out string err) ?? throw new InvalidDataException(err);
            var r = fs.ExtractAll(args[2], new Progress<(long, long, string)>(p => Console.Write($"\r{100.0 * p.Item1 / Math.Max(1, p.Item2),5:N1}% {p.Item3,-60}")), cts.Token);
            Console.WriteLine($"\n{r.Files} files, {r.Directories} folders, {r.Damaged.Count} damaged.");
            return 0;
        }

        case "iso":
        {
            using var img = DiscImage.Open(args[1]);
            var fs = IsoFileSystem.TryOpen(img, out _);
            var method = IsoExport.Create(img, fs, args[2], new Progress<(long, long, string)>(p => Console.Write($"\r{100.0 * p.Item1 / Math.Max(1, p.Item2),5:N1}%")), cts.Token);
            Console.WriteLine($"\nISO written ({method}).");
            return 0;
        }

        case "mkiso":
        {
            IsoBuilder.Write(BuildNode.FromFolder(args[1]), args.Length > 3 ? args[3] : new DirectoryInfo(args[1]).Name, args[2], null, cts.Token);
            Console.WriteLine("ISO written.");
            return 0;
        }

        case "mp4":
        {
            string ffmpeg = VideoConverter.FindFfmpeg() ?? await VideoConverter.DownloadFfmpegAsync(null, cts.Token);
            var q = Opt("--quality", "720") switch { "original" => VideoQuality.Original, "1080" => VideoQuality.FullHd1080, _ => VideoQuality.Hd720 };
            await VideoConverter.ConvertAsync(ffmpeg, new VideoJob { Input = args[1], Output = args[2], Quality = q, UseHevc = Flag("--hevc") },
                new Progress<double>(p => Console.Write($"\r{p * 100,5:N1}%")), Console.WriteLine, cts.Token);
            Console.WriteLine("\nDone.");
            return 0;
        }

        default:
            Console.WriteLine(Usage);
            return 1;
    }
}
catch (OperationCanceledException)
{
    Console.WriteLine("Stopped. Run the same command again to continue.");
    return 3;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException or NoMediaException)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    return 1;
}
