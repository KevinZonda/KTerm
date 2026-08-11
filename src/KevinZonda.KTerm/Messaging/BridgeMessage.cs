using System.Text.Json;
using System.Text.Json.Serialization;

namespace KevinZonda.KTerm.Messaging;

internal sealed class BridgeMessage
{
    [JsonPropertyName("version")]
    public int Version { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("requestId")]
    public string? RequestId { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; init; }
}

