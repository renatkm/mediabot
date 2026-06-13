using System.Text.Json.Serialization;

namespace MediaBot.Models;

public record TorrentFile(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("priority")] int Priority
)
{
    public string FormattedSize => Size switch
    {
        >= 1_073_741_824 => $"{Size / 1_073_741_824.0:F1} GB",
        >= 1_048_576 => $"{Size / 1_048_576.0:F0} MB",
        _ => $"{Size / 1024} KB"
    };
}
