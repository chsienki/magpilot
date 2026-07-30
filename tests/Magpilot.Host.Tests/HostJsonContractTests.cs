using System.Text.Json;
using Magpilot.Host;
using Magpilot.Shared.Models;
using Xunit;

namespace Magpilot.Host.Tests;

/// <summary>
/// Locks the wire contract between the agent's serialization and the
/// launcher's source-generated JSON contexts (<see cref="HostGeneralJsonContext"/>
/// and <see cref="HostWebJsonContext"/>). These contexts exist so the launcher
/// (de)serializes without reflection under Native AOT; a naming-policy slip in
/// either context would silently break SSE parsing or the agent HTTP calls, so
/// the assertions pin the exact shapes the agent emits.
/// </summary>
public class HostJsonContractTests
{
    // The agent streams SSE events via a raw JsonSerializer.Serialize<StreamEvent>(evt)
    // with no options -> General (PascalCase) naming + the "type" discriminator.
    [Fact]
    public void StreamEvent_parses_the_agent_wire_shape()
    {
        const string wire = """{"type":"release_requested","Requester":"spa","Force":true}""";

        var evt = JsonSerializer.Deserialize(wire, HostGeneralJsonContext.Default.StreamEvent);

        var released = Assert.IsType<ReleaseRequested>(evt);
        Assert.Equal("spa", released.Requester);
        Assert.True(released.Force);
    }

    [Theory]
    [InlineData("assistant_delta")]
    [InlineData("tool_call_end")]
    [InlineData("release_requested")]
    public void StreamEvent_roundtrips_through_the_source_gen_context(string _)
    {
        StreamEvent[] events =
        [
            new AssistantDelta("hello"),
            new ToolCallEnd("call-1", "done", Success: true),
            new ReleaseRequested("whatsapp", Force: false),
        ];

        foreach (var original in events)
        {
            // Mirror the agent: serialize the polymorphic base with default options.
            var wire = JsonSerializer.Serialize<StreamEvent>(original);
            var parsed = JsonSerializer.Deserialize(wire, HostGeneralJsonContext.Default.StreamEvent);
            Assert.Equal(original, parsed);
        }
    }

    // The agent returns SessionStateInfo through ASP.NET's web-defaults JSON
    // (camelCase); the string-backed enums stay strings, SessionInfo.State is an
    // integer ordinal.
    [Fact]
    public void SessionStateInfo_parses_the_agent_web_shape()
    {
        const string wire = """
        {
          "info": { "id": "s1", "state": 1, "cwd": "/w", "repository": null,
                    "branch": null, "summary": null, "ownerPid": 123,
                    "createdAt": null, "updatedAt": null, "yolo": false },
          "owner": "Host",
          "hostPid": 999,
          "activity": "InFlight",
          "inFlight": { "driver": "spa", "startedAtMs": 100, "preview": "hi" },
          "lastEvent": { "type": "assistant_delta", "id": "e1", "timestamp": null }
        }
        """;

        var state = JsonSerializer.Deserialize(wire, HostWebJsonContext.Default.SessionStateInfo);

        Assert.NotNull(state);
        Assert.Equal("s1", state!.Info.Id);
        Assert.Equal(SessionState.Locked, state.Info.State);
        Assert.Equal(SessionOwner.Host, state.Owner);
        Assert.Equal(999, state.HostPid);
        Assert.Equal(SessionActivity.InFlight, state.Activity);
        Assert.Equal("spa", state.InFlight!.Driver);
        Assert.Equal("assistant_delta", state.LastEvent!.Type);
    }

    // UDP discovery replies are parsed with the General context; the property
    // names are pinned by explicit [JsonPropertyName] tags.
    [Fact]
    public void DiscoveryReply_parses_the_hub_wire_shape()
    {
        const string wire = """{"magic":"MAGPILOT-PAIR-DISCOVER-v1","hubUrl":"http://h:5099","hubName":"home"}""";

        var reply = JsonSerializer.Deserialize(
            System.Text.Encoding.UTF8.GetBytes(wire),
            HostGeneralJsonContext.Default.DiscoveryReply);

        Assert.NotNull(reply);
        Assert.Equal("MAGPILOT-PAIR-DISCOVER-v1", reply!.Magic);
        Assert.Equal("http://h:5099", reply.HubUrl);
        Assert.Equal("home", reply.HubName);
    }

    // The magpilot2+ enrollment bundle codec (Shared) moved to a source-gen
    // context; this pins the camelCase wire shape and the Encode/TryDecode
    // round-trip the launcher's --magpilot-pair path depends on.
    [Fact]
    public void EnrollmentBundle_roundtrips_and_stays_camelCase()
    {
        var bundle = new EnrollmentBundle("http://hub:5099", "voucher-abc", "bearer-xyz");

        var encoded = bundle.Encode();
        Assert.StartsWith("magpilot2+", encoded);

        Assert.True(EnrollmentBundle.TryDecode(encoded, out var decoded, out var error));
        Assert.Null(error);
        Assert.Equal(bundle, decoded);

        // Decode the base64url payload and assert the JSON keys are camelCase
        // -- a PascalCase slip in EnrollmentBundleJsonContext would break the
        // wire contract with the hub's /admin/enroll page.
        var b64 = encoded["magpilot2+".Length..].Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        Assert.Contains("\"hubUrl\"", json);
        Assert.Contains("\"voucher\"", json);
        Assert.Contains("\"hubBearer\"", json);
        Assert.DoesNotContain("\"HubUrl\"", json);
    }
}
