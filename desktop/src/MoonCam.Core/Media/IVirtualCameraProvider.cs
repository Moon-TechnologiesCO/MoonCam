namespace MoonCam.Core.Media;

/// <summary>A virtual camera device available on this computer.</summary>
public sealed record VirtualCameraDevice(string Path, string Name);

/// <summary>Finds and opens the platform's virtual cameras (v4l2loopback on Linux).</summary>
public interface IVirtualCameraProvider
{
    IReadOnlyList<VirtualCameraDevice> Find();

    IVirtualCamera Open(VirtualCameraDevice device);
}
