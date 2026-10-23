using System.Diagnostics;
using MoonCam.Core.Setup;
using MoonCam.Platform.Linux.Video;

namespace MoonCam.Platform.Linux.Setup;

/// <summary>
/// Checks for and installs MoonCam's system dependencies. Installation runs the root-owned
/// <c>mooncam-setup</c> script through pkexec, so the user approves it in the standard system password dialog
/// and never has to type commands.
/// </summary>
public sealed class LinuxSystemSetup(Func<bool> isVideoDecoderAvailable, string? setupScriptPath = null) : ISystemSetup
{
    private const int PkexecDismissed = 126;
    private const int SecureBootExitCode = 3;
    private const int UnsupportedKernelExitCode = 4;

    private readonly string _script = setupScriptPath ?? FindScript();

    public IReadOnlyList<Component> Check() =>
    [
        new("USB connection (adb)", "Talks to your phone over the cable.", CommandExists("adb")),
        new("Virtual camera", "Lets every app use your phone as a webcam.", V4l2LoopbackCamera.FindDevices().Count > 0),
        new("Video decoder (FFmpeg)", "Turns the phone's video into pictures.", isVideoDecoderAvailable()),
    ];

    public async Task<SetupResult> InstallAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_script))
            return new SetupResult(SetupOutcome.Failed, $"The setup helper is missing ({_script}). Reinstall MoonCam.");

        var info = new ProcessStartInfo("pkexec") { RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(_script);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start pkexec.");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var errors = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return process.ExitCode switch
        {
            0 => new SetupResult(SetupOutcome.Ready, "Everything is installed."),
            PkexecDismissed => new SetupResult(SetupOutcome.Cancelled, "Installation was cancelled."),
            SecureBootExitCode => new SetupResult(SetupOutcome.SecureBootEnrollmentRequired,
                "Secure Boot needs to trust the virtual camera driver once."),
            UnsupportedKernelExitCode => new SetupResult(SetupOutcome.Failed,
                "This kernel has no signed virtual camera driver. Use one of Ubuntu's standard kernels."),
            _ => new SetupResult(SetupOutcome.Failed, (errors + output).Trim()),
        };
    }

    /// <summary>
    /// Packaged builds install the helper root-owned in /usr/libexec/mooncam; development builds use the copy
    /// next to the executable.
    /// </summary>
    private static string FindScript()
    {
        const string packaged = "/usr/libexec/mooncam/mooncam-setup";
        return File.Exists(packaged) ? packaged : Path.Combine(AppContext.BaseDirectory, "mooncam-setup");
    }

    private static bool CommandExists(string name)
        => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(dir => File.Exists(Path.Combine(dir, name)));
}
