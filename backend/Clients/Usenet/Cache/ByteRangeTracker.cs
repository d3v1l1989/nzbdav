namespace NzbWebDAV.Clients.Usenet.Cache;

/// <summary>
/// Thread-safe sorted list of non-overlapping (start, end) byte ranges with merge-on-insert.
/// </summary>
public class ByteRangeTracker : IDisposable
{
    private readonly SortedList<long, long> _ranges = new(); // key=start, value=end (exclusive)
    private readonly ReaderWriterLockSlim _lock = new();
    private long _totalCachedBytes;

    public long TotalCachedBytes
    {
        get
        {
            _lock.EnterReadLock();
            try { return _totalCachedBytes; }
            finally { _lock.ExitReadLock(); }
        }
    }

    public void AddRange(long start, long length)
    {
        if (length <= 0) return;
        var end = start + length;

        _lock.EnterWriteLock();
        try
        {
            var mergedStart = start;
            var mergedEnd = end;
            var toRemove = new List<int>();

            // Find all overlapping or adjacent ranges
            var keys = _ranges.Keys;
            var values = _ranges.Values;

            for (var i = 0; i < keys.Count; i++)
            {
                var rStart = keys[i];
                var rEnd = values[i];

                // Range is entirely after our merged range — stop
                if (rStart > mergedEnd) break;

                // Range is entirely before our merged range — skip
                if (rEnd < mergedStart) continue;

                // Overlapping or adjacent — merge
                mergedStart = Math.Min(mergedStart, rStart);
                mergedEnd = Math.Max(mergedEnd, rEnd);
                toRemove.Add(i);
            }

            // Remove merged ranges in reverse order to preserve indices
            for (var i = toRemove.Count - 1; i >= 0; i--)
                _ranges.RemoveAt(toRemove[i]);

            _ranges[mergedStart] = mergedEnd;

            // Recalculate total
            _totalCachedBytes = 0;
            for (var i = 0; i < _ranges.Count; i++)
                _totalCachedBytes += _ranges.Values[i] - _ranges.Keys[i];
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public bool IsPresent(long start, long length)
    {
        if (length <= 0) return true;
        var end = start + length;

        _lock.EnterReadLock();
        try
        {
            var keys = _ranges.Keys;
            var values = _ranges.Values;

            // Binary search for the range that could contain [start, end)
            var lo = 0;
            var hi = keys.Count - 1;
            while (lo <= hi)
            {
                var mid = lo + (hi - lo) / 2;
                if (keys[mid] <= start)
                    lo = mid + 1;
                else
                    hi = mid - 1;
            }

            // hi now points to the last range with start <= our start
            if (hi < 0) return false;
            return keys[hi] <= start && values[hi] >= end;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public List<(long Start, long End)> GetRanges()
    {
        _lock.EnterReadLock();
        try
        {
            var result = new List<(long, long)>(_ranges.Count);
            for (var i = 0; i < _ranges.Count; i++)
                result.Add((_ranges.Keys[i], _ranges.Values[i]));
            return result;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public void Dispose()
    {
        _lock.Dispose();
    }

    internal static ByteRangeTracker FromRanges(List<(long Start, long End)> ranges)
    {
        var tracker = new ByteRangeTracker();
        foreach (var (start, end) in ranges)
        {
            tracker._ranges[start] = end;
            tracker._totalCachedBytes += end - start;
        }
        return tracker;
    }
}
