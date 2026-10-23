namespace MoonCam.Core.Diagnostics;

/// <summary>
/// Where MoonCam writes what happened, for troubleshooting. Implementations must stay local and must never
/// receive secrets (session tokens) or media.
/// </summary>
public interface IEventLog
{
    void Info(string message);

    void Warn(string message, Exception? error = null);
}

public sealed class NullEventLog : IEventLog
{
    public static readonly NullEventLog Instance = new();

    public void Info(string message) { }

    public void Warn(string message, Exception? error = null) { }
}
