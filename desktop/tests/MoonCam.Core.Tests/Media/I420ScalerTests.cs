using MoonCam.Core.Media;

namespace MoonCam.Core.Tests.Media;

public class I420ScalerTests
{
    [Fact]
    public void Same_size_returns_the_same_frame()
    {
        var frame = Solid(4, 4, 100, 120, 140);

        Assert.Same(frame, I420Scaler.Scale(frame, 4, 4));
    }

    [Theory]
    [InlineData(1920, 1080, 1280, 720)]
    [InlineData(640, 360, 1280, 720)]
    public void A_flat_picture_stays_flat_at_any_size(int w, int h, int targetW, int targetH)
    {
        var scaled = I420Scaler.Scale(Solid(w, h, 90, 60, 200), targetW, targetH);

        Assert.Equal((targetW, targetH), (scaled.Width, scaled.Height));
        Assert.All(scaled.Y.ToArray(), v => Assert.Equal(90, v));
        Assert.All(scaled.U.ToArray(), v => Assert.Equal(60, v));
        Assert.All(scaled.V.ToArray(), v => Assert.Equal(200, v));
    }

    [Fact]
    public void Halving_averages_neighbouring_pixels()
    {
        var frame = Solid(4, 2, 0, 128, 128);
        var y = frame.Data;
        y[0] = 0; y[1] = 200; y[2] = 0; y[3] = 200; // first row alternates
        y[4] = 0; y[5] = 200; y[6] = 0; y[7] = 200;

        var scaled = I420Scaler.Scale(frame, 2, 2);

        Assert.All(scaled.Y.ToArray(), v => Assert.InRange(v, 95, 105));
    }

    [Fact]
    public void Odd_targets_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => I420Scaler.Scale(Solid(4, 4, 0, 0, 0), 3, 4));
    }

    private static VideoFrame Solid(int w, int h, byte y, byte u, byte v)
    {
        var data = new byte[VideoFrame.SizeOf(w, h)];
        data.AsSpan(0, w * h).Fill(y);
        data.AsSpan(w * h, w * h / 4).Fill(u);
        data.AsSpan(w * h * 5 / 4, w * h / 4).Fill(v);
        return new VideoFrame(w, h, data, 0);
    }
}
