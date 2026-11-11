using System.Diagnostics;
using MoonCam.Infrastructure.Audio;

namespace MoonCam.Infrastructure.Tests.Audio;

/// <summary>Runs against the real PipeWire on this computer. Set MOONCAM_PIPEWIRE_TESTS=1 to enable.</summary>
public class VirtualMicrophonePipeWireTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("MOONCAM_PIPEWIRE_TESTS") == "1";

    [Fact]
    public async Task Phone_audio_reaches_the_MoonCam_source()
    {
        if (!Enabled)
            return; // disabled: CI has no PipeWire

        await using var microphone = new VirtualMicrophone(48000, 1);
        microphone.Start();

        // Two seconds of a 440 Hz tone, sent in 20 ms packets like the phone does.
        var packet = new byte[960 * 2];
        for (var i = 0; i < 100; i++)
        {
            for (var n = 0; n < 960; n++)
            {
                var t = (i * 960 + n) / 48000.0;
                var sample = (short)(Math.Sin(2 * Math.PI * 440 * t) * 8000);
                packet[n * 2] = (byte)(sample & 0xFF);
                packet[n * 2 + 1] = (byte)(sample >> 8);
            }
            microphone.Submit(packet);
            await Task.Delay(20);
        }

        // Record one second from the MoonCam source and check that it is not silent.
        var file = Path.Combine(Path.GetTempPath(), $"mooncam-test-{Guid.NewGuid():N}.raw");
        try
        {
            using var record = Process.Start(new ProcessStartInfo("pw-cat")
            {
                ArgumentList = { "--record", "--raw", "--target", VirtualMicrophone.SourceName, "--rate", "48000", "--channels", "1", "--format", "s16", file },
            })!;
            await Task.Delay(1000);
            record.Kill();
            await record.WaitForExitAsync();

            var bytes = File.ReadAllBytes(file);
            Assert.True(bytes.Length > 0, "nothing was recorded");
            Assert.Contains(bytes, b => b != 0);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
