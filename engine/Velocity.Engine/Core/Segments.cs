namespace Velocity.Engine.Core;

public sealed class RangeList
{
    private readonly List<(long Start, long End)> _ranges = new();

    public int Count => _ranges.Count;
    public IReadOnlyList<(long Start, long End)> Items => _ranges;
    public long Total { get; private set; }

    public void Add(long start, long end)
    {
        if (end <= start) return;
        var index = LowerBound(start);
        if (index > 0 && _ranges[index - 1].End >= start) index--;
        var first = index;
        var newStart = start;
        var newEnd = end;
        while (index < _ranges.Count && _ranges[index].Start <= newEnd)
        {
            newStart = Math.Min(newStart, _ranges[index].Start);
            newEnd = Math.Max(newEnd, _ranges[index].End);
            Total -= _ranges[index].End - _ranges[index].Start;
            index++;
        }
        if (index > first) _ranges.RemoveRange(first, index - first);
        _ranges.Insert(first, (newStart, newEnd));
        Total += newEnd - newStart;
    }

    public void Remove(long start, long end)
    {
        if (end <= start) return;
        for (var i = _ranges.Count - 1; i >= 0; i--)
        {
            var (s, e) = _ranges[i];
            if (e <= start || s >= end) continue;
            _ranges.RemoveAt(i);
            Total -= e - s;
            if (e > end)
            {
                _ranges.Insert(i, (end, e));
                Total += e - end;
            }
            if (s < start)
            {
                _ranges.Insert(i, (s, start));
                Total += start - s;
            }
        }
    }

    private int LowerBound(long start)
    {
        int lo = 0, hi = _ranges.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_ranges[mid].Start < start) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}

public sealed class Segment
{
    public long Start;
    public long Pos;
    public long Reserved;
    public long End;
    public long RequestEnd;
    public string LinkId = "";
    public double Speed;
    public int WorkerId;
}

public sealed record ActiveSpan(long Start, long Pos, long End, string LinkId, double Speed, int WorkerId);

public sealed record MapSnapshot(long Size, IReadOnlyList<(long Start, long End)> Done, IReadOnlyList<ActiveSpan> Active);

public sealed class SegmentMap
{
    private const long Align = 64 * 1024;
    private readonly object _gate = new();
    private readonly RangeList _done = new();
    private readonly RangeList _pending = new();
    private readonly List<Segment> _active = new();

    public long Size { get; }

    public SegmentMap(long size, IEnumerable<(long Start, long End)>? done = null)
    {
        Size = size;
        if (done != null)
            foreach (var (s, e) in done)
                _done.Add(Math.Clamp(s, 0, size), Math.Clamp(e, 0, size));
        var cursor = 0L;
        foreach (var (s, e) in _done.Items)
        {
            if (s > cursor) _pending.Add(cursor, s);
            cursor = e;
        }
        if (cursor < size) _pending.Add(cursor, size);
    }

    public long DoneBytes { get { lock (_gate) return _done.Total; } }
    public int ActiveCount { get { lock (_gate) return _active.Count; } }
    public int FragmentCount { get { lock (_gate) return _active.Count + _pending.Count; } }
    public bool IsComplete { get { lock (_gate) return _done.Total >= Size && _active.Count == 0; } }

    public long LargestAvailable
    {
        get
        {
            lock (_gate)
            {
                var best = 0L;
                foreach (var (s, e) in _pending.Items) best = Math.Max(best, e - s);
                foreach (var seg in _active) best = Math.Max(best, (seg.End - Math.Max(seg.Reserved, seg.Pos)) / 2);
                return best;
            }
        }
    }

    public Segment? Acquire(long minSplit, string linkId, int workerId)
    {
        lock (_gate)
        {
            var bestIndex = -1;
            var bestLength = -1L;
            var items = _pending.Items;
            for (var i = 0; i < items.Count; i++)
            {
                var length = items[i].End - items[i].Start;
                if (length > bestLength)
                {
                    bestLength = length;
                    bestIndex = i;
                }
            }
            if (bestIndex >= 0)
            {
                var (s, e) = items[bestIndex];
                _pending.Remove(s, e);
                return Track(s, e, linkId, workerId);
            }
            Segment? victim = null;
            var victimRemaining = 0L;
            foreach (var seg in _active)
            {
                var remaining = seg.End - Math.Max(seg.Reserved, seg.Pos);
                if (remaining > victimRemaining)
                {
                    victimRemaining = remaining;
                    victim = seg;
                }
            }
            if (victim == null || victimRemaining < minSplit * 2) return null;
            var from = Math.Max(victim.Reserved, victim.Pos);
            var cut = from + victimRemaining / 2;
            if (victim.RequestEnd > cut && victim.RequestEnd <= victim.End - minSplit) cut = victim.RequestEnd;
            var aligned = cut / Align * Align;
            if (aligned - from >= minSplit && victim.End - aligned >= minSplit) cut = aligned;
            var oldEnd = victim.End;
            victim.End = cut;
            return Track(cut, oldEnd, linkId, workerId);
        }
    }

    private Segment Track(long start, long end, string linkId, int workerId)
    {
        var seg = new Segment { Start = start, Pos = start, Reserved = start, End = end, RequestEnd = end, LinkId = linkId, WorkerId = workerId };
        _active.Add(seg);
        return seg;
    }

    public long EndOf(Segment seg)
    {
        lock (_gate) return seg.End;
    }

    public int Reserve(Segment seg, int wanted)
    {
        lock (_gate)
        {
            var available = seg.End - seg.Pos;
            var allowed = (int)Math.Min(available, wanted);
            if (allowed < 0) allowed = 0;
            seg.Reserved = seg.Pos + allowed;
            return allowed;
        }
    }

    public void Commit(Segment seg, int count)
    {
        lock (_gate)
        {
            if (count > 0) _done.Add(seg.Pos, seg.Pos + count);
            seg.Pos += count;
            seg.Reserved = seg.Pos;
        }
    }

    public void Release(Segment seg)
    {
        lock (_gate)
        {
            _active.Remove(seg);
            if (seg.Pos < seg.End) _pending.Add(seg.Pos, seg.End);
        }
    }

    public void Invalidate(long start, long end)
    {
        lock (_gate)
        {
            _done.Remove(start, end);
            _pending.Add(start, end);
        }
    }

    public int FindTakeover(double mySpeed, int selfWorkerId)
    {
        if (mySpeed <= 0) return -1;
        lock (_gate)
        {
            var worst = -1;
            var worstEta = 0d;
            foreach (var seg in _active)
            {
                if (seg.WorkerId == selfWorkerId) continue;
                var remaining = seg.End - seg.Pos;
                if (remaining <= 0) continue;
                var eta = seg.Speed > 1 ? remaining / seg.Speed : 3600;
                var mine = remaining / mySpeed;
                if (eta > mine * 3 + 2 && eta > worstEta)
                {
                    worstEta = eta;
                    worst = seg.WorkerId;
                }
            }
            return worst;
        }
    }

    public MapSnapshot Snapshot(int maxRanges = 512)
    {
        lock (_gate)
        {
            var active = new ActiveSpan[_active.Count];
            for (var i = 0; i < active.Length; i++)
            {
                var seg = _active[i];
                active[i] = new ActiveSpan(seg.Start, seg.Pos, seg.End, seg.LinkId, seg.Speed, seg.WorkerId);
            }
            var done = new List<(long, long)>(Math.Min(_done.Count, maxRanges));
            if (_done.Count <= maxRanges)
            {
                done.AddRange(_done.Items);
            }
            else
            {
                var gap = Size / maxRanges;
                (long Start, long End)? current = null;
                foreach (var range in _done.Items)
                {
                    if (current is { } c && range.Start - c.End <= gap) current = (c.Start, range.End);
                    else
                    {
                        if (current is { } flush) done.Add(flush);
                        current = range;
                    }
                }
                if (current is { } last) done.Add(last);
            }
            return new MapSnapshot(Size, done, active);
        }
    }
}
