namespace MoonCam.Core.Session;

/// <summary>
/// Tracks when we last heard from the peer. Any frame counts, not only PONGs: a phone busy sending video is
/// clearly alive. Time comes from a <see cref="TimeProvider"/> so tests can move the clock by hand.
/// </summary>
public sealed class HeartbeatMonitor(TimeProvider time, TimeSpan timeout)
{
    private long _lastReceived = time.GetTimestamp();

    public TimeSpan Timeout { get; } = timeout;

    public void MarkReceived() => Interlocked.Exchange(ref _lastReceived, time.GetTimestamp());

    public TimeSpan SinceLastReceived => time.GetElapsedTime(Interlocked.Read(ref _lastReceived));

    public bool IsTimedOut => SinceLastReceived >= Timeout;
}
