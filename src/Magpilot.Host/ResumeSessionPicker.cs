using Magpilot.Shared.Models;

namespace Magpilot.Host;

internal static class ResumeSessionPicker
{
    private const int PageSize = 8;

    public static IReadOnlyList<string> WithSelectedSession(IReadOnlyList<string> args, string sessionId)
    {
        if (!Guid.TryParseExact(sessionId, "D", out _))
            throw new ArgumentException("A session UUID is required.", nameof(sessionId));

        var index = -1;
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] is "--continue" or "--session-id" ||
                args[i].StartsWith("--session-id=", StringComparison.Ordinal) ||
                args[i].StartsWith("--resume=", StringComparison.Ordinal))
                throw new ArgumentException("Bare --resume cannot be combined with another session selector.", nameof(args));

            if (args[i] is "--resume" or "-r")
            {
                if (i + 1 < args.Count && !args[i + 1].StartsWith('-'))
                    throw new ArgumentException("Bare --resume cannot be combined with another resume value.", nameof(args));
                if (index != -1)
                    throw new ArgumentException("More than one bare --resume flag was supplied.", nameof(args));
                index = i;
            }
        }

        if (index == -1)
            throw new ArgumentException("No bare --resume or -r flag was supplied.", nameof(args));

        var result = args.ToArray();
        result[index] = $"--resume={sessionId}";
        return result;
    }

    public static SessionInfo? Choose(
        IReadOnlyList<SessionInfo> sessions,
        string cwd,
        TextReader input,
        TextWriter output)
    {
        var ordered = sessions
            .Where(s => Guid.TryParseExact(s.Id, "D", out _))
            .OrderByDescending(s => SameDirectory(s.Cwd, cwd))
            .ThenByDescending(s => s.UpdatedAt ?? s.CreatedAt ?? DateTimeOffset.MinValue)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .ToArray();

        if (ordered.Length == 0)
        {
            output.WriteLine("magpilot: no Copilot sessions found.");
            return null;
        }

        var filter = "";
        var page = 0;
        while (true)
        {
            var matches = ordered.Where(s => Matches(s, filter)).ToArray();
            var pages = Math.Max(1, (matches.Length + PageSize - 1) / PageSize);
            page = Math.Min(page, pages - 1);
            output.WriteLine();
            output.WriteLine($"Copilot sessions ({matches.Length} matching, page {page + 1}/{pages}; current directory first)");
            if (filter.Length > 0)
                output.WriteLine($"Search: {filter}");
            for (var i = 0; i < PageSize && page * PageSize + i < matches.Length; i++)
            {
                var session = matches[page * PageSize + i];
                var title = Shorten(SafeText(session.Summary ?? "(untitled)"), 70);
                var path = Shorten(SafeText(session.Cwd ?? "(unknown directory)"), 90);
                var updated = session.UpdatedAt ?? session.CreatedAt;
                var when = updated is { } timestamp ? timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "unknown";
                output.WriteLine($"  {i + 1}. {title}  [{session.Id[..8]}]  {session.State}");
                output.WriteLine($"     {path}  {when}");
            }
            if (matches.Length == 0)
                output.WriteLine("  No matching sessions.");

            output.Write("Choose 1-8, n/p pages, type to search (/123 for numeric IDs), / clear, q cancel: ");
            var answer = input.ReadLine()?.Trim();
            if (answer is null || answer.Equals("q", StringComparison.OrdinalIgnoreCase))
                return null;
            if (answer == "/")
            {
                filter = "";
                page = 0;
            }
            else if (answer.Equals("n", StringComparison.OrdinalIgnoreCase))
                page = Math.Min(page + 1, pages - 1);
            else if (answer.Equals("p", StringComparison.OrdinalIgnoreCase))
                page = Math.Max(page - 1, 0);
            else if (int.TryParse(answer, out var choice))
            {
                var index = page * PageSize + choice - 1;
                if (choice >= 1 && choice <= PageSize && index < matches.Length)
                    return matches[index];
                output.WriteLine("magpilot: choose a displayed number.");
            }
            else if (answer.Length > 0)
            {
                filter = answer.TrimStart('/');
                page = 0;
            }
        }
    }

    private static bool Matches(SessionInfo session, string filter) =>
        filter.Length == 0 ||
        (session.Summary?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (session.Cwd?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (session.Repository?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (session.Branch?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false) ||
        session.Id.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private static bool SameDirectory(string? left, string right) =>
        left is not null &&
        string.Equals(
            NormalizeDirectory(left),
            NormalizeDirectory(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string NormalizeDirectory(string path) =>
        (OperatingSystem.IsWindows() ? path.Replace('/', '\\') : path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string SafeText(string text) =>
        new(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray());

    private static string Shorten(string text, int limit) =>
        text.Length <= limit ? text : string.Concat(text.AsSpan(0, limit - 3), "...");
}
