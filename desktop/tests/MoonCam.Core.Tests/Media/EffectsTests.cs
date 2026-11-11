using MoonCam.Core.Media;
using Microsoft.Extensions.Time.Testing;

namespace MoonCam.Core.Tests.Media;

public class EffectsTests
{
    private static VideoFrame Solid(int w, int h, byte y, byte u = 128, byte v = 128)
    {
        var data = new byte[VideoFrame.SizeOf(w, h)];
        data.AsSpan(0, w * h).Fill(y);
        data.AsSpan(w * h, w * h / 4).Fill(u);
        data.AsSpan(w * h * 5 / 4).Fill(v);
        return new VideoFrame(w, h, data, 0);
    }

    [Fact]
    public void Mono_removes_all_colour_and_keeps_brightness()
    {
        var output = new FrameFilter().Apply(Solid(8, 8, 150, 90, 200), FilterKind.Mono);

        Assert.Equal(150, output.Data[0]);
        Assert.All(output.Data.AsSpan(64).ToArray(), b => Assert.Equal(128, b));
    }

    [Fact]
    public void Warm_moves_colour_towards_red_and_away_from_blue()
    {
        var output = new FrameFilter().Apply(Solid(8, 8, 120), FilterKind.Warm);

        Assert.True(output.Data[64] < 128);        // U: less blue
        Assert.True(output.Data[64 + 16] > 128);   // V: more red
    }

    [Fact]
    public void Without_a_mask_the_background_is_left_alone()
    {
        var compositor = new BackgroundCompositor { Mode = BackgroundMode.Blur };
        var frame = Solid(32, 18, 100);

        Assert.Same(frame, compositor.Apply(frame));
    }

    [Fact]
    public void The_person_stays_and_the_background_is_replaced()
    {
        var time = new FakeTimeProvider();
        var compositor = new BackgroundCompositor(time) { Mode = BackgroundMode.Image };
        compositor.SetImage(Solid(64, 36, 30));
        // Mask: left half background (0), right half person (255).
        var mask = new byte[16 * 9];
        for (var y = 0; y < 9; y++)
        for (var x = 8; x < 16; x++)
            mask[y * 16 + x] = 255;
        compositor.SetMask(mask, 16, 9);

        var output = compositor.Apply(Solid(64, 36, 200));

        Assert.Equal(30, output.Data[18 * 64 + 2]);    // far left: background image
        Assert.Equal(200, output.Data[18 * 64 + 61]);  // far right: the person
    }

    [Fact]
    public void An_old_mask_is_ignored_so_a_silent_phone_never_freezes_the_effect()
    {
        var time = new FakeTimeProvider();
        var compositor = new BackgroundCompositor(time) { Mode = BackgroundMode.Blur };
        compositor.SetMask(new byte[16 * 9], 16, 9);
        time.Advance(TimeSpan.FromSeconds(2));
        var frame = Solid(32, 18, 100);

        Assert.Same(frame, compositor.Apply(frame));
    }

    [Fact]
    public void Mask_payload_carries_its_size()
    {
        var compositor = new BackgroundCompositor { Mode = BackgroundMode.Blur };
        var payload = new byte[4 + 4 * 2];
        payload[1] = 4; payload[3] = 2;
        compositor.SetMaskPayload(payload);

        Assert.True(compositor.HasMask);
    }

    [Theory]
    [InlineData("moonlit")]
    [InlineData("aurora")]
    [InlineData("sunset")]
    [InlineData("studio")]
    [InlineData("ocean")]
    [InlineData("lavender")]
    public void Every_built_in_background_draws_at_the_requested_size(string id)
    {
        var background = BuiltInBackgrounds.All.Single(b => b.Id == id);
        var frame = background.Draw(320, 180);

        Assert.Equal((320, 180), (frame.Width, frame.Height));
        Assert.Contains(frame.Data.AsSpan(0, 320 * 180).ToArray(), b => b > 40); // not a black picture
    }
}
