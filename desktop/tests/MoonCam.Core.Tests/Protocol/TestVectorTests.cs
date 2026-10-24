using System.Text.Json;
using MoonCam.Core.Protocol;

namespace MoonCam.Core.Tests.Protocol;

/// <summary>Runs the shared MoonLink vectors (protocol/test-vectors) that the Android tests also run.</summary>
public class TestVectorTests
{
    private static readonly JsonElement Vectors = JsonDocument.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestVectors", "moonlink-v1.json"))).RootElement;

    public static TheoryData<string> ValidNames() => Names("valid");

    public static TheoryData<string> InvalidNames() => Names("invalid");

    [Fact]
    public void Preamble_matches_vector()
    {
        var expected = Hex(Vectors.GetProperty("preamble").GetProperty("hex").GetString()!);
        var buffer = new byte[Preamble.Size];
        Preamble.Write(buffer);

        Assert.Equal(expected, buffer);
        Assert.Equal(1, Preamble.Parse(expected));
    }

    [Theory]
    [MemberData(nameof(ValidNames))]
    public async Task Valid_frame_decodes_and_reencodes_identically(string name)
    {
        var vector = Find("valid", name);
        var bytes = Hex(vector.GetProperty("hex").GetString()!);

        var frame = await new FrameReader(new MemoryStream(bytes)).ReadAsync();

        Assert.NotNull(frame);
        Assert.Equal(vector.GetProperty("type").GetInt32(), (int)frame.Type);
        Assert.Equal(vector.GetProperty("flags").GetInt32(), (int)frame.Flags);
        Assert.Equal(vector.GetProperty("timestampUs").GetInt64(), frame.TimestampUs);
        Assert.Equal(Hex(vector.GetProperty("payloadHex").GetString()!), frame.Payload.ToArray());

        var output = new MemoryStream();
        using (var writer = new FrameWriter(output))
            await writer.WriteAsync(frame);
        Assert.Equal(bytes, output.ToArray());
    }

    [Fact]
    public async Task Video_and_audio_configs_match_vectors()
    {
        var video = Find("valid", "video config 1280x720 at 30 fps, H.264");
        var v = VideoConfig.FromFrame((await new FrameReader(new MemoryStream(Hex(video.GetProperty("hex").GetString()!))).ReadAsync())!);
        var expectedVideo = video.GetProperty("videoConfig");
        Assert.Equal(expectedVideo.GetProperty("width").GetInt32(), v.Width);
        Assert.Equal(expectedVideo.GetProperty("height").GetInt32(), v.Height);
        Assert.Equal(expectedVideo.GetProperty("fps").GetInt32(), v.Fps);
        Assert.Equal(Hex(expectedVideo.GetProperty("codecDataHex").GetString()!), v.CodecData.ToArray());

        var audio = Find("valid", "audio config Opus 48 kHz mono");
        var a = AudioConfig.FromFrame((await new FrameReader(new MemoryStream(Hex(audio.GetProperty("hex").GetString()!))).ReadAsync())!);
        Assert.Equal(48000, a.SampleRate);
        Assert.Equal(1, a.Channels);
        Assert.Equal(AudioCodec.Opus, a.Codec);
    }

    [Theory]
    [MemberData(nameof(InvalidNames))]
    public async Task Invalid_frame_is_rejected(string name)
    {
        var bytes = Hex(Find("invalid", name).GetProperty("hex").GetString()!);

        var error = await Record.ExceptionAsync(async () => await new FrameReader(new MemoryStream(bytes)).ReadAsync());

        Assert.True(error is ProtocolException or EndOfStreamException, $"{name}: got {error?.GetType().Name ?? "no error"}");
    }

    [Fact]
    public void Json_vectors_deserialize()
    {
        foreach (var vector in Vectors.GetProperty("json").EnumerateArray())
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(vector.GetProperty("payload"));
            var frame = new Frame((FrameType)vector.GetProperty("type").GetInt32(), payload);
            switch (frame.Type)
            {
                case FrameType.Hello: Assert.Equal("abc123", Messages.ReadHello(frame).Token); break;
                case FrameType.Welcome: Assert.True(Messages.ReadWelcome(frame).Accepted); break;
                case FrameType.Control: Assert.Equal(2_800_000, Messages.ReadControl(frame).BitrateBps); break;
                default: Assert.Fail($"Unexpected vector type {frame.Type}"); break;
            }
        }
    }

    private static TheoryData<string> Names(string group)
    {
        var data = new TheoryData<string>();
        foreach (var v in Vectors.GetProperty(group).EnumerateArray())
            data.Add(v.GetProperty("name").GetString()!);
        return data;
    }

    private static JsonElement Find(string group, string name)
        => Vectors.GetProperty(group).EnumerateArray().Single(v => v.GetProperty("name").GetString() == name);

    private static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", ""));
}

public class PairingVectorTests
{
    [Fact]
    public void Pairing_code_and_commit_match_the_shared_vector()
    {
        var v = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestVectors", "moonlink-v1.json")))
            .RootElement.GetProperty("pairing");
        byte[] H(string name) => Convert.FromHexString(v.GetProperty(name).GetString()!);

        Assert.Equal(H("commitHex"), Pairing.Commit(H("phoneNonceHex")));
        Assert.True(Pairing.VerifyCommit(H("commitHex"), H("phoneNonceHex")));
        Assert.False(Pairing.VerifyCommit(H("commitHex"), H("desktopNonceHex")));
        Assert.Equal(v.GetProperty("code").GetString(),
            Pairing.Code(H("phoneFingerprintHex"), H("desktopFingerprintHex"), H("phoneNonceHex"), H("desktopNonceHex")));
    }

    [Fact]
    public void A_different_certificate_gives_a_different_code()
    {
        var np = Pairing.NewNonce();
        var nd = Pairing.NewNonce();
        var real = Pairing.Code(new byte[32], new byte[32], np, nd);
        var attacker = Pairing.Code(new byte[32], Enumerable.Repeat((byte)1, 32).ToArray(), np, nd);

        Assert.NotEqual(real, attacker);
        Assert.Equal("482 913", Pairing.Format("482913"));
    }
}
