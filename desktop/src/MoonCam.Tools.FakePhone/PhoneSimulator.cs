using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using MoonCam.Core.Media;
using MoonCam.Core.Protocol;

namespace MoonCam.Tools.FakePhone;

/// <summary>
/// Behaves like the MoonCam Android app on the wire: handshake, capabilities, then H.264 video produced by
/// the ffmpeg command-line tool, split and timestamped exactly as MediaCodec output would be.
/// </summary>
public sealed class PhoneSimulator(string host, int port, string token)
{
    private static readonly CapabilitiesMessage FakeCapabilities = new([
        new CameraInfo("0", "back", "wide", null, 5.4, [new VideoSize(1920, 1080), new VideoSize(1280, 720), new VideoSize(854, 480)], [30, 60]),
        new CameraInfo("1", "front", "wide", null, 3.1, [new VideoSize(1280, 720)], [30]),
    ]);

    public event Action<string>? Log;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
        var stream = tcp.GetStream();
        var reader = new FrameReader(stream);
        using var writer = new FrameWriter(stream);

        await writer.WritePreambleAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        await reader.ReadPreambleAsync(cancellationToken).ConfigureAwait(false);
        await writer.WriteAsync(Messages.Hello(new HelloMessage("MoonCam FakePhone", "Moon Technologies", "FakePhone", "16", "0.1.0", token)), cancellationToken).ConfigureAwait(false);
        var welcome = Messages.ReadWelcome(await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                                           ?? throw new EndOfStreamException("No WELCOME."));
        if (!welcome.Accepted)
            throw new UnauthorizedAccessException(welcome.Reason ?? "Rejected by the desktop.");
        Log?.Invoke($"Connected to {welcome.ServerName}.");

        await writer.WriteAsync(Messages.Capabilities(FakeCapabilities), cancellationToken).ConfigureAwait(false);

        CancellationTokenSource? streaming = null;
        try
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) is { } frame)
            {
                switch (frame.Type)
                {
                    case FrameType.Ping:
                        await writer.WriteAsync(Heartbeat.Pong(frame), cancellationToken).ConfigureAwait(false);
                        break;
                    case FrameType.Control:
                        var control = Messages.ReadControl(frame);
                        if (control.Op is ControlOps.Start or ControlOps.Stop && streaming is not null)
                        {
                            await streaming.CancelAsync().ConfigureAwait(false);
                            streaming.Dispose();
                            streaming = null;
                        }
                        if (control.Op == ControlOps.Start)
                        {
                            streaming = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                            _ = StreamAsync(control, writer, streaming.Token);
                        }
                        break;
                    case FrameType.Bye:
                        Log?.Invoke($"Desktop said goodbye: {Messages.ReadBye(frame).Reason}");
                        return;
                }
            }
        }
        finally
        {
            if (streaming is not null)
                await streaming.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task StreamAsync(ControlMessage start, FrameWriter writer, CancellationToken cancellationToken)
    {
        int width = start.Width ?? 1280, height = start.Height ?? 720, fps = start.Fps ?? 30;
        Log?.Invoke($"Streaming {width}x{height} at {fps} fps, {start.BitrateBps / 1000 ?? 0} kbit/s.");
        var info = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-re", "-f", "lavfi",
                     "-i", $"testsrc2=size={width}x{height}:rate={fps}",
                     "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-bf", "0",
                     "-g", fps.ToString(CultureInfo.InvariantCulture), "-b:v", (start.BitrateBps ?? 3_000_000).ToString(CultureInfo.InvariantCulture),
                     "-pix_fmt", "yuv420p", "-bsf:v", "h264_metadata=aud=insert", "-f", "h264", "-",
                 })
            info.ArgumentList.Add(arg);

        using var ffmpeg = Process.Start(info) ?? throw new InvalidOperationException("Could not start ffmpeg.");
        try
        {
            var splitter = new AccessUnitSplitter();
            var buffer = new byte[64 * 1024];
            var configSent = false;
            var clock = Stopwatch.StartNew();
            int read;
            while ((read = await ffmpeg.StandardOutput.BaseStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                foreach (var unit in splitter.Push(buffer.AsSpan(0, read)))
                {
                    var (parameterSets, slices) = Separate(unit);
                    if (!configSent && parameterSets.Count >= 2)
                    {
                        await writer.WriteAsync(new VideoConfig(VideoCodec.H264, width, height, fps, AnnexB.Join(parameterSets)).ToFrame(), cancellationToken).ConfigureAwait(false);
                        configSent = true;
                    }
                    if (!configSent || slices.Count == 0)
                        continue;
                    var data = AnnexB.Join(slices);
                    var flags = AnnexB.ContainsIdr(data) ? FrameFlags.KeyFrame : FrameFlags.None;
                    await writer.WriteAsync(new Frame(FrameType.VideoFrame, data, flags, (long)clock.Elapsed.TotalMicroseconds), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            // The desktop went away.
        }
        finally
        {
            if (!ffmpeg.HasExited)
                ffmpeg.Kill();
        }
    }

    /// <summary>Like MediaCodec: parameter sets travel apart from the picture data; delimiters are dropped.</summary>
    private static (List<ReadOnlyMemory<byte>> ParameterSets, List<ReadOnlyMemory<byte>> Slices) Separate(byte[] unit)
    {
        var parameterSets = new List<ReadOnlyMemory<byte>>();
        var slices = new List<ReadOnlyMemory<byte>>();
        foreach (var (offset, length) in AnnexB.FindNalUnits(unit))
        {
            var nal = unit.AsMemory(offset, length);
            switch (AnnexB.H264Type(nal.Span))
            {
                case AnnexB.NalSps or AnnexB.NalPps: parameterSets.Add(nal); break;
                case AnnexB.NalAccessUnitDelimiter: break;
                default: slices.Add(nal); break;
            }
        }
        return (parameterSets, slices);
    }
}
