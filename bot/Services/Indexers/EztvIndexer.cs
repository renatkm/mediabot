using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBot.Models;

namespace MediaBot.Services.Indexers;

public class EztvIndexer : ITorrentIndexer
{
    private readonly HttpClient _http;
    private readonly ILogger<EztvIndexer> _logger;

    public string Name => "EZTV";

    public EztvIndexer(HttpClient http, ILogger<EztvIndexer> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<List<SearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        try
        {
            var url = $"https://eztv.re/api/get-torrents?limit=20&keywords={Uri.EscapeDataString(query)}";
            var response = await _http.GetStringAsync(url, ct);
            var data = JsonSerializer.Deserialize<EztvResponse>(response);

            if (data?.Torrents == null) return [];

            return data.Torrents
                .Where(t => t.Seeds > 0)
                .Select(t => new SearchResult
                {
                    Title = t.Title,
                    MagnetLink = t.MagnetUrl,
                    TorrentUrl = t.TorrentUrl,
                    SizeBytes = long.TryParse(t.SizeBytes, out var size) ? size : 0,
                    Seeders = t.Seeds,
                    Indexer = Name
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EZTV search failed for '{Query}'", query);
            return [];
        }
    }

    private record EztvResponse(
        [property: JsonPropertyName("torrents")] List<EztvTorrent>? Torrents
    );

    private record EztvTorrent(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("magnet_url")] string MagnetUrl,
        [property: JsonPropertyName("torrent_url")] string TorrentUrl,
        [property: JsonPropertyName("size_bytes")] string SizeBytes,
        [property: JsonPropertyName("seeds")] int Seeds
    );
}
