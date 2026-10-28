using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using MoonCam.Core.Media;

namespace MoonCam.App.Views;

/// <summary>
/// Paints decoded frames into an <see cref="Image"/> as cheaply as the screen allows. Colour conversion runs on
/// the decoder thread, shrunk to the size the preview is actually drawn at (a 1080p picture shown in a 760-pixel
/// box is converted at 960×540: a quarter of the work). The UI thread only copies the newest result into the
/// bitmap, at most once per UI frame. While the window is minimised nothing is converted at all.
/// </summary>
public sealed class PreviewRenderer(Image target) : IVideoSink
{
    private const int MaxShrink = 4;
    private readonly Lock _lock = new();
    private byte[]? _ready; // newest converted picture, waiting for the UI thread
    private byte[]? _spare; // a buffer to convert the next picture into
    private PixelSize _readySize;
    private int _paintScheduled;
    private volatile bool _isShown = true;
    private volatile int _targetWidth = 1280;
    private volatile int _targetHeight = 720;
    private WriteableBitmap? _bitmap;

    /// <summary>False while the window is minimised: frames are skipped instead of converted.</summary>
    public bool IsShown
    {
        get => _isShown;
        set => _isShown = value;
    }

    /// <summary>The preview's size on screen, in device pixels.</summary>
    public void SetTargetSize(int width, int height)
    {
        _targetWidth = Math.Max(1, width);
        _targetHeight = Math.Max(1, height);
    }

    public void Push(VideoFrame frame)
    {
        if (!_isShown)
            return;
        var factor = Math.Clamp(Math.Min(frame.Width / _targetWidth, frame.Height / _targetHeight), 1, MaxShrink);
        var size = new PixelSize(frame.Width / factor, frame.Height / factor);
        var length = size.Width * size.Height * 4;

        byte[] buffer;
        lock (_lock)
        {
            buffer = _spare is { } spare && spare.Length == length ? spare : new byte[length];
            _spare = null;
        }
        I420Converter.ToBgra(frame, buffer, size.Width * 4, factor);
        lock (_lock)
        {
            _spare = _ready is { } older && older.Length == length ? older : null;
            _ready = buffer;
            _readySize = size;
        }
        if (Interlocked.Exchange(ref _paintScheduled, 1) == 0)
            Dispatcher.UIThread.Post(Paint, DispatcherPriority.Render);
    }

    private unsafe void Paint()
    {
        Volatile.Write(ref _paintScheduled, 0);
        byte[]? picture;
        PixelSize size;
        lock (_lock)
        {
            picture = _ready;
            size = _readySize;
            _ready = null;
        }
        if (picture is null)
            return;

        if (_bitmap is null || _bitmap.PixelSize != size)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            target.Source = _bitmap;
        }

        using (var buffer = _bitmap.Lock())
        {
            var rowBytes = size.Width * 4;
            for (var row = 0; row < size.Height; row++)
                picture.AsSpan(row * rowBytes, rowBytes).CopyTo(new Span<byte>((byte*)buffer.Address + row * buffer.RowBytes, rowBytes));
        }
        lock (_lock)
            _spare ??= picture;
        target.InvalidateVisual();
    }
}
