using MoonCam.Platform.Linux.Audio;

namespace MoonCam.Platform.Linux.Tests;

public class LinuxMicrophonesTests
{
    [Fact]
    public void Keeps_audio_inputs_and_skips_monitors_and_outputs()
    {
        const string json = """
        [
          {"type":"PipeWire:Interface:Node","info":{"props":{"media.class":"Audio/Sink","node.name":"sink","node.description":"Speakers"}}},
          {"type":"PipeWire:Interface:Node","info":{"props":{"media.class":"Audio/Source","node.name":"sink.monitor","node.description":"Monitor"}}},
          {"type":"PipeWire:Interface:Node","info":{"props":{"media.class":"Audio/Source","node.name":"alsa_input.mic","node.description":"Built-in mic"}}},
          {"type":"PipeWire:Interface:Node","info":{"props":{"media.class":"Audio/Source","node.name":"mooncam_phone"}}}
        ]
        """;

        var microphones = LinuxMicrophones.Parse(json);

        Assert.Equal(2, microphones.Count);
        Assert.Equal(new Microphone("alsa_input.mic", "Built-in mic"), microphones[0]);
        Assert.Equal(new Microphone("mooncam_phone", "mooncam_phone"), microphones[1]);
    }

    [Fact]
    public void Empty_dump_gives_no_microphones()
        => Assert.Empty(LinuxMicrophones.Parse("[]"));
}
