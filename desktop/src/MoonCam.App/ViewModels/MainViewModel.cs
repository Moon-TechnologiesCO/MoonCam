using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoonCam.Core.Media;
using MoonCam.Core.Protocol;
using MoonCam.Core.Session;
using MoonCam.Core.Setup;
using MoonCam.Infrastructure.Streaming;

namespace MoonCam.App.ViewModels;

public sealed record CameraOption(string Id, string Label);

public sealed record PresetOption(string Label, int Width, int Height, int Fps);

/// <summary>
/// State and commands of the main window. It knows nothing about Avalonia: UI-thread marshalling is
/// injected, so the same class can be unit-tested.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly StreamController _controller;
    private readonly IVirtualCameraProvider _virtualCameras;
    private readonly ISystemSetup _setup;
    private readonly Action<Action> _runOnUi;
    private readonly ITimer _statsTimer;
    private bool _applyingCapabilities;

    public MainViewModel(StreamController controller, IVirtualCameraProvider virtualCameras, ISystemSetup setup,
        Action<Action>? runOnUi = null, TimeProvider? time = null)
    {
        _controller = controller;
        _virtualCameras = virtualCameras;
        _setup = setup;
        _runOnUi = runOnUi ?? (action => action());

        Presets = [.. StreamSettings.Presets.Select(p => new PresetOption(p.Name, p.Width, p.Height, p.Fps))];
        _selectedPreset = Presets.First(p => p.Width == 1280 && p.Fps == 30);
        Cameras.Add(new CameraOption("0", "Back camera"));
        _selectedCamera = Cameras[0];

        _controller.StateChanged += state => _runOnUi(() => ShowState(state));
        _controller.CapabilitiesChanged += caps => _runOnUi(() => ShowCameras(caps));
        _controller.Notice += text => _runOnUi(() => Notice = text);
        _statsTimer = (time ?? TimeProvider.System).CreateTimer(_ => _runOnUi(UpdateStats), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        RefreshSetup();
        ShowState(_controller.State);
    }

    public ObservableCollection<CameraOption> Cameras { get; } = [];

    public IReadOnlyList<PresetOption> Presets { get; }

    public IReadOnlyList<string> Qualities { get; } = ["Low", "Balanced", "High"];

    public ObservableCollection<Component> SetupItems { get; } = [];


    [ObservableProperty] private string _statusTitle = "Starting…";
    [ObservableProperty] private string _statusDetail = "";
    [ObservableProperty] private string _phoneName = "No phone connected";
    [ObservableProperty] private bool _isLive;
    [ObservableProperty] private bool _isWaiting = true;
    [ObservableProperty] private string? _notice;
    [ObservableProperty] private string _statsText = "";
    [ObservableProperty] private CameraOption? _selectedCamera;
    [ObservableProperty] private PresetOption _selectedPreset;
    [ObservableProperty] private int _selectedQuality = 1;
    [ObservableProperty] private bool _isVirtualCameraAvailable;
    [ObservableProperty] private bool _isVirtualCameraOn;
    [ObservableProperty] private string _virtualCameraStatus = "";
    [ObservableProperty] private bool _isSetupComplete = true;
    [ObservableProperty] private bool _isInstalling;

    [RelayCommand]
    private async Task ConnectUsbAsync()
    {
        try
        {
            Notice = await _controller.ConnectUsbAsync();
        }
        catch (InvalidOperationException e)
        {
            Notice = e.Message;
        }
    }

    [RelayCommand]
    private void RefreshSetup()
    {
        SetupItems.Clear();
        foreach (var component in _setup.Check())
            SetupItems.Add(component);
        IsSetupComplete = SetupItems.All(c => c.IsReady);

        var devices = _virtualCameras.Find();
        IsVirtualCameraAvailable = devices.Count > 0;
        VirtualCameraStatus = devices.Count > 0
            ? $"{devices[0].Name} · {devices[0].Path}"
            : "Finish the setup above to turn this on.";
    }

    /// <summary>Installs whatever is missing. Ubuntu shows its own password dialog; MoonCam never sees the password.</summary>
    [RelayCommand]
    private async Task InstallComponentsAsync()
    {
        IsInstalling = true;
        Notice = "Repairing… Ubuntu will ask for your password.";
        try
        {
            var result = await _setup.InstallAsync();
            Notice = result.Outcome switch
            {
                SetupOutcome.Ready => "All set. MoonCam is ready.",
                SetupOutcome.Cancelled => "Installation cancelled.",
                SetupOutcome.SecureBootEnrollmentRequired => "Your custom kernel needs the virtual camera driver approved by Secure Boot. See MoonCam's help page.",
                _ => $"Installation failed: {result.Details}",
            };
        }
        finally
        {
            IsInstalling = false;
            RefreshSetup();
        }
    }

    partial void OnSelectedCameraChanged(CameraOption? value) => ApplySettings();

    partial void OnSelectedPresetChanged(PresetOption value) => ApplySettings();

    partial void OnSelectedQualityChanged(int value) => ApplySettings();

    partial void OnIsVirtualCameraOnChanged(bool value)
    {
        if (!value)
        {
            _controller.DisableVirtualCamera();
            RefreshSetup();
            return;
        }
        var device = _virtualCameras.Find().FirstOrDefault();
        if (device is null)
        {
            Notice = "No virtual camera found. Set it up first.";
            IsVirtualCameraOn = false;
            return;
        }
        try
        {
            _controller.EnableVirtualCamera(_virtualCameras.Open(device));
            VirtualCameraStatus = $"Apps now see \"{device.Name}\" ({device.Path}).";
        }
        catch (IOException e)
        {
            Notice = e.Message;
            IsVirtualCameraOn = false;
        }
    }

    private async void ApplySettings()
    {
        if (_applyingCapabilities || SelectedCamera is null)
            return;
        try
        {
            await _controller.ApplySettingsAsync(new StreamSettings(
                SelectedCamera.Id, SelectedPreset.Width, SelectedPreset.Height, SelectedPreset.Fps, (StreamQuality)SelectedQuality));
        }
        catch (ArgumentException e)
        {
            Notice = e.Message;
        }
    }

    private void ShowState(ConnectionState state)
    {
        IsLive = state == ConnectionState.Streaming;
        IsWaiting = state is ConnectionState.Listening or ConnectionState.Reconnecting or ConnectionState.Stopped or ConnectionState.Handshaking;
        PhoneName = _controller.Phone is { } phone ? $"{phone.Manufacturer} {phone.Model}" : "No phone connected";
        (StatusTitle, StatusDetail) = state switch
        {
            ConnectionState.Listening => ("Waiting for your phone", "Plug it in with a USB cable and press Connect."),
            ConnectionState.Handshaking => ("Connecting…", "Checking that this is your phone."),
            ConnectionState.Connected => ("Connected", "Starting the camera…"),
            ConnectionState.Streaming => ("Live", "Your phone is streaming to this computer."),
            ConnectionState.Reconnecting => ("Reconnecting…", "The connection dropped. MoonCam is waiting for your phone to come back."),
            _ => ("Stopped", ""),
        };
    }

    private void ShowCameras(CapabilitiesMessage capabilities)
    {
        _applyingCapabilities = true;
        Cameras.Clear();
        foreach (var camera in capabilities.Cameras)
            Cameras.Add(new CameraOption(camera.PhysicalId ?? camera.Id, Describe(camera)));
        SelectedCamera = Cameras.FirstOrDefault();
        _applyingCapabilities = false;
    }

    private static string Describe(CameraInfo camera)
    {
        var facing = camera.Facing switch { "front" => "Front", "external" => "External", _ => "Back" };
        var lens = camera.Lens switch { "ultraWide" => " · ultra-wide", "telephoto" => " · telephoto", _ => "" };
        return $"{facing} camera{lens}";
    }

    private void UpdateStats()
    {
        if (!IsLive)
        {
            StatsText = "";
            return;
        }
        var stats = _controller.GetStats();
        var rtt = stats.RoundTrip is { } r ? $" · {r.TotalMilliseconds:0} ms" : "";
        StatsText = string.Create(CultureInfo.InvariantCulture, $"{stats.Width}×{stats.Height} · {stats.FramesPerSecond:0} fps{rtt}");
    }

    public void Dispose() => _statsTimer.Dispose();
}
