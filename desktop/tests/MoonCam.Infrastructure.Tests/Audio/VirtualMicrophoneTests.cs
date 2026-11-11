using MoonCam.Infrastructure.Audio;

namespace MoonCam.Infrastructure.Tests.Audio;

public class VirtualMicrophoneTests
{
    [Fact]
    public void Denoise_stage_resamples_to_48k_mono_and_removes_rumble_and_noise()
    {
        var args = VirtualMicrophone.DenoiseArguments(44100, 2).ToList();

        Assert.Equal("44100", args[args.IndexOf("-ar") + 1]);     // input rate
        Assert.Equal("2", args[args.IndexOf("-ac") + 1]);          // input channels
        Assert.Contains("highpass=f=100", args[args.IndexOf("-af") + 1]);
        Assert.Contains("afftdn=", args[args.IndexOf("-af") + 1]);
        Assert.Equal(VirtualMicrophone.OutputRate.ToString(), args[args.LastIndexOf("-ar") + 1]); // output rate
        Assert.Equal("pipe:1", args[^1]);
    }

    [Fact]
    public void Packets_are_dropped_instead_of_queued_forever_when_the_pipeline_is_behind()
    {
        // Not started: nothing drains the queue, so the bounded queue fills and extra packets are counted as dropped.
        var microphone = new VirtualMicrophone(48000, 1);
        var packet = new byte[960];
        for (var i = 0; i < 200; i++)
            microphone.Submit(packet);

        Assert.True(microphone.Dropped > 0);
    }

    [Fact]
    public void Without_noise_reduction_the_sound_is_only_resampled()
    {
        var args = VirtualMicrophone.DenoiseArguments(44100, 2, noiseReduction: false).ToList();

        Assert.Equal("anull", args[args.IndexOf("-af") + 1]);
        Assert.Equal(VirtualMicrophone.OutputRate.ToString(), args[args.LastIndexOf("-ar") + 1]);
        Assert.Equal("1", args[args.LastIndexOf("-ac") + 1]);
    }

    [Fact]
    public async Task Noise_reduction_can_be_switched_before_starting()
    {
        await using var microphone = new VirtualMicrophone(48000, 1);
        Assert.True(microphone.NoiseReduction);

        await microphone.SetNoiseReductionAsync(false);

        Assert.False(microphone.NoiseReduction);
    }
}
