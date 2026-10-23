using System.Text.Json;
using System.Text.Json.Serialization;

namespace MoonCam.Core.Protocol;

/// <summary>First message from the phone: who it is, and the one-time token that proves it was invited.</summary>
public sealed record HelloMessage(
    string DeviceName,
    string Manufacturer,
    string Model,
    string AndroidVersion,
    string AppVersion,
    string Token);

/// <summary>The desktop's answer to <see cref="HelloMessage"/>.</summary>
public sealed record WelcomeMessage(bool Accepted, string? SessionId, string ServerName, string? Reason = null);

public sealed record ByeMessage(string Reason);

public sealed record VideoSize(int Width, int Height);

/// <summary>One selectable lens. <see cref="PhysicalId"/> is set when the lens belongs to a logical multi-camera.</summary>
public sealed record CameraInfo(
    string Id,
    string Facing,
    string Lens,
    string? PhysicalId,
    double? FocalLengthMm,
    IReadOnlyList<VideoSize> Sizes,
    IReadOnlyList<int> FrameRates);

public sealed record CapabilitiesMessage(IReadOnlyList<CameraInfo> Cameras);

/// <summary>Well-known values of <see cref="ControlMessage.Op"/>.</summary>
public static class ControlOps
{
    public const string Start = "start";
    public const string Stop = "stop";
    public const string KeyFrame = "keyFrame";
    public const string Status = "status";
}

/// <summary>A command or event. Fields that do not apply to an operation stay null and are not serialised.</summary>
public sealed record ControlMessage(
    string Op,
    string? Camera = null,
    int? Width = null,
    int? Height = null,
    int? Fps = null,
    int? BitrateBps = null,
    string? Codec = null,
    bool? Streaming = null,
    string? Message = null);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(HelloMessage))]
[JsonSerializable(typeof(WelcomeMessage))]
[JsonSerializable(typeof(ByeMessage))]
[JsonSerializable(typeof(CapabilitiesMessage))]
[JsonSerializable(typeof(ControlMessage))]
internal sealed partial class MoonLinkJsonContext : JsonSerializerContext;

/// <summary>Builds and reads the JSON-carrying frames.</summary>
public static class Messages
{
    public static Frame Hello(HelloMessage message) => Json(FrameType.Hello, message, MoonLinkJsonContext.Default.HelloMessage);

    public static Frame Welcome(WelcomeMessage message) => Json(FrameType.Welcome, message, MoonLinkJsonContext.Default.WelcomeMessage);

    public static Frame Bye(string reason) => Json(FrameType.Bye, new ByeMessage(reason), MoonLinkJsonContext.Default.ByeMessage);

    public static Frame Capabilities(CapabilitiesMessage message) => Json(FrameType.Capabilities, message, MoonLinkJsonContext.Default.CapabilitiesMessage);

    public static Frame Control(ControlMessage message) => Json(FrameType.Control, message, MoonLinkJsonContext.Default.ControlMessage);

    public static HelloMessage ReadHello(Frame frame) => Read(frame, FrameType.Hello, MoonLinkJsonContext.Default.HelloMessage);

    public static WelcomeMessage ReadWelcome(Frame frame) => Read(frame, FrameType.Welcome, MoonLinkJsonContext.Default.WelcomeMessage);

    public static ByeMessage ReadBye(Frame frame) => Read(frame, FrameType.Bye, MoonLinkJsonContext.Default.ByeMessage);

    public static CapabilitiesMessage ReadCapabilities(Frame frame) => Read(frame, FrameType.Capabilities, MoonLinkJsonContext.Default.CapabilitiesMessage);

    public static ControlMessage ReadControl(Frame frame) => Read(frame, FrameType.Control, MoonLinkJsonContext.Default.ControlMessage);

    private static Frame Json<T>(FrameType type, T message, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
        => new(type, JsonSerializer.SerializeToUtf8Bytes(message, info));

    private static T Read<T>(Frame frame, FrameType expected, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
    {
        if (frame.Type != expected)
            throw new ProtocolException($"Expected {expected}, got {frame.Type}.");
        try
        {
            return JsonSerializer.Deserialize(frame.Payload.Span, info)
                   ?? throw new ProtocolException($"{expected} payload is empty.");
        }
        catch (JsonException e)
        {
            throw new ProtocolException($"{expected} payload is not valid JSON.", e);
        }
    }
}
