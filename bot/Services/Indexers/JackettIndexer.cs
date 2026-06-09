using System.Text.Json;
using MediaBot.Configuration;
using MediaBot.Models;
using Microsoft.Extensions.Options;

namespace MediaBot.Services.Indexers;

public class JackettIndexer : ITorrentIndexer
{
    private readonly HttpClient _http;
    private readonly JackettConfig _config;
    private readonly ILogger<JackettIndexer> _logger;
    private readonly string _indexerId;

    public string Name { get; }

    public JackettIndexer(
        HttpClient http,
        IOptions<JackettConfig> config,
        ILogger<JackettIndexer> logger,
        string indexerId,
        string displayName)
    {
        _http = http;
        _config = config.Value;
        _logger = logger;
        _indexerId = indexerId;
        Name = displayName;
    }

    public async Task<List<SearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_config.ApiKey))
        {
            _logger.LogWarning("Jackett API key not configured, skipping {Indexer}", Name);
            return [];
        }

        try
        {
            var url = $"{_config.BaseUrl}/api/v2.0/indexers/{_indexerId}/results" +
                      $"?apikey={_config.ApiKey}&Query={Uri.EscapeDataString(query)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("Accept", "application/json");

            using var response = await _http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var data = JsonSerializer.Deserialize<JackettResponse>(json);

            if (data?.Results == null) return [];

            return data.Results
                .Where(r => r.Seeders > 0)
                .Select(r => new SearchResult
                {
                    Title = r.Title,
                    MagnetLink = r.MagnetUri,
                    TorrentUrl = r.Link,
                    SizeBytes = r.Size,
                    Seeders = r.Seeders,
                    Indexer = Name
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jackett search failed for '{Query}' on {Indexer}", query, Name);
            return [];
        }
    }
}
