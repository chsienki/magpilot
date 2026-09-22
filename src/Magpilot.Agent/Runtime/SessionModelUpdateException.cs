namespace Magpilot.Agent.Runtime;

internal sealed class SessionModelUpdateException(string message)
    : InvalidOperationException(message);
