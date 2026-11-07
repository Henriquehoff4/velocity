namespace Velocity.Engine.Core;

public sealed record ControllerOptions
{
    public int Start { get; init; } = 1;
    public int Max { get; init; } = 16;
    public int MaxStep { get; init; } = 2;
    public double GainThreshold { get; init; } = 0.06;
    public int SettleMs { get; init; } = 1200;
    public int WindowTicks { get; init; } = 5;
    public int HoldMs { get; init; } = 20000;
    public int? Fixed { get; init; }
    public bool AutoSecondaryLinks { get; init; } = true;
}

public sealed class LinkControl
{
    public required string Id;
    public int Target;
    public int Cap = int.MaxValue;
    public long HoldUntil;
    public long CooldownMs;
    public bool Down;
    public bool NoGain;
    public double Speed;
    public int Errors;
}

public enum ProbeKind { None, Add, Remove }

public sealed class ConnectionController
{
    private readonly ControllerOptions _options;
    private readonly List<LinkControl> _links = new();
    private readonly object _gate = new();
    private long _lastChange;
    private ProbeKind _probe;
    private LinkControl? _probeLink;
    private int _probeStep;
    private double _baseline;
    private bool _hasBaseline;
    private double _sum;
    private int _samples;
    private int _step;
    private long _lastThrottleAt = long.MinValue / 2;
    private long _lastRemoveProbe;
    private long _throttledUntil;
    private bool _saturated;

    public ConnectionController(ControllerOptions options, IEnumerable<string> linkIds)
    {
        _options = options;
        _step = Math.Max(1, options.MaxStep / 2);
        var ids = linkIds.ToArray();
        for (var i = 0; i < ids.Length; i++)
        {
            int target;
            if (options.Fixed is { } value) target = Math.Max(1, value / ids.Length);
            else target = i == 0 ? Math.Clamp(options.Start, 1, options.Max) : 0;
            _links.Add(new LinkControl { Id = ids[i], Target = target, CooldownMs = options.HoldMs });
        }
    }

    public int MaxTotal => _options.Max;
    public bool Saturated { get { lock (_gate) return _saturated; } }
    public long ThrottledUntil { get { lock (_gate) return _throttledUntil; } }

    public IReadOnlyList<LinkControl> Links
    {
        get { lock (_gate) return _links.ToArray(); }
    }

    public int TotalTarget
    {
        get { lock (_gate) return _links.Where(l => !l.Down).Sum(l => l.Target); }
    }

    public int TargetFor(string id)
    {
        lock (_gate)
        {
            foreach (var link in _links)
                if (link.Id == id) return link.Down ? 0 : link.Target;
            return 0;
        }
    }

    public void SetLinks(IEnumerable<string> ids, long now)
    {
        lock (_gate)
        {
            var wanted = ids.ToHashSet();
            foreach (var link in _links) link.Down = !wanted.Contains(link.Id);
            foreach (var id in wanted)
                if (_links.All(l => l.Id != id))
                    _links.Add(new LinkControl { Id = id, CooldownMs = _options.HoldMs, HoldUntil = now });
            if (_probeLink is { Down: true }) ResetProbe(now);
            if (_links.All(l => l.Down || l.Target == 0))
            {
                var alive = _links.FirstOrDefault(l => !l.Down);
                if (alive != null) alive.Target = 1;
            }
        }
    }

    public void SetLinkDown(string id, bool down, long now)
    {
        lock (_gate)
        {
            var link = _links.FirstOrDefault(l => l.Id == id);
            if (link == null || link.Down == down) return;
            link.Down = down;
            if (down && _probeLink == link) ResetProbe(now);
            if (down && _links.All(l => l.Down || l.Target == 0))
            {
                var alive = _links.FirstOrDefault(l => !l.Down);
                if (alive != null) alive.Target = 1;
            }
            if (!down && link.Target == 0 && _links.All(l => l.Down || l.Target == 0)) link.Target = 1;
        }
    }

    public void OnThrottle(string linkId, long now, long retryAfterMs)
    {
        lock (_gate)
        {
            var sameBurst = now - _lastThrottleAt < 1500;
            _lastThrottleAt = now;
            var total = _links.Sum(l => l.Target);
            if (!sameBurst)
            {
                foreach (var item in _links)
                    if (item.Target > 1) item.Target = Math.Max(1, item.Target / 2);
                if (_links.Sum(l => l.Target) == total && total > 1)
                {
                    var link = _links.FirstOrDefault(l => l.Id == linkId);
                    if (link is { Target: > 0 }) link.Target -= 1;
                }
            }
            _step = 1;
            var wait = Math.Max(retryAfterMs, 1000);
            foreach (var item in _links)
            {
                item.HoldUntil = Math.Max(item.HoldUntil, now + wait + _options.HoldMs);
                item.Cap = Math.Max(1, Math.Min(item.Cap, Math.Max(item.Target, 1) + 1));
            }
            _throttledUntil = Math.Max(_throttledUntil, now + wait);
            ResetProbe(now);
        }
    }

    public void OnConnectionLimit(string linkId, long now)
    {
        lock (_gate)
        {
            var link = _links.FirstOrDefault(l => l.Id == linkId);
            if (link == null || link.Target <= 1) return;
            link.Target -= 1;
            link.Cap = link.Target;
            link.HoldUntil = now + _options.HoldMs * 3;
            ResetProbe(now);
        }
    }

    public void OnErrors(string linkId, int count, long now)
    {
        lock (_gate)
        {
            var link = _links.FirstOrDefault(l => l.Id == linkId);
            if (link == null) return;
            link.Errors += count;
            if (link.Errors >= Math.Max(3, link.Target) && link.Target > 1)
            {
                link.Target = Math.Max(1, (int)Math.Ceiling(link.Target * 0.75));
                link.HoldUntil = now + _options.HoldMs;
                link.Errors = 0;
                ResetProbe(now);
            }
        }
    }

    private void ResetProbe(long now)
    {
        _probe = ProbeKind.None;
        _probeLink = null;
        _hasBaseline = false;
        _sum = 0;
        _samples = 0;
        _lastChange = now;
    }

    public void Tick(long now, double totalSpeed, IReadOnlyDictionary<string, double> perLink, bool bounded)
    {
        lock (_gate)
        {
            foreach (var link in _links)
                link.Speed = perLink.TryGetValue(link.Id, out var value) ? value : 0;
            if (_options.Fixed != null) return;
            if (now < _throttledUntil) return;
            if (now - _lastChange < _options.SettleMs) return;
            _sum += totalSpeed;
            _samples++;
            if (_samples < _options.WindowTicks) return;
            var average = _sum / _samples;
            _sum = 0;
            _samples = 0;
            if (bounded)
            {
                if (_probe == ProbeKind.Add && _probeLink != null) _probeLink.Target = Math.Max(1, _probeLink.Target - _probeStep);
                _probe = ProbeKind.None;
                _probeLink = null;
                _baseline = average;
                _hasBaseline = true;
                _saturated = false;
                return;
            }
            if (!_hasBaseline)
            {
                _baseline = average;
                _hasBaseline = true;
            }
            else if (_probe != ProbeKind.None)
            {
                Decide(now, average);
            }
            else
            {
                _baseline = average;
            }
            if (_probe == ProbeKind.None) Next(now);
        }
    }

    private void Decide(long now, double average)
    {
        var link = _probeLink!;
        var gain = (average - _baseline) / Math.Max(_baseline, 1);
        if (_probe == ProbeKind.Add)
        {
            var before = Math.Max(1, _links.Where(l => !l.Down).Sum(l => l.Target) - _probeStep);
            var linear = (double)_probeStep / before;
            if (gain >= Math.Max(0.03, Math.Min(_options.GainThreshold, linear * 0.5)))
            {
                link.CooldownMs = _options.HoldMs;
                link.NoGain = false;
                _step = gain >= linear * 0.5 ? Math.Min(_step * 2, _options.MaxStep) : 1;
                _baseline = average;
                _saturated = false;
            }
            else
            {
                link.Target = Math.Max(0, link.Target - _probeStep);
                if (_links.Where(l => !l.Down).Sum(l => l.Target) == 0) link.Target = 1;
                link.HoldUntil = now + link.CooldownMs;
                link.CooldownMs = Math.Min(link.CooldownMs * 2, 300000);
                if (link.Target == 0)
                {
                    link.NoGain = true;
                    link.HoldUntil = now + 300000;
                }
                _step = 1;
                _hasBaseline = false;
                _saturated = true;
            }
        }
        else if (gain <= -0.03)
        {
            link.Target += _probeStep;
            _hasBaseline = false;
        }
        else
        {
            _baseline = average;
        }
        _probe = ProbeKind.None;
        _probeLink = null;
        _lastChange = now;
    }

    private void Next(long now)
    {
        if (!_hasBaseline) return;
        var total = _links.Where(l => !l.Down).Sum(l => l.Target);
        LinkControl? choice = null;
        var bestScore = double.MinValue;
        var activeCanClimb = _links.Any(l => !l.Down && l.Target > 0 && now >= l.HoldUntil && l.Target + 1 <= l.Cap);
        foreach (var link in _links)
        {
            if (link.Down || now < link.HoldUntil) continue;
            if (link.Target + 1 > link.Cap) continue;
            if (link.Target == 0)
            {
                if (!_options.AutoSecondaryLinks) continue;
                if (activeCanClimb && total < 4) continue;
            }
            var score = link.Target == 0 ? double.MaxValue : link.Speed / link.Target;
            if (score > bestScore)
            {
                bestScore = score;
                choice = link;
            }
        }
        if (choice != null && total < _options.Max)
        {
            var step = Math.Min(_step, _options.Max - total);
            if (choice.Cap != int.MaxValue) step = Math.Min(step, choice.Cap - choice.Target);
            step = Math.Max(1, step);
            choice.Target += step;
            _probe = ProbeKind.Add;
            _probeLink = choice;
            _probeStep = step;
            _lastChange = now;
            return;
        }
        if (now - _lastRemoveProbe < 60000) return;
        var biggest = _links.Where(l => !l.Down && l.Target > 2).OrderByDescending(l => l.Target).FirstOrDefault();
        if (biggest == null) return;
        _lastRemoveProbe = now;
        biggest.Target -= 1;
        _probe = ProbeKind.Remove;
        _probeLink = biggest;
        _probeStep = 1;
        _lastChange = now;
    }
}
