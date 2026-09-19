namespace Magpilot.Agent.Runtime;

public sealed record SessionRuntimeBackendOptions(
    SessionRuntimeBackend DefaultBackend)
{
    public static readonly SessionRuntimeBackendOptions AcpDefault =
        new(SessionRuntimeBackend.Acp);

    public static SessionRuntimeBackendOptions FromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(
            "MAGPILOT_RUNTIME_BACKEND");
        if (string.IsNullOrWhiteSpace(value))
            return AcpDefault;

        return value.Trim().ToLowerInvariant() switch
        {
            "acp" => AcpDefault,
            "sdk" => new SessionRuntimeBackendOptions(
                SessionRuntimeBackend.Sdk),
            _ => throw new InvalidOperationException(
                "MAGPILOT_RUNTIME_BACKEND must be 'acp' or 'sdk'."),
        };
    }

    public SessionRuntimeBackend ForProfile(bool useAgency) =>
        useAgency ? SessionRuntimeBackend.Acp : DefaultBackend;
}
