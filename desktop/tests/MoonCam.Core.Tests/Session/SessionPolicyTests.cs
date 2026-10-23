using Microsoft.Extensions.Time.Testing;
using MoonCam.Core.Protocol;
using MoonCam.Core.Session;

namespace MoonCam.Core.Tests.Session;

public class SessionPolicyTests
{
    [Fact]
    public void State_machine_follows_the_happy_path_and_reports_changes()
    {
        var machine = new ConnectionStateMachine();
        var seen = new List<ConnectionState>();
        machine.Changed += (_, now) => seen.Add(now);

        Assert.True(machine.TryMoveTo(ConnectionState.Listening));
        Assert.True(machine.TryMoveTo(ConnectionState.Handshaking));
        Assert.True(machine.TryMoveTo(ConnectionState.Connected));
        Assert.True(machine.TryMoveTo(ConnectionState.Streaming));
        Assert.True(machine.TryMoveTo(ConnectionState.Reconnecting));

        Assert.Equal([ConnectionState.Listening, ConnectionState.Handshaking, ConnectionState.Connected,
            ConnectionState.Streaming, ConnectionState.Reconnecting], seen);
    }

    [Fact]
    public void Illegal_transitions_are_refused_without_throwing()
    {
        var machine = new ConnectionStateMachine();

        Assert.False(machine.TryMoveTo(ConnectionState.Streaming));
        Assert.Equal(ConnectionState.Stopped, machine.State);
    }

    [Theory]
    [InlineData(1280, 720, 30, StreamQuality.Balanced, 2_800_000)]
    [InlineData(1920, 1080, 30, StreamQuality.Balanced, 6_200_000)]
    [InlineData(1920, 1080, 60, StreamQuality.High, 18_700_000)]
    [InlineData(854, 480, 30, StreamQuality.Low, 700_000)]
    public void Bitrate_scales_with_pixels_frame_rate_and_quality(int w, int h, int fps, StreamQuality q, int expected)
    {
        Assert.Equal(expected, new StreamSettings("0", w, h, fps, q).BitrateBps);
    }

    [Fact]
    public void Settings_validation_explains_every_problem()
    {
        var errors = new StreamSettings("", 1281, 720, 120).Validate();

        Assert.Equal(3, errors.Count);
        Assert.Empty(StreamSettings.Default.Validate());
    }

    [Fact]
    public void Start_command_carries_the_settings()
    {
        var start = new StreamSettings("2", 1920, 1080, 60, Codec: VideoCodec.H265).ToStartCommand();

        Assert.Equal((ControlOps.Start, "2", 1920, 1080, 60, "h265"), (start.Op, start.Camera, start.Width, start.Height, start.Fps, start.Codec));
    }

    [Fact]
    public void Heartbeat_times_out_after_silence_and_recovers_on_data()
    {
        var time = new FakeTimeProvider();
        var monitor = new HeartbeatMonitor(time, TimeSpan.FromSeconds(3));

        time.Advance(TimeSpan.FromSeconds(2.9));
        Assert.False(monitor.IsTimedOut);
        time.Advance(TimeSpan.FromSeconds(0.2));
        Assert.True(monitor.IsTimedOut);

        monitor.MarkReceived();
        Assert.False(monitor.IsTimedOut);
    }

    [Fact]
    public void Tokens_are_unique_url_safe_and_compared_exactly()
    {
        var a = SessionToken.Create();
        var b = SessionToken.Create();

        Assert.NotEqual(a, b);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", a);
        Assert.True(SessionToken.Matches(a, a));
        Assert.False(SessionToken.Matches(a, b));
        Assert.False(SessionToken.Matches(a, null));
        Assert.False(SessionToken.Matches("", ""));
    }
}
