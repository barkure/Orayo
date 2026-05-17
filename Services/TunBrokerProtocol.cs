using System.Text.Json.Serialization;

namespace Orayo.Services;

public static class TunBrokerProtocol
{
    public const string PipeName = "Orayo.TunBroker";
}

public sealed class TunBrokerRequest
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("configJson")]
    public string? ConfigJson { get; set; }

}

public sealed class TunBrokerResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("isRunning")]
    public bool IsRunning { get; set; }

    [JsonPropertyName("errorTitle")]
    public string? ErrorTitle { get; set; }

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }
}
