namespace Dvd360.Core;

/// <summary>Saves the audio tracks of a rescued CD image as WAV files (CD audio is 44.1 kHz, 16-bit stereo PCM).</summary>
public static class AudioTracks
{
    public sealed record AudioTrack(int Number, long StartLba, long EndLba)
    {
        public long Sectors => EndLba - StartLba;
        public TimeSpan Duration => TimeSpan.FromSeconds(Sectors / 75.0);
    }

    /// <summary>Audio tracks of a disc with their sector ranges.</summary>
    public static List<AudioTrack> Find(DiscInfo info)
    {
        var list = new List<AudioTrack>();
        var tracks = info.Tracks.OrderBy(t => t.StartLba).ToList();
        for (int i = 0; i < tracks.Count; i++)
        {
            if (tracks[i].IsData) continue;
            long end = i + 1 < tracks.Count ? tracks[i + 1].StartLba : info.TotalSectors;
            // on an Enhanced CD the data session follows after a long gap; never run past the disc
            end = Math.Min(end, info.TotalSectors);
            if (end > tracks[i].StartLba) list.Add(new AudioTrack(tracks[i].Number, tracks[i].StartLba, end));
        }
        return list;
    }

    /// <summary>Writes "Track NN.wav" for each audio track. Unread sectors become silence. Returns the files written.</summary>
    public static List<string> ExportWav(DiscImage image, string outputDir, IProgress<(long Done, long Total, string Name)>? progress = null, CancellationToken ct = default)
    {
        if (!image.IsRawCd) throw new InvalidOperationException("Not a raw CD image.");
        Directory.CreateDirectory(outputDir);
        var tracks = Find(image.Info);
        long total = tracks.Sum(t => t.Sectors), done = 0;
        var written = new List<string>();
        var silence = new byte[CdSector.RawSize];
        foreach (var t in tracks)
        {
            string path = Path.Combine(outputDir, $"Track {t.Number:00}.wav");
            progress?.Report((done, total, Path.GetFileName(path)));
            using (var o = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                WriteWavHeader(o, t.Sectors * CdSector.RawSize);
                for (long lba = t.StartLba; lba < t.EndLba; lba++)
                {
                    if (((lba - t.StartLba) & 1023) == 0) { ct.ThrowIfCancellationRequested(); progress?.Report((done, total, Path.GetFileName(path))); }
                    if (lba < image.SectorCount && image.IsGood(lba)) o.Write(image.ReadRaw(lba));
                    else o.Write(silence);
                    done++;
                }
            }
            written.Add(path);
        }
        progress?.Report((total, total, ""));
        return written;
    }

    internal static void WriteWavHeader(Stream o, long dataBytes)
    {
        using var w = new BinaryWriter(o, System.Text.Encoding.ASCII, leaveOpen: true);
        w.Write("RIFF"u8);
        w.Write((uint)Math.Min(uint.MaxValue, 36 + dataBytes));
        w.Write("WAVEfmt "u8);
        w.Write(16);              // PCM chunk size
        w.Write((short)1);        // PCM
        w.Write((short)2);        // stereo
        w.Write(44100);
        w.Write(44100 * 4);       // byte rate
        w.Write((short)4);        // block align
        w.Write((short)16);       // bits
        w.Write("data"u8);
        w.Write((uint)Math.Min(uint.MaxValue, dataBytes));
    }
}
