namespace Clawpilot.Shared.Models;

public sealed record NewSessionRequest(string? Cwd, string? Name);
public sealed record PromptRequest(string Text);
public sealed record AdoptRequest(bool Force = false);
public sealed record ApprovalResponse(string OptionId);

public sealed record SessionDetails(SessionInfo Info, string? AcpSessionId);
