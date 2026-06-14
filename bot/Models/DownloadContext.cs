namespace MediaBot.Models;

public class DownloadContext
{
    public required string Hash { get; init; }
    public required List<TorrentFile> AllFiles { get; init; }
    public required List<TorrentFile> VideoFiles { get; init; }
    public string? TorrentName { get; init; }
    public string? Category { get; init; }  // "movie", "series", "music", "ebook"
}
