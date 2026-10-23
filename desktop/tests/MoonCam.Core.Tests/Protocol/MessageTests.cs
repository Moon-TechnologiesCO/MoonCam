using System.Text;
using MoonCam.Core.Protocol;

namespace MoonCam.Core.Tests.Protocol;

public class MessageTests
{
    [Fact]
    public void Control_message_uses_camel_case_and_omits_nulls()
    {
        var frame = Messages.Control(new ControlMessage(ControlOps.KeyFrame));

        Assert.Equal("""{"op":"keyFrame"}""", Encoding.UTF8.GetString(frame.Payload.Span));
    }

    [Fact]
    public void Hello_round_trips()
    {
        var hello = new HelloMessage("Pixel 9", "Google", "Pixel 9", "16", "0.1.0", "tok");

        Assert.Equal(hello, Messages.ReadHello(Messages.Hello(hello)));
    }

    [Fact]
    public void Capabilities_round_trip_with_lenses()
    {
        var caps = new CapabilitiesMessage([
            new CameraInfo("0", "back", "wide", null, 6.9, [new VideoSize(1920, 1080)], [30, 60]),
            new CameraInfo("0", "back", "ultraWide", "2", 2.2, [new VideoSize(1280, 720)], [30]),
        ]);

        var read = Messages.ReadCapabilities(Messages.Capabilities(caps));

        Assert.Equal(2, read.Cameras.Count);
        Assert.Equal("2", read.Cameras[1].PhysicalId);
        Assert.Equal(new VideoSize(1920, 1080), read.Cameras[0].Sizes[0]);
    }

    [Fact]
    public void Reading_the_wrong_type_or_bad_json_is_a_protocol_error()
    {
        Assert.Throws<ProtocolException>(() => Messages.ReadHello(Messages.Bye("x")));
        Assert.Throws<ProtocolException>(() => Messages.ReadControl(new Frame(FrameType.Control, "{not json"u8.ToArray())));
    }

    [Fact]
    public void Video_config_round_trips_and_rejects_odd_sizes()
    {
        var config = new VideoConfig(VideoCodec.H264, 1920, 1080, 60, new byte[] { 0, 0, 0, 1, 0x67 });

        var read = VideoConfig.FromFrame(config.ToFrame());

        Assert.Equal((1920, 1080, 60), (read.Width, read.Height, read.Fps));
        Assert.Equal(config.CodecData.ToArray(), read.CodecData.ToArray());
        Assert.Throws<ProtocolException>(() => VideoConfig.FromFrame(new VideoConfig(VideoCodec.H264, 1921, 1080, 30, default).ToFrame()));
    }

    [Fact]
    public void Audio_config_rejects_unsupported_channel_counts()
    {
        var frame = new AudioConfig(AudioCodec.Opus, 48000, 6, default).ToFrame();

        Assert.Throws<ProtocolException>(() => AudioConfig.FromFrame(frame));
    }
}
