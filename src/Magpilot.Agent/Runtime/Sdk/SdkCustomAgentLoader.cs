using GitHub.Copilot;

namespace Magpilot.Agent.Runtime.Sdk;

internal static class SdkCustomAgentLoader
{
    public static IList<CustomAgentConfig>? Load(
        SessionRuntimeProfile profile,
        string workingDirectory)
    {
        if (profile.Agent is null)
            return null;

        var path = FindAgentPath(
            profile.Agent,
            profile.CopilotHome,
            workingDirectory);
        if (path is null)
        {
            throw new ArgumentException(
                $"Custom agent '{profile.Agent}' was not found in the selected Copilot home or working directory.",
                nameof(SessionRuntimeProfile.Agent));
        }

        return [Parse(profile.Agent, path)];
    }

    private static string? FindAgentPath(
        string agent,
        string? copilotHome,
        string workingDirectory)
    {
        var fileName = $"{agent}.agent.md";
        if (copilotHome is not null)
        {
            var isolated = Path.Combine(
                copilotHome,
                "agents",
                fileName);
            return File.Exists(isolated) ? isolated : null;
        }

        var userAgent = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.UserProfile),
            ".copilot",
            "agents",
            fileName);
        if (File.Exists(userAgent))
            return userAgent;

        var projectAgent = Path.Combine(
            workingDirectory,
            ".github",
            "agents",
            fileName);
        return File.Exists(projectAgent) ? projectAgent : null;
    }

    private static CustomAgentConfig Parse(
        string agent,
        string path)
    {
        var lines = File.ReadAllText(path)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n');
        if (lines.Length < 3
            || lines[0].TrimStart('\uFEFF').Trim() != "---")
        {
            throw InvalidAgent(
                agent,
                path,
                "missing opening YAML frontmatter delimiter");
        }

        var frontmatterEnd = Array.FindIndex(
            lines,
            1,
            static line => line.Trim() == "---");
        if (frontmatterEnd < 0)
        {
            throw InvalidAgent(
                agent,
                path,
                "missing closing YAML frontmatter delimiter");
        }

        var scalars = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        var lists = new Dictionary<string, List<string>>(
            StringComparer.OrdinalIgnoreCase);
        string? activeList = null;
        for (var index = 1; index < frontmatterEnd; index++)
        {
            var raw = lines[index];
            var trimmed = raw.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;

            if (activeList is not null
                && trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                lists[activeList].Add(
                    Unquote(trimmed[2..].Trim()));
                continue;
            }

            activeList = null;
            var separator = trimmed.IndexOf(':');
            if (separator <= 0)
            {
                throw InvalidAgent(
                    agent,
                    path,
                    $"invalid frontmatter line {index + 1}");
            }

            var key = trimmed[..separator].Trim();
            var value = trimmed[(separator + 1)..].Trim();
            if (value.Length == 0)
            {
                activeList = key;
                lists[key] = [];
                continue;
            }

            if (value.StartsWith('[') && value.EndsWith(']'))
            {
                lists[key] = value[1..^1]
                    .Split(
                        ',',
                        StringSplitOptions.RemoveEmptyEntries
                        | StringSplitOptions.TrimEntries)
                    .Select(Unquote)
                    .ToList();
                continue;
            }

            scalars[key] = Unquote(value);
        }

        if (!scalars.TryGetValue(
                "description",
                out var description)
            || string.IsNullOrWhiteSpace(description))
        {
            throw InvalidAgent(
                agent,
                path,
                "description is required");
        }

        var prompt = string.Join(
            '\n',
            lines[(frontmatterEnd + 1)..]).Trim();
        if (prompt.Length == 0)
        {
            throw InvalidAgent(
                agent,
                path,
                "prompt body is required");
        }

        bool? infer = null;
        if (scalars.TryGetValue("infer", out var inferValue))
        {
            if (!bool.TryParse(inferValue, out var parsedInfer))
            {
                throw InvalidAgent(
                    agent,
                    path,
                    "infer must be true or false");
            }
            infer = parsedInfer;
        }

        scalars.TryGetValue("name", out var displayName);
        scalars.TryGetValue("model", out var model);
        if (!scalars.TryGetValue(
                "reasoning-effort",
                out var reasoningEffort))
        {
            scalars.TryGetValue(
                "reasoningEffort",
                out reasoningEffort);
        }
        lists.TryGetValue("tools", out var tools);

        return new CustomAgentConfig
        {
            Name = agent,
            DisplayName = displayName,
            Description = description,
            Prompt = prompt,
            Tools = tools is { Count: > 0 } ? tools : null,
            Model = model,
            ReasoningEffort = reasoningEffort,
            Infer = infer,
        };
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"')
                || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }
        return value;
    }

    private static ArgumentException InvalidAgent(
        string agent,
        string path,
        string problem) =>
        new(
            $"Custom agent '{agent}' at '{path}' is invalid: {problem}.",
            nameof(SessionRuntimeProfile.Agent));
}
