using System.Text.Json.Serialization;
using Magpilot.Shared;
using Magpilot.Shared.Models;

namespace Magpilot.Host;

/// <summary>
/// Source-generated JSON metadata for the types the launcher exchanges
/// with the agent/hub over <c>System.Net.Http.Json</c>. Those extension
/// methods default to <see cref="System.Text.Json.JsonSerializerDefaults.Web"/>
/// (camelCase, case-insensitive), so this context mirrors those defaults and
/// is a drop-in replacement for the reflection-based
/// <c>ReadFromJsonAsync</c>/<c>PostAsJsonAsync</c>/<c>GetFromJsonAsync</c>
/// calls -- which is what lets the launcher compile and run under Native AOT.
/// </summary>
[JsonSourceGenerationOptions(System.Text.Json.JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(SessionStateInfo))]
[JsonSerializable(typeof(AcquireForHostBody))]
[JsonSerializable(typeof(ReleaseFromHostBody))]
[JsonSerializable(typeof(ReleaseRequestBody))]
[JsonSerializable(typeof(EnrollmentRedeemRequest))]
[JsonSerializable(typeof(EnrollmentRedeemResponse))]
[JsonSerializable(typeof(MagpilotPair.ErrorBody))]
[JsonSerializable(typeof(PairingClaimRequest))]
[JsonSerializable(typeof(PairingClaimResponse))]
[JsonSerializable(typeof(PairingClaimStatus))]
[JsonSerializable(typeof(LatestVersionInfo))]
internal sealed partial class HostWebJsonContext : JsonSerializerContext;

/// <summary>
/// Source-generated JSON metadata for payloads (de)serialized with the
/// General (PascalCase, case-sensitive) defaults rather than the web ones:
/// the SSE <see cref="StreamEvent"/> stream (the agent emits it via a raw
/// <c>JsonSerializer.Serialize&lt;StreamEvent&gt;(evt)</c> with no options)
/// and the UDP <c>DiscoveryReply</c> (raw <c>JsonSerializer.Deserialize</c>,
/// property names pinned by explicit <c>[JsonPropertyName]</c> tags). Kept
/// separate from <see cref="HostWebJsonContext"/> because a
/// <c>JsonSerializerContext</c> carries a single naming policy.
/// </summary>
[JsonSerializable(typeof(StreamEvent))]
[JsonSerializable(typeof(MagpilotPairDiscover.DiscoveryReply))]
internal sealed partial class HostGeneralJsonContext : JsonSerializerContext;
