using System.Text;

namespace Dvd360.Core;

public static class CueSheet
{
    /// <summary>Writes a .cue for a single raw .bin that holds every sector from LBA 0.</summary>
    /// <param name="kindAt">Returns the sector kind at an LBA, or null if that sector is not readable.</param>
    public static void Write(string cuePath, string binFileName, DiscInfo info, Func<long, SectorKind?> kindAt)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"FILE \"{binFileName}\" BINARY");
        var tracks = info.Tracks.Count > 0 ? info.Tracks : [new TrackInfo(1, 0, true)];
        for (int i = 0; i < tracks.Count; i++)
        {
            var t = tracks[i];
            string mode = "AUDIO";
            if (t.IsData)
            {
                mode = "MODE1/2352";
                long end = i + 1 < tracks.Count ? tracks[i + 1].StartLba : info.TotalSectors;
                for (long lba = t.StartLba; lba < Math.Min(end, t.StartLba + 32); lba++)
                {
                    var k = kindAt(lba);
                    if (k is SectorKind.Mode2Form1 or SectorKind.Mode2Form2) { mode = "MODE2/2352"; break; }
                    if (k == SectorKind.Mode1) break;
                }
            }
            sb.AppendLine($"  TRACK {t.Number:00} {mode}");
            sb.AppendLine($"    INDEX 01 {CdSector.ToMsf(t.StartLba)}");
        }
        File.WriteAllText(cuePath, sb.ToString(), new UTF8Encoding(false));
    }
}
