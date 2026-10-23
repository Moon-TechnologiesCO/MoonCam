namespace MoonCam.Core.Setup;

/// <summary>A piece of the operating system MoonCam relies on.</summary>
public sealed record Component(string Name, string Purpose, bool IsReady);

public enum SetupOutcome
{
    Ready,
    Cancelled,
    SecureBootEnrollmentRequired,
    Failed,
}

public sealed record SetupResult(SetupOutcome Outcome, string Details);

/// <summary>Checks for and installs the platform components MoonCam needs.</summary>
public interface ISystemSetup
{
    IReadOnlyList<Component> Check();

    Task<SetupResult> InstallAsync(CancellationToken cancellationToken = default);
}
