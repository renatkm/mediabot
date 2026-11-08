namespace MediaBot.Models;

public record SearchResult
{
    public required string Title { get; init; }
    public string? MagnetLink { get; init; }
    public string? TorrentUrl { get; init; }
    public string? DetailsUrl { get; init; }
    public long SizeBytes { get; init; }
    public int Seeders { get; init; }
    public required string Indexer { get; init; }

    public string FormattedSize => SizeBytes switch
    {
        >= 1_073_741_824 => $"{SizeBytes / 1_073_741_824.0:F1} GB",
        >= 1_048_576 => $"{SizeBytes / 1_048_576.0:F0} MB",
        _ => $"{SizeBytes / 1024} KB"
    };
}
