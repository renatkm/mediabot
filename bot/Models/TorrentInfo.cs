using System.Text.Json.Serialization;

namespace MediaBot.Models;

public record TorrentInfo(
    [property: JsonPropertyName("hash")] string Hash,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("progress")] double Progress,
    [property: JsonPropertyName("dlspeed")] long DlSpeed,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("state")] string State
)
{
    public string FormattedProgress => $"{Progress * 100:F0}%";
    public string FormattedSpeed => DlSpeed switch
    {
        >= 1_048_576 => $"{DlSpeed / 1_048_576.0:F1} MB/s",
        >= 1_024 => $"{DlSpeed / 1024.0:F0} KB/s",
        _ => $"{DlSpeed} B/s"
    };
}
