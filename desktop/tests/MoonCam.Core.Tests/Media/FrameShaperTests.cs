using MoonCam.Core.Media;

namespace MoonCam.Core.Tests.Media;

public class FrameShaperTests
{
    [Theory]
    [InlineData(FrameShape.Wide, 1280, 720)]
    [InlineData(FrameShape.Standard, 960, 720)]
    [InlineData(FrameShape.Square, 720, 720)]
    [InlineData(FrameShape.Circle, 720, 720)]
    [InlineData(FrameShape.Vertical, 404, 720)]
    [InlineData(FrameShape.RoundedWide, 1280, 720)]
    [InlineData(FrameShape.RoundedSquare, 720, 720)]
    [InlineData(FrameShape.Cinema, 1280, 548)]
    public void Each_shape_crops_a_720p_picture_to_its_proportions(FrameShape shape, int width, int height)
    {
        Assert.Equal((width, height), FrameShaper.SizeFor(shape, 1280, 720));
    }

    [Fact]
    public void Square_keeps_the_centre_of_the_picture()
    {
        // Left half dark, right half bright: the centre column is where they meet.
        var frame = new VideoFrame(16, 8, new byte[VideoFrame.SizeOf(16, 8)], 0);
        for (var y = 0; y < 8; y++)
        for (var x = 0; x < 16; x++)
            frame.Data[y * 16 + x] = (byte)(x < 8 ? 20 : 200);

        var square = new FrameShaper().Apply(frame, FrameShape.Square);

        Assert.Equal((8, 8), (square.Width, square.Height));
        Assert.Equal(20, square.Data[0]);   // x = 4 in the original
        Assert.Equal(200, square.Data[7]);  // x = 11 in the original
    }

    [Fact]
    public void Circle_paints_the_corners_black_and_keeps_the_middle()
    {
        var frame = new VideoFrame(64, 36, new byte[VideoFrame.SizeOf(64, 36)], 0);
        Array.Fill(frame.Data, (byte)200);

        var circle = new FrameShaper().Apply(frame, FrameShape.Circle);

        Assert.Equal(16, circle.Data[0]);                       // top-left corner: video black
        Assert.Equal(200, circle.Data[18 * 36 + 18]);           // centre untouched
    }

    [Fact]
    public void Wide_returns_the_same_frame_without_copying()
    {
        var frame = new VideoFrame(16, 8, new byte[VideoFrame.SizeOf(16, 8)], 0);
        Assert.Same(frame, new FrameShaper().Apply(frame, FrameShape.Wide));
    }

    [Fact]
    public void Rounded_shapes_paint_only_the_corners()
    {
        var frame = new VideoFrame(64, 36, new byte[VideoFrame.SizeOf(64, 36)], 0);
        Array.Fill(frame.Data, (byte)200);

        var rounded = new FrameShaper().Apply(frame, FrameShape.RoundedWide);

        Assert.Equal((64, 36), (rounded.Width, rounded.Height));
        Assert.Equal(16, rounded.Data[0]);          // corner
        Assert.Equal(200, rounded.Data[32]);        // middle of the top edge stays
        Assert.Equal(200, rounded.Data[18 * 64]);   // middle of the left edge stays
    }
}
