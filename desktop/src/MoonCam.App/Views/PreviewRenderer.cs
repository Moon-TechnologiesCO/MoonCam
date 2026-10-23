using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using MoonCam.Core.Media;

namespace MoonCam.App.Views;

/// <summary>
/// Paints decoded frames into an <see cref="Image"/>. Frames arrive on the decoder thread; only the newest
/// one is kept, and painting happens on the UI thread at most once per UI frame, so a busy UI never slows
/// down the virtual camera.
/// </summary>
public sealed class PreviewRenderer(Image target) : IVideoSink
{
    private VideoFrame? _latest;
    private int _paintScheduled;
    private WriteableBitmap? _bitmap;

    public void Push(VideoFrame frame)
    {
        Interlocked.Exchange(ref _latest, frame);
        if (Interlocked.Exchange(ref _paintScheduled, 1) == 0)
            Dispatcher.UIThread.Post(Paint, DispatcherPriority.Render);
    }

    private unsafe void Paint()
    {
        Volatile.Write(ref _paintScheduled, 0);
        var frame = Interlocked.Exchange(ref _latest, null);
        if (frame is null)
            return;

        if (_bitmap is null || _bitmap.PixelSize.Width != frame.Width || _bitmap.PixelSize.Height != frame.Height)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            target.Source = _bitmap;
        }

        using (var buffer = _bitmap.Lock())
        {
            var destination = new Span<byte>((void*)buffer.Address, buffer.RowBytes * frame.Height);
            I420Converter.ToBgra(frame, destination, buffer.RowBytes);
        }
        target.InvalidateVisual();
    }
}
