using System.Collections.Concurrent;
using System.Reflection;
using GitHub.Copilot;
using Magpilot.Shared;

namespace Magpilot.Agent.Runtime.Sdk;

internal interface ISdkClientHost : IAsyncDisposable
{
    CopilotClient Client { get; }
    Task StartAsync(CancellationToken ct);
    Task<GetStatusResponse> GetStatusAsync(CancellationToken ct);
    Task StopAsync();
    Task ForceStopAsync();
}

internal interface ISdkClientHostFactory
{
    void ValidateProfile(SessionRuntimeProfile profile);
    ISdkClientHost Create(SdkClientKey key);
}

internal sealed class CopilotSdkClientHost(CopilotClient client) : ISdkClientHost
{
    public CopilotClient Client => client;

    public Task StartAsync(CancellationToken ct) => client.StartAsync(ct);

    public Task<GetStatusResponse> GetStatusAsync(CancellationToken ct) =>
        client.GetStatusAsync(ct);

    public Task StopAsync() => client.StopAsync();

    public Task ForceStopAsync() => client.ForceStopAsync();

    public ValueTask DisposeAsync() => client.DisposeAsync();
}

internal sealed class CopilotSdkClientHostFactory(ILoggerFactory loggerFactory)
    : ISdkClientHostFactory
{
    private static readonly string SdkVersion =
        typeof(CopilotClient).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? typeof(CopilotClient).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    public void ValidateProfile(SessionRuntimeProfile profile) =>
        CopilotHomeLayout.Validate(profile);

    public ISdkClientHost Create(SdkClientKey key)
    {
        var client = new CopilotClient(new CopilotClientOptions
        {
            Connection = RuntimeConnection.ForStdio(),
            Mode = CopilotClientMode.CopilotCli,
            BaseDirectory = key.BaseDirectory,
            WorkingDirectory = Environment.CurrentDirectory,
            Logger = loggerFactory.CreateLogger("GitHub.Copilot.SDK"),
            ClientInfo = new CopilotClientInfo
            {
                ApplicationName = "Magpilot.Agent",
                ApplicationVersion = Versioning.AssemblyVersion,
                IntegrationName = "copilot-sdk",
                IntegrationVersion = SdkVersion,
            },
        });
        return new CopilotSdkClientHost(client);
    }
}

/// <summary>
/// Lazily owns one SDK client for each client-wide runtime isolation key.
/// Merely registering the pool does not start a Copilot runtime.
/// </summary>
internal sealed class SdkClientPool : IHostedService, IAsyncDisposable
{
    private readonly ISdkClientHostFactory _factory;
    private readonly ILogger<SdkClientPool> _log;
    private readonly ConcurrentDictionary<SdkClientKey, Lazy<Task<ISdkClientHost>>> _clients = new();

    public SdkClientPool(
        ILoggerFactory loggerFactory,
        ILogger<SdkClientPool> log)
        : this(new CopilotSdkClientHostFactory(loggerFactory), log)
    {
    }

    internal SdkClientPool(
        ISdkClientHostFactory factory,
        ILogger<SdkClientPool> log)
    {
        _factory = factory;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public async Task<ISdkClientHost> AcquireAsync(
        SessionRuntimeProfile profile,
        CancellationToken ct)
    {
        _factory.ValidateProfile(profile);
        var key = SdkClientKey.FromProfile(profile);
        var lazy = _clients.GetOrAdd(
            key,
            static (clientKey, state) => new Lazy<Task<ISdkClientHost>>(
                () => state.Pool.StartClientAsync(clientKey, state.Token),
                LazyThreadSafetyMode.ExecutionAndPublication),
            (Pool: this, Token: ct));

        try
        {
            return await lazy.Value.WaitAsync(ct);
        }
        catch
        {
            _clients.TryRemove(
                new KeyValuePair<SdkClientKey, Lazy<Task<ISdkClientHost>>>(
                    key,
                    lazy));
            throw;
        }
    }

    private async Task<ISdkClientHost> StartClientAsync(
        SdkClientKey key,
        CancellationToken ct)
    {
        var host = _factory.Create(key);
        try
        {
            await host.StartAsync(ct);
            var status = await host.GetStatusAsync(ct);
            _log.LogInformation(
                "Started Copilot SDK runtime version {RuntimeVersion} protocol {ProtocolVersion} baseDirectory={BaseDirectory}",
                status.Version,
                status.ProtocolVersion,
                key.BaseDirectory ?? "(default)");
            return host;
        }
        catch
        {
            await CleanupFailedStartAsync(host);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var clients = _clients.Values.ToArray();
        _clients.Clear();

        foreach (var lazy in clients)
        {
            if (!lazy.IsValueCreated)
                continue;

            ISdkClientHost host;
            try
            {
                host = await lazy.Value.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Copilot SDK client did not finish starting before shutdown");
                continue;
            }

            try
            {
                await host.StopAsync().WaitAsync(
                    TimeSpan.FromSeconds(10),
                    cancellationToken);
            }
            catch (Exception ex) when (
                ex is TimeoutException ||
                ex is AggregateException ||
                ex is InvalidOperationException)
            {
                _log.LogWarning(ex, "Graceful Copilot SDK runtime shutdown failed; forcing stop");
                await host.ForceStopAsync();
            }
            finally
            {
                await host.DisposeAsync();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await StopAsync(cts.Token);
    }

    private async Task CleanupFailedStartAsync(ISdkClientHost host)
    {
        try
        {
            await host.ForceStopAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Cleaning up a failed Copilot SDK runtime start also failed");
        }
        finally
        {
            await host.DisposeAsync();
        }
    }
}
