using System.Collections.Immutable;
using System.Threading.Channels;
using MoonCam.Core.Media;
using MoonCam.Core.Protocol;

namespace MoonCam.Infrastructure.Decoding;

/// <summary>
/// Decodes video on its own thread and hands pictures to the sinks (preview, virtual camera).
///
/// The network thread never waits for the decoder. If decoding falls behind and the small queue fills up,
/// everything queued is thrown away, the pipeline skips frames until the next key frame, and
/// <see cref="KeyFrameNeeded"/> asks the phone for one: a short freeze instead of growing delay or artefacts.
/// </summary>
public sealed class VideoPipeline : IAsyncDisposable
{
    private const int QueueCapacity = 8;

    private readonly IVideoDecoder _decoder;
    private readonly Channel<object> _queue = Channel.CreateBounded<object>(new BoundedChannelOptions(QueueCapacity)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait,
    });
    private readonly Task _worker;
    private ImmutableArray<IVideoSink> _sinks = [];
    private VideoConfig? _pendingConfig; // a configuration rescued from a drained queue; never lost
    private volatile bool _waitingForKeyFrame = true;
    private long _decoded;
    private long _dropped;

    public VideoPipeline(IVideoDecoder decoder)
    {
        _decoder = decoder;
        _worker = Task.Run(RunAsync);
    }

    /// <summary>Raised (on the network thread) when frames were dropped and a key frame is required.</summary>
    public event Action? KeyFrameNeeded;

    /// <summary>Raised on the decoder thread when a frame cannot be decoded. Decoding continues.</summary>
    public event Action<Exception>? DecodeFailed;

    /// <summary>Raised on the decoder thread when a sink throws. The other sinks keep receiving pictures.</summary>
    public event Action<IVideoSink, Exception>? SinkFailed;

    public long FramesDecoded => Interlocked.Read(ref _decoded);
    public long FramesDropped => Interlocked.Read(ref _dropped);

    public void AddSink(IVideoSink sink) => ImmutableInterlocked.Update(ref _sinks, s => s.Add(sink));

    public void RemoveSink(IVideoSink sink) => ImmutableInterlocked.Update(ref _sinks, s => s.Remove(sink));

    public void Configure(VideoConfig config)
    {
        _waitingForKeyFrame = true;
        if (!_queue.Writer.TryWrite(config))
        {
            Drain();
            _queue.Writer.TryWrite(config);
        }
    }

    /// <summary>Queues an encoded frame. Never blocks.</summary>
    public void Submit(Frame frame)
    {
        if (_waitingForKeyFrame && !frame.IsKeyFrame)
        {
            Interlocked.Increment(ref _dropped);
            return;
        }
        _waitingForKeyFrame = false;

        if (_queue.Writer.TryWrite(frame))
            return;

        // Decoder is behind: drop the backlog and resynchronise on the next key frame.
        Drain();
        _waitingForKeyFrame = !frame.IsKeyFrame;
        if (frame.IsKeyFrame)
            _queue.Writer.TryWrite(frame);
        else
            Interlocked.Increment(ref _dropped);
        KeyFrameNeeded?.Invoke();
    }

    private void Drain()
    {
        while (_queue.Reader.TryRead(out var item))
        {
            if (item is Frame)
                Interlocked.Increment(ref _dropped);
            else if (item is VideoConfig config)
                Interlocked.Exchange(ref _pendingConfig, config);
        }
    }

    private async Task RunAsync()
    {
        await foreach (var item in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            IReadOnlyList<VideoFrame> pictures;
            try
            {
                if (Interlocked.Exchange(ref _pendingConfig, null) is { } pending)
                    _decoder.Configure(pending);
                switch (item)
                {
                    case VideoConfig config:
                        _decoder.Configure(config);
                        continue;
                    case Frame frame:
                        pictures = _decoder.Decode(frame.Payload, frame.IsKeyFrame, frame.TimestampUs);
                        break;
                    default:
                        continue;
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // Fault boundary: a bad frame must never stop the video thread.
                DecodeFailed?.Invoke(e);
                continue;
            }

            foreach (var picture in pictures)
            {
                Interlocked.Increment(ref _decoded);
                foreach (var sink in _sinks)
                    Deliver(sink, picture);
            }
        }
    }

    /// <summary>Fault boundary per sink: a failing virtual camera must not freeze the preview, and vice versa.</summary>
    private void Deliver(IVideoSink sink, VideoFrame picture)
    {
        try
        {
            sink.Push(picture);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            SinkFailed?.Invoke(sink, e);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _worker.ConfigureAwait(false);
        _decoder.Dispose();
    }
}
