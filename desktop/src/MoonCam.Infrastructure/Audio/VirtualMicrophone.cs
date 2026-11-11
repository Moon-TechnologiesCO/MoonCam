using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace MoonCam.Infrastructure.Audio;

/// <summary>
/// Makes the phone's microphone appear as "MoonCam Microphone", a PipeWire source every app can record from.
///
/// Pipeline: phone PCM (16-bit little-endian) → ffmpeg (high-pass and FFT noise reduction) → pw-cat → the
/// virtual source. Packets are dropped when the pipeline falls behind, so latency never grows.
/// </summary>
public sealed class VirtualMicrophone : IAsyncDisposable
{
    /// <summary>The sink the phone's audio is played into; it is the input of the loopback below.</summary>
    public const string SinkName = "mooncam_mic_sink";
    /// <summary>The source apps record from. Its description is what apps show.</summary>
    public const string SourceName = "mooncam_microphone";
    public const string Description = "MoonCam Microphone";
    public const int OutputRate = 48000;
    private const int QueueCapacity = 64;

    private readonly int _inputRate;
    private readonly int _inputChannels;
    private readonly Channel<byte[]> _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(QueueCapacity)
    {
        FullMode = BoundedChannelFullMode.Wait, // TryWrite returns false when full, so Submit can count the drop
        SingleReader = true,
    });
    private Process? _loopback;
    private Process? _ffmpeg;
    private Process? _pwcat;
    private Task? _writer;
    private Task? _pump;
    private long _dropped;

    public VirtualMicrophone(int inputRate, int inputChannels)
    {
        _inputRate = inputRate;
        _inputChannels = inputChannels;
    }

    /// <summary>Packets dropped because the pipeline could not keep up.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>ffmpeg arguments for the noise reduction stage (high-pass, then FFT denoise).</summary>
    public static IReadOnlyList<string> DenoiseArguments(int inputRate, int inputChannels) =>
    [
        "-hide_banner", "-loglevel", "error",
        "-f", "s16le", "-ar", inputRate.ToString(), "-ac", inputChannels.ToString(), "-i", "pipe:0",
        "-af", "highpass=f=80,afftdn=nr=18:nf=-30:tn=1",
        "-f", "s16le", "-ar", OutputRate.ToString(), "-ac", "1", "pipe:1",
    ];

    /// <summary>
    /// Creates the sink and the loopback that publishes it as a source, then starts the pipeline. Call once per
    /// phone connection. The loopback is a process we own, so Dispose removes the source again.
    /// </summary>
    public void Start()
    {
        EnsureSink();
        _loopback = Run("pw-loopback",
        [
            "-n", SourceName,
            "-C", $"{SinkName}.monitor", "-i", "{ node.passive=true }",
            "-o", $"{{ media.class=Audio/Source node.description=\"{Description}\" }}",
        ]);

        _ffmpeg = Run("ffmpeg", DenoiseArguments(_inputRate, _inputChannels));
        _pwcat = Run("pw-cat",
        [
            "--playback", "--raw", "--target", SinkName,
            "--rate", OutputRate.ToString(), "--channels", "1", "--format", "s16", "--latency", "40ms", "-",
        ]);

        _writer = Task.Run(() => WriteLoopAsync(_ffmpeg.StandardInput.BaseStream));
        _pump = Task.Run(() => PumpAsync(_ffmpeg.StandardOutput.BaseStream, _pwcat.StandardInput.BaseStream));
    }

    /// <summary>Queues one PCM packet from the phone. Never blocks the network reader.</summary>
    public void Submit(ReadOnlyMemory<byte> pcm)
    {
        if (!_queue.Writer.TryWrite(pcm.ToArray()))
            Interlocked.Increment(ref _dropped);
    }

    private async Task WriteLoopAsync(Stream toFfmpeg)
    {
        try
        {
            await foreach (var packet in _queue.Reader.ReadAllAsync())
                await toFfmpeg.WriteAsync(packet);
            await toFfmpeg.FlushAsync();
            toFfmpeg.Close();
        }
        catch (IOException)
        {
            // ffmpeg has exited; Stop reports it.
        }
    }

    private static async Task PumpAsync(Stream fromFfmpeg, Stream toPwCat)
    {
        var buffer = new byte[4096];
        try
        {
            int read;
            while ((read = await fromFfmpeg.ReadAsync(buffer)) > 0)
                await toPwCat.WriteAsync(buffer.AsMemory(0, read));
            toPwCat.Close();
        }
        catch (IOException)
        {
            // One of the processes exited; Stop cleans up.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        foreach (var process in new[] { _ffmpeg, _pwcat, _loopback })
        {
            if (process is null || process.HasExited)
                continue;
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync();
            process.Dispose();
        }
        if (_writer is not null) await _writer.ConfigureAwait(false);
        if (_pump is not null) await _pump.ConfigureAwait(false);
        DestroySink();
    }

    private static void EnsureSink()
    {
        if (FindNodeId(SinkName) is not null)
            return;
        var props = $"{{ factory.name=support.null-audio-sink node.name={SinkName} node.description=\"MoonCam sink\" " +
                    "media.class=Audio/Sink audio.position=[ MONO ] object.linger=true }";
        RunToEnd("pw-cli", ["create-node", "adapter", props]);
    }

    private static void DestroySink()
    {
        if (FindNodeId(SinkName) is { } id)
            RunToEnd("pw-cli", ["destroy", id.ToString()]);
    }

    private static int? FindNodeId(string nodeName)
    {
        var json = RunToEnd("pw-dump", []);
        if (json is null)
            return null;
        using var document = JsonDocument.Parse(json);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.TryGetProperty("info", out var info)
                && info.TryGetProperty("props", out var props)
                && props.TryGetProperty("node.name", out var name)
                && name.GetString() == nodeName
                && item.TryGetProperty("id", out var id))
                return id.GetInt32();
        }
        return null;
    }

    private static string? RunToEnd(string file, IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        try
        {
            using var process = Process.Start(info);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return output;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static Process Run(string file, IEnumerable<string> args)
    {
        var info = new ProcessStartInfo(file) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return Process.Start(info) ?? throw new IOException($"Could not start {file}.");
    }
}
