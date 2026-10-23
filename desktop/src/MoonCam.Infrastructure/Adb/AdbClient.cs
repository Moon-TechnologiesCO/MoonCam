using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MoonCam.Infrastructure.Adb;

public enum AdbDeviceState
{
    Device,
    Unauthorized,
    Offline,
    Other,
}

/// <summary>A phone as reported by <c>adb devices -l</c>.</summary>
public sealed record AdbDevice(string Serial, AdbDeviceState State, string? Model)
{
    public bool IsReady => State == AdbDeviceState.Device;
}

/// <summary>Pure parsing of adb output, kept separate from process handling so it can be unit-tested.</summary>
public static partial class AdbOutput
{
    [GeneratedRegex(@"^(?<serial>\S+)\s+(?<state>\S+)(?<rest>.*)$")]
    private static partial Regex DeviceLine();

    [GeneratedRegex(@"\bmodel:(?<model>\S+)")]
    private static partial Regex ModelField();

    public static IReadOnlyList<AdbDevice> ParseDevices(string output)
    {
        var devices = new List<AdbDevice>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.Ordinal) || line.StartsWith('*'))
                continue;
            var match = DeviceLine().Match(line);
            if (!match.Success)
                continue;
            var state = match.Groups["state"].Value switch
            {
                "device" => AdbDeviceState.Device,
                "unauthorized" => AdbDeviceState.Unauthorized,
                "offline" => AdbDeviceState.Offline,
                _ => AdbDeviceState.Other,
            };
            var model = ModelField().Match(match.Groups["rest"].Value);
            devices.Add(new AdbDevice(match.Groups["serial"].Value, state, model.Success ? model.Groups["model"].Value.Replace('_', ' ') : null));
        }
        return devices;
    }
}

/// <summary>
/// Talks to the phone through the <c>adb</c> command-line tool. Arguments are always passed as an argument
/// list (never through a shell), so device serials or tokens can never inject commands.
/// </summary>
public sealed class AdbClient(string adbPath = "adb")
{
    public const string AppPackage = "io.github.moontechnologiesco.mooncam";
    public const string ConnectAction = AppPackage + ".action.CONNECT_USB";

    public async Task<bool> IsInstalledAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return (await RunAsync(["version"], cancellationToken).ConfigureAwait(false)).ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // adb is not on PATH
        }
    }

    public async Task<IReadOnlyList<AdbDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
        => AdbOutput.ParseDevices((await RunCheckedAsync(["devices", "-l"], cancellationToken).ConfigureAwait(false)).Output);

    /// <summary>Makes 127.0.0.1:<paramref name="port"/> on the phone reach the same port on this computer.</summary>
    public Task ReverseAsync(string serial, int port, CancellationToken cancellationToken = default)
        => RunCheckedAsync(["-s", serial, "reverse", $"tcp:{port}", $"tcp:{port}"], cancellationToken);

    public Task RemoveReverseAsync(string serial, int port, CancellationToken cancellationToken = default)
        => RunCheckedAsync(["-s", serial, "reverse", "--remove", $"tcp:{port}"], cancellationToken);

    /// <summary>
    /// Opens MoonCam on the phone with an explicit intent carrying the session token. Explicit intents are
    /// delivered only to MoonCam, which is what makes the token a secret shared with the app alone.
    /// </summary>
    public Task LaunchAppAsync(string serial, string token, int port, CancellationToken cancellationToken = default)
        => RunCheckedAsync(
            ["-s", serial, "shell", "am", "start", "-n", $"{AppPackage}/.MainActivity", "-a", ConnectAction,
             "--es", "token", token, "--ei", "port", port.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            cancellationToken);

    private async Task<(int ExitCode, string Output)> RunCheckedAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var result = await RunAsync(arguments, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"adb {arguments.FirstOrDefault(a => !a.StartsWith('-'))} failed: {result.Output.Trim()}");
        return result;
    }

    private async Task<(int ExitCode, string Output)> RunAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(adbPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);

        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start adb.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return (process.ExitCode, await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false));
    }
}
