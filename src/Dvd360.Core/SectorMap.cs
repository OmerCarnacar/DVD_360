using System.Text;

namespace Dvd360.Core;

/// <summary>State of a single sector during a rescue.</summary>
public enum SectorState : byte
{
    /// <summary>Not read yet.</summary>
    Untried = 0,
    /// <summary>Read and verified.</summary>
    Ok = 1,
    /// <summary>Failed inside a multi-sector block; will be retried one by one.</summary>
    NeedsRetry = 2,
    /// <summary>Failed every individual attempt so far.</summary>
    Bad = 3,
    /// <summary>The drive reports this address does not exist (e.g. gap between sessions).</summary>
    Skipped = 4,
}

/// <summary>
/// One byte per sector, saved next to the image so a rescue can be resumed and
/// later passes only touch the sectors that still need work (like GNU ddrescue's mapfile).
/// </summary>
public sealed class SectorMap
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("D360MAP1");
    private readonly byte[] _states;
    private readonly long[] _counts = new long[5];

    public SectorMap(long sectorCount, int sectorSize)
    {
        if (sectorCount <= 0 || sectorCount > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(sectorCount));
        _states = new byte[sectorCount];
        SectorSize = sectorSize;
        _counts[0] = sectorCount;
    }

    public long Count => _states.LongLength;
    public int SectorSize { get; }
    public ReadOnlySpan<byte> Raw => _states;

    public SectorState this[long lba] => (SectorState)_states[lba];

    /// <summary>Single writer (the engine thread); readers may see slightly stale values.</summary>
    public void Set(long lba, SectorState state)
    {
        byte old = _states[lba];
        if (old == (byte)state) return;
        _states[lba] = (byte)state;
        Interlocked.Decrement(ref _counts[old]);
        Interlocked.Increment(ref _counts[(int)state]);
    }

    public long CountOf(SectorState state) => Interlocked.Read(ref _counts[(int)state]);

    public long FindNext(long from, SectorState state)
    {
        if (from >= Count) return -1;
        int i = _states.AsSpan((int)from).IndexOf((byte)state);
        return i < 0 ? -1 : from + i;
    }

    public long FindPrev(long from, SectorState state)
    {
        if (from < 0) return -1;
        return _states.AsSpan(0, (int)Math.Min(from + 1, Count)).LastIndexOf((byte)state);
    }

    /// <summary>Number of consecutive sectors with <paramref name="state"/> starting at <paramref name="lba"/>, capped at <paramref name="max"/>.</summary>
    public int RunLength(long lba, SectorState state, int max)
    {
        int n = 0;
        while (n < max && lba + n < Count && _states[lba + n] == (byte)state) n++;
        return n;
    }

    public void Save(string path)
    {
        string tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var w = new BinaryWriter(fs))
        {
            w.Write(Magic);
            w.Write(SectorSize);
            w.Write(Count);
            w.Write(_states);
            w.Flush();
            fs.Flush(true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Reads only the header of a map file: (sector size, sector count), or null if it is not one.</summary>
    public static (int SectorSize, long Count)? TryReadHeader(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var r = new BinaryReader(fs);
            if (!r.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic)) return null;
            return (r.ReadInt32(), r.ReadInt64());
        }
        catch { return null; }
    }

    /// <summary>Counts sectors per state in [start, start+count).</summary>
    public (long Ok, long Bad, long Retry, long Pending) Summarize(long start, long count)
    {
        long ok = 0, bad = 0, retry = 0, pending = 0;
        long end = Math.Min(Count, start + count);
        for (long i = Math.Max(0, start); i < end; i++)
        {
            switch ((SectorState)_states[i])
            {
                case SectorState.Ok: case SectorState.Skipped: ok++; break;
                case SectorState.Bad: bad++; break;
                case SectorState.NeedsRetry: retry++; break;
                default: pending++; break;
            }
        }
        return (ok, bad, retry, pending);
    }

    public static SectorMap Load(string path)
    {
        using var fs = File.OpenRead(path);
        using var r = new BinaryReader(fs);
        if (!r.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("Not a DVD 360 map file.");
        int sectorSize = r.ReadInt32();
        long count = r.ReadInt64();
        var map = new SectorMap(count, sectorSize);
        byte[] data = r.ReadBytes((int)count);
        if (data.Length != count) throw new InvalidDataException("Map file is truncated.");
        Array.Fill(map._counts, 0);
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] > 4) data[i] = 0;
            map._counts[data[i]]++;
        }
        data.CopyTo(map._states, 0);
        return map;
    }
}
