namespace Magpilot.Agent.Runtime;

internal class SessionRuntimeConfigurationException
    : InvalidOperationException
{
    internal SessionRuntimeConfigurationException(string message)
        : base(message)
    {
    }

    internal SessionRuntimeConfigurationException(
        string message,
        Exception innerException)
        : base(message, innerException)
    {
    }

    internal bool LeavesSessionIndeterminate { get; init; }

    internal string? SessionId { get; set; }
}
