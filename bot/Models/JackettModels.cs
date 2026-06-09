using System.Text.Json.Serialization;

namespace MediaBot.Models;

public record JackettResponse(
    [property: JsonPropertyName("Results")] List<JackettResult> Results
);

public record JackettResult(
    [property: JsonPropertyName("Title")] string Title,
    [property: JsonPropertyName("MagnetUri")] string? MagnetUri,
    [property: JsonPropertyName("Link")] string? Link,
    [property: JsonPropertyName("Size")] long Size,
    [property: JsonPropertyName("Seeders")] int Seeders,
    [property: JsonPropertyName("Tracker")] string? Tracker
);
