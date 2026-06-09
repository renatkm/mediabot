using System.Text.Json.Serialization;

namespace MediaBot.Models;

public record JellyfinResponse(
    [property: JsonPropertyName("Items")] List<JellyfinItem> Items,
    [property: JsonPropertyName("TotalRecordCount")] int TotalRecordCount
);

public record JellyfinItem(
    [property: JsonPropertyName("Id")] string Id,
    [property: JsonPropertyName("Name")] string Name,
    [property: JsonPropertyName("Type")] string Type,
    [property: JsonPropertyName("Overview")] string? Overview,
    [property: JsonPropertyName("ProductionYear")] int? Year,
    [property: JsonPropertyName("RunTimeTicks")] long? RunTimeTicks
)
{
    public TimeSpan? Duration => RunTimeTicks.HasValue
        ? TimeSpan.FromTicks(RunTimeTicks.Value)
        : null;

    public string TypeEmoji => Type switch
    {
        "Movie" => "🎬",
        "Episode" => "📺",
        "Series" => "📺",
        _ => "🎞"
    };
}
