using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using MoonCam.Core.Diagnostics;
using MoonCam.Core.Network;

namespace MoonCam.Platform.Linux.Network;

/// <summary>
/// Lists the networks the computer is on through NetworkManager and follows changes with <c>nmcli monitor</c>.
/// Each saved Wi-Fi network or wired profile has a stable UUID: that is what a trusted network refers to, the
/// same way GNOME's sharing settings remember networks. Without NetworkManager the list stays empty, so Wi-Fi
/// stays off and USB keeps working.
/// </summary>
public sealed class NetworkManagerMonitor(IEventLog log) : INetworkMonitor, IDisposable
{
    /// <summary>Connection types that join the computer to a network other devices are on.</summary>
    private static readonly HashSet<string> NetworkTypes =
        ["802-11-wireless", "802-3-ethernet", "gsm", "cdma", "bluetooth", "pppoe", "adsl", "infiniband"];

    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(300);
    private readonly CancellationTokenSource _stop = new();
    private IReadOnlyList<NetworkConnection> _current = [];
    private int _refreshQueued;

    public IReadOnlyList<NetworkConnection> Current => Volatile.Read(ref _current);

    public event Action? Changed;

    public void Start()
    {
        Refresh();
        _ = WatchAsync(_stop.Token);
    }

    /// <summary>Parses <c>nmcli -t -f UUID,TYPE,NAME connection show --active</c>, keeping real networks only.</summary>
    internal static IReadOnlyList<NetworkConnection> Parse(string output)
    {
        var networks = new List<NetworkConnection>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = SplitTerse(line);
            if (fields.Count == 3 && fields[0].Length > 0 && NetworkTypes.Contains(fields[1]))
                networks.Add(new NetworkConnection(fields[0], fields[2]));
        }
        return networks;
    }

    /// <summary>nmcli's terse format separates fields with ':' and escapes ':' and '\' inside values with '\'.</summary>
    private static List<string> SplitTerse(string line)
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '\\' && i + 1 < line.Length)
                field.Append(line[++i]);
            else if (line[i] == ':')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else
                field.Append(line[i]);
        }
        fields.Add(field.ToString());
        return fields;
    }

    private async Task WatchAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var monitor = Process.Start(Nmcli("monitor"))!;
                using var kill = cancellationToken.Register(() => Kill(monitor));
                while (await monitor.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is not null)
                    QueueRefresh();
                await Task.Delay(RestartDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Win32Exception e)
            {
                log.Warn("NetworkManager (nmcli) is not available, so MoonCam cannot tell which network it is on; Wi-Fi stays off", e);
                return;
            }
        }
    }

    /// <summary>One change prints several lines; refresh once after they settle.</summary>
    private void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1)
            return;
        _ = Task.Delay(SettleDelay, _stop.Token).ContinueWith(_ =>
        {
            Volatile.Write(ref _refreshQueued, 0);
            Refresh();
        }, TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    private void Refresh()
    {
        try
        {
            using var nmcli = Process.Start(Nmcli("-t", "-f", "UUID,TYPE,NAME", "connection", "show", "--active"))!;
            var output = nmcli.StandardOutput.ReadToEnd();
            nmcli.WaitForExit(3000);
            var next = Parse(output);
            if (next.SequenceEqual(Current))
                return;
            Volatile.Write(ref _current, next);
            log.Info($"Networks: {(next.Count == 0 ? "none" : string.Join(", ", next.Select(n => n.Name)))}");
            Changed?.Invoke();
        }
        catch (Win32Exception)
        {
            // Reported once by WatchAsync.
        }
    }

    private static ProcessStartInfo Nmcli(params string[] arguments)
    {
        var info = new ProcessStartInfo("nmcli") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        info.Environment["LC_ALL"] = "C"; // stable, untranslated output
        return info;
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill();
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }

    public void Dispose() => _stop.Cancel(); // ends nmcli monitor; the source is not disposed so late callbacks stay safe
}
