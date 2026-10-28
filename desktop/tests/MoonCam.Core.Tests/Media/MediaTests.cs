using MoonCam.Core.Media;

namespace MoonCam.Core.Tests.Media;

public class MediaTests
{
    private static readonly byte[] Sps = [0x67, 0x42, 0x00, 0x1F];
    private static readonly byte[] Pps = [0x68, 0xCE];
    private static readonly byte[] Idr = [0x65, 0x88, 0x84];
    private static readonly byte[] Slice = [0x41, 0x9A];
    private static readonly byte[] Aud = [0x09, 0xF0];

    [Fact]
    public void Finds_nal_units_with_three_and_four_byte_start_codes()
    {
        byte[] stream = [0, 0, 0, 1, .. Sps, 0, 0, 1, .. Pps, 0, 0, 0, 1, .. Idr];

        var units = AnnexB.FindNalUnits(stream);

        Assert.Equal([AnnexB.NalSps, AnnexB.NalPps, AnnexB.NalIdr],
            units.Select(u => AnnexB.H264Type(stream.AsSpan(u.Offset, u.Length))));
        Assert.Equal(Pps.Length, units[1].Length); // the extra zero of the 4-byte start code is not part of PPS
    }

    [Fact]
    public void Detects_key_frames()
    {
        Assert.True(AnnexB.ContainsIdr(AnnexB.Join([Sps, Pps, Idr])));
        Assert.False(AnnexB.ContainsIdr(AnnexB.Join([Slice])));
    }

    [Fact]
    public void Splitter_emits_whole_access_units_even_from_tiny_chunks()
    {
        // A unit is final only once the next NAL begins, so the trailing Slice closes the third unit.
        var stream = AnnexB.Join([Aud, Sps, Pps, Idr, Aud, Slice, Aud, Slice, Aud, Slice]);
        var splitter = new AccessUnitSplitter();
        var units = new List<byte[]>();

        foreach (var b in stream)
            units.AddRange(splitter.Push([b]));

        Assert.Equal(3, units.Count);
        Assert.True(AnnexB.ContainsIdr(units[0]));
        Assert.False(AnnexB.ContainsIdr(units[1]));
    }

    [Theory]
    [InlineData(16, 128, 128, 0, 0, 0)]       // video black
    [InlineData(235, 128, 128, 255, 255, 255)] // video white
    [InlineData(81, 90, 240, 0, 0, 255)]       // BT.601 red, written as B,G,R
    public void Converts_i420_to_bgra(byte y, byte u, byte v, byte b, byte g, byte r)
    {
        var data = new byte[VideoFrame.SizeOf(2, 2)];
        data.AsSpan(0, 4).Fill(y);
        data[4] = u;
        data[5] = v;
        var bgra = new byte[16];

        I420Converter.ToBgra(new VideoFrame(2, 2, data, 0), bgra, 8);

        Assert.InRange(bgra[0], b - 2, b + 2);
        Assert.InRange(bgra[1], g - 2, g + 2);
        Assert.InRange(bgra[2], r - 2, r + 2);
        Assert.Equal(255, bgra[3]);
    }

    [Fact]
    public void A_shrunk_preview_averages_each_block_of_luma()
    {
        // 4×4 picture: the top-left 2×2 block is white, the rest black; chroma neutral.
        var data = new byte[VideoFrame.SizeOf(4, 4)];
        data.AsSpan(0, 16).Fill(16);
        data[0] = data[1] = data[4] = data[5] = 235;
        data.AsSpan(16).Fill(128);
        var bgra = new byte[2 * 2 * 4];

        I420Converter.ToBgra(new VideoFrame(4, 4, data, 0), bgra, 8, factor: 2);

        Assert.Equal([255, 255, 255, 255], bgra[..4]);   // the white block
        Assert.Equal([0, 0, 0, 255], bgra[4..8]);        // its black neighbour
        Assert.Equal([0, 0, 0, 255], bgra[12..16]);
    }

    [Fact]
    public void Video_frame_rejects_wrong_buffer_sizes()
    {
        Assert.Throws<ArgumentException>(() => new VideoFrame(4, 4, new byte[10], 0));
        Assert.Throws<ArgumentException>(() => new VideoFrame(3, 4, new byte[18], 0));
    }
}
