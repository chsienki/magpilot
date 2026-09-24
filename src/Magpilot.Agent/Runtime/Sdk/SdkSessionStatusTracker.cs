#pragma warning disable GHCP001

using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Magpilot.Shared.Models;

namespace Magpilot.Agent.Runtime.Sdk;

internal sealed class SdkSessionStatusTracker(SessionRuntimeProfile profile)
{
    private const double NanoAiUnitsPerCredit = 1_000_000_000d;

    private readonly object _sync = new();
    private Dictionary<string, ModelInfo> _models =
        new(StringComparer.OrdinalIgnoreCase);
    private SessionRuntimeProfile _profile = profile;
    private SessionRuntimeStatus _status = new(
        profile.Model,
        profile.Model,
        profile.ReasoningEffort,
        CurrentTokens: null,
        TokenLimit: null,
        AiCreditsUsed: null,
        CanEditModel: true,
        DateTimeOffset.UtcNow);

    public SessionRuntimeProfile Profile
    {
        get
        {
            lock (_sync)
                return _profile;
        }
    }

    public SessionRuntimeStatus Snapshot
    {
        get
        {
            lock (_sync)
                return _status;
        }
    }

    public IReadOnlyList<SessionModelOption> ModelOptions
    {
        get
        {
            lock (_sync)
            {
                return _models.Values
                    .OrderBy(static model => model.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(static model => new SessionModelOption(
                        model.Id,
                        model.Name,
                        model.SupportedReasoningEfforts?.ToArray() ?? [],
                        model.DefaultReasoningEffort))
                    .ToArray();
            }
        }
    }

    public void SetModels(IEnumerable<ModelInfo> models)
    {
        lock (_sync)
        {
            _models = models.ToDictionary(
                static model => model.Id,
                StringComparer.OrdinalIgnoreCase);
            _status = _status with
            {
                ModelName = DisplayName(_status.ModelId),
                UpdatedAt = DateTimeOffset.UtcNow,
            };
        }
    }

    public void Apply(CurrentModel current)
    {
        lock (_sync)
        {
            _profile = _profile with
            {
                Model = current.ModelId,
                ReasoningEffort = current.ReasoningEffort,
            };
            _status = _status with
            {
                ModelId = current.ModelId,
                ModelName = DisplayName(current.ModelId),
                ReasoningEffort = current.ReasoningEffort,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
        }
    }

    public void Apply(UsageGetMetricsResult usage)
    {
        lock (_sync)
        {
            _status = _status with
            {
                AiCreditsUsed = ToAiCredits(usage.TotalNanoAiu),
                UpdatedAt = DateTimeOffset.UtcNow,
            };
        }
    }

    public void Apply(SessionEvent evt)
    {
        lock (_sync)
        {
            switch (evt)
            {
                case SessionModelChangeEvent changed:
                    _profile = _profile with
                    {
                        Model = changed.Data.NewModel,
                        ReasoningEffort = changed.Data.ReasoningEffort,
                    };
                    _status = _status with
                    {
                        ModelId = changed.Data.NewModel,
                        ModelName = DisplayName(changed.Data.NewModel),
                        ReasoningEffort = changed.Data.ReasoningEffort,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    };
                    break;

                case SessionUsageInfoEvent usage:
                    _status = _status with
                    {
                        CurrentTokens = usage.Data.CurrentTokens,
                        TokenLimit = usage.Data.TokenLimit,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    };
                    break;

                case SessionUsageCheckpointEvent checkpoint:
                    _status = _status with
                    {
                        AiCreditsUsed = ToAiCredits(checkpoint.Data.TotalNanoAiu),
                        UpdatedAt = DateTimeOffset.UtcNow,
                    };
                    break;

                case AssistantUsageEvent
                {
                    Data.CopilotUsage: { } copilotUsage,
                }:
                    _status = _status with
                    {
                        AiCreditsUsed =
                            (_status.AiCreditsUsed ?? 0)
                            + ToAiCredits(copilotUsage.TotalNanoAiu),
                        UpdatedAt = DateTimeOffset.UtcNow,
                    };
                    break;
            }
        }
    }

    public void SetAppliedProfile(string model, string? reasoningEffort)
    {
        lock (_sync)
        {
            _profile = _profile with
            {
                Model = model,
                ReasoningEffort = reasoningEffort,
            };
            _status = _status with
            {
                ModelId = model,
                ModelName = DisplayName(model),
                ReasoningEffort = reasoningEffort,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
        }
    }

    internal static double? ToAiCredits(double? totalNanoAiu) =>
        totalNanoAiu is null
            ? null
            : totalNanoAiu.Value / NanoAiUnitsPerCredit;

    private string? DisplayName(string? modelId) =>
        modelId is not null && _models.TryGetValue(modelId, out var model)
            ? model.Name
            : modelId;
}

#pragma warning restore GHCP001
