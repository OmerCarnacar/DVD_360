namespace Dvd360.Core;

public sealed record TrackInfo(int Number, long StartLba, bool IsData);

/// <summary>What we know about the inserted disc.</summary>
public sealed class DiscInfo
{
    public string DriveLetter { get; set; } = "";
    public string DriveName { get; set; } = "";
    public string MediaType { get; set; } = "";
    public string VolumeLabel { get; set; } = "";
    public ushort Profile { get; set; }
    public bool IsCd { get; set; }
    public long TotalSectors { get; set; }
    /// <summary>Bytes stored per sector in the image: 2352 (raw CD) or 2048 (DVD/BD).</summary>
    public int SectorSize { get; set; }
    public List<TrackInfo> Tracks { get; set; } = new();

    public long TotalBytes => TotalSectors * SectorSize;
    public string ImageExtension => IsCd ? ".bin" : ".iso";
}

public enum ReadOutcome
{
    Ok,
    /// <summary>Unreadable (scratch, dirt, damaged dye...). Worth retrying.</summary>
    MediaError,
    /// <summary>Address does not exist on the disc. Not worth retrying.</summary>
    NotPresent,
    /// <summary>The disc was removed or the drive is not ready.</summary>
    NoMedia,
}

/// <summary>A source of raw sectors. The real implementation talks SCSI to the drive; tests use a fake.</summary>
public interface ISectorDevice : IDisposable
{
    DiscInfo Info { get; }
    /// <summary>Largest number of sectors one <see cref="Read"/> may ask for.</summary>
    int MaxSectorsPerRead { get; }
    /// <summary>How long the drive may spend on one read before it counts as failed.</summary>
    uint ReadTimeoutSeconds { get; set; }
    ReadOutcome Read(long lba, int count, Span<byte> destination);
    /// <summary>Sets read speed as a multiple of 1x (0 = maximum). Best effort.</summary>
    bool SetReadSpeed(int multiplier);
}

public sealed class NoMediaException(string message) : Exception(message);
