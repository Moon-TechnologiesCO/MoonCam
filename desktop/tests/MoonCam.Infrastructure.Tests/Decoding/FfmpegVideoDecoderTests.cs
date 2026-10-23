using System.Diagnostics;
using MoonCam.Core.Media;
using MoonCam.Core.Protocol;
using MoonCam.Infrastructure.Decoding;

namespace MoonCam.Infrastructure.Tests.Decoding;

/// <summary>
/// Decodes a real H.264 stream produced by the ffmpeg command-line tool, split exactly as the phone sends it:
/// SPS/PPS in VIDEO_CONFIG, then one access unit per frame without parameter sets.
/// </summary>
public class FfmpegVideoDecoderTests
{
    [Fact]
    public void Decodes_a_real_h264_stream_to_i420()
    {
        if (!FfmpegVideoDecoder.IsAvailable || !TryEncodeTestPattern(out var stream))
            return; // FFmpeg is not installed on this machine; CI installs it.

        var (codecData, units) = SplitLikeThePhone(stream);
        using var decoder = new FfmpegVideoDecoder();
        decoder.Configure(new VideoConfig(VideoCodec.H264, 320, 240, 30, codecData));

        var pictures = units.SelectMany((u, i) => decoder.Decode(u.Data, u.IsKey, i * 33_333L)).ToList();

        Assert.True(pictures.Count >= units.Count - 1, $"decoded {pictures.Count} of {units.Count}");
        Assert.All(pictures, p => Assert.Equal((320, 240), (p.Width, p.Height)));
        Assert.Equal(0, pictures[0].TimestampUs);
        // testsrc2 is colourful: the luma plane cannot be flat.
        Assert.True(pictures[0].Y.ToArray().Distinct().Count() > 16);
    }

    [Fact]
    public void Decode_before_configure_is_a_clear_error()
    {
        using var decoder = new FfmpegVideoDecoder();

        Assert.Throws<InvalidOperationException>(() => decoder.Decode(new byte[] { 0, 0, 0, 1, 0x65 }, true, 0));
    }

    private static (byte[] CodecData, List<(byte[] Data, bool IsKey)> Units) SplitLikeThePhone(byte[] stream)
    {
        var parameterSets = new List<ReadOnlyMemory<byte>>();
        var units = new List<(byte[], bool)>();
        foreach (var unit in new AccessUnitSplitter().Push(stream).Append(stream))
        {
            var kept = new List<ReadOnlyMemory<byte>>();
            foreach (var (offset, length) in AnnexB.FindNalUnits(unit))
            {
                var nal = unit.AsMemory(offset, length);
                switch (AnnexB.H264Type(nal.Span))
                {
                    case AnnexB.NalSps or AnnexB.NalPps:
                        if (parameterSets.Count < 2) parameterSets.Add(nal);
                        break;
                    case AnnexB.NalAccessUnitDelimiter:
                        break;
                    default:
                        kept.Add(nal);
                        break;
                }
            }
            if (kept.Count > 0 && units.Count < 10)
            {
                var data = AnnexB.Join(kept);
                units.Add((data, AnnexB.ContainsIdr(data)));
            }
        }
        return (AnnexB.Join(parameterSets), units);
    }

    private static bool TryEncodeTestPattern(out byte[] stream)
    {
        stream = [];
        try
        {
            var info = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=30",
                         "-frames:v", "10", "-c:v", "libx264", "-preset", "ultrafast", "-tune", "zerolatency", "-bf", "0",
                         "-g", "5", "-bsf:v", "h264_metadata=aud=insert", "-f", "h264", "-" })
                info.ArgumentList.Add(arg);
            using var process = Process.Start(info)!;
            using var output = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(output);
            process.WaitForExit();
            stream = output.ToArray();
            return process.ExitCode == 0 && stream.Length > 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
