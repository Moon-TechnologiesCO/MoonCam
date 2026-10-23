using System.Globalization;
using MoonCam.Tools.FakePhone;

// FakePhone: streams a test pattern to a MoonCam desktop exactly like the Android app would.
// Usage: dotnet run --project src/MoonCam.Tools.FakePhone -- --token <token> [--host 127.0.0.1] [--port 47321]

var options = ParseArguments(args);
if (!options.TryGetValue("token", out var token))
{
    Console.Error.WriteLine("Usage: fakephone --token <token shown by MoonCam> [--host 127.0.0.1] [--port 47321]");
    return 2;
}

var host = options.GetValueOrDefault("host", "127.0.0.1");
var port = int.Parse(options.GetValueOrDefault("port", "47321"), CultureInfo.InvariantCulture);
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

var phone = new PhoneSimulator(host, port, token);
phone.Log += message => Console.WriteLine($"[fakephone] {message}");
try
{
    await phone.RunAsync(stop.Token);
    return 0;
}
catch (OperationCanceledException)
{
    return 0;
}
catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"[fakephone] {e.Message}");
    return 1;
}

static Dictionary<string, string> ParseArguments(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i + 1 < args.Length; i += 2)
    {
        if (args[i].StartsWith("--", StringComparison.Ordinal))
            result[args[i][2..]] = args[i + 1];
    }
    return result;
}
