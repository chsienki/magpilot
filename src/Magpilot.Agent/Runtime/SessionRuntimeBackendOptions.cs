namespace Magpilot.Agent.Runtime;

public sealed record SessionRuntimeBackendOptions(
    SessionRuntimeBackend DefaultBackend)
{
    public static readonly SessionRuntimeBackendOptions AcpDefault =
        new(SessionRuntimeBackend.Acp);

    public static readonly SessionRuntimeBackendOptions SdkDefault =
        new(SessionRuntimeBackend.Sdk);

    public static SessionRuntimeBackendOptions FromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(
            "MAGPILOT_RUNTIME_BACKEND");
        return FromValue(value);
    }

    internal static SessionRuntimeBackendOptions FromValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return SdkDefault;

        return value.Trim().ToLowerInvariant() switch
        {
            "acp" => AcpDefault,
            "sdk" => SdkDefault,
            _ => throw new InvalidOperationException(
                "MAGPILOT_RUNTIME_BACKEND must be 'acp' or 'sdk'."),
        };
    }

}
