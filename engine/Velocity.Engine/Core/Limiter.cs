using System.Diagnostics;

namespace Velocity.Engine.Core;

public sealed class TokenBucket
{
    private readonly object _gate = new();
    private double _tokens;
    private long _last = Stopwatch.GetTimestamp();
    private long _rate;
    private long _waitedMs;

    public TokenBucket(long bytesPerSecond = 0) => _rate = bytesPerSecond;

    public long Rate
    {
        get => Volatile.Read(ref _rate);
        set => Volatile.Write(ref _rate, value);
    }

    public long WaitedMs => Interlocked.Read(ref _waitedMs);

    public int MaxChunk
    {
        get
        {
            var rate = Rate;
            if (rate <= 0) return int.MaxValue;
            return (int)Math.Clamp(rate / 20, 4 * 1024, 1024 * 1024);
        }
    }

    public ValueTask TakeAsync(int count, CancellationToken ct)
    {
        var rate = Rate;
        if (rate <= 0) return ValueTask.CompletedTask;
        double wait;
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = (now - _last) / (double)Stopwatch.Frequency;
            _last = now;
            _tokens = Math.Min(_tokens + elapsed * rate, rate * 0.25);
            _tokens -= count;
            wait = _tokens >= 0 ? 0 : -_tokens / rate;
        }
        if (wait <= 0) return ValueTask.CompletedTask;
        Interlocked.Add(ref _waitedMs, (long)(wait * 1000));
        return new ValueTask(Task.Delay(TimeSpan.FromSeconds(wait), ct));
    }
}
