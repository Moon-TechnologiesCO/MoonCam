using MoonCam.Core.Protocol;

namespace MoonCam.Core.Media;

/// <summary>Turns encoded access units into I420 pictures. Implementations are not thread-safe.</summary>
public interface IVideoDecoder : IDisposable
{
    /// <summary>(Re)starts the decoder for a new stream description.</summary>
    void Configure(VideoConfig config);

    /// <summary>Decodes one access unit. Returns zero or more pictures (usually exactly one).</summary>
    IReadOnlyList<VideoFrame> Decode(ReadOnlyMemory<byte> accessUnit, bool isKeyFrame, long timestampUs);

    /// <summary>Every sink is done with <paramref name="frame"/>: the decoder may reuse its buffer for a later picture.</summary>
    void Recycle(VideoFrame frame) { }
}
