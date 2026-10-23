using MoonCam.Core.Media;

namespace MoonCam.Platform.Linux.Video;

/// <summary>v4l2loopback-backed virtual cameras. Devices labelled "MoonCam" come first.</summary>
public sealed class LinuxVirtualCameras : IVirtualCameraProvider
{
    public const string CardLabel = "MoonCam";

    public IReadOnlyList<VirtualCameraDevice> Find()
        => V4l2LoopbackCamera.FindDevices()
            .OrderByDescending(d => d.Card.Contains(CardLabel, StringComparison.OrdinalIgnoreCase))
            .Select(d => new VirtualCameraDevice(d.Path, d.Card))
            .ToList();

    public IVirtualCamera Open(VirtualCameraDevice device) => V4l2LoopbackCamera.Open(device.Path);
}
