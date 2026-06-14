using System.Text.Json;
using MediaBot.Configuration;
using MediaBot.Models;
using Microsoft.Extensions.Options;

namespace MediaBot.Services;

public class JellyfinService
{
    private readonly HttpClient _http;
    private readonly JellyfinConfig _config;
    private readonly ILogger<JellyfinService> _logger;

    public JellyfinService(HttpClient http, IOptions<JellyfinConfig> config, ILogger<JellyfinService> logger)
    {
        _http = http;
        _config = config.Value;
        _http.BaseAddress = new Uri(_config.BaseUrl);
        _logger = logger;
    }

    public bool IsConfigured => !string.IsNullOrEmpty(_config.ApiKey);

    public async Task<List<JellyfinItem>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (!IsConfigured) return [];

        try
        {
            var cleanQuery = ExtractTitle(query);
            var url = $"/Items?api_key={_config.ApiKey}" +
                      $"&searchTerm={Uri.EscapeDataString(cleanQuery)}" +
                      $"&IncludeItemTypes=Movie,Episode,Series" +
                      $"&Recursive=true" +
                      $"&Fields=Overview,RunTimeTicks,ProductionYear" +
                      $"&Limit=10";

            var response = await _http.GetStringAsync(url, ct);
            var result = JsonSerializer.Deserialize<JellyfinResponse>(response);
            return result?.Items ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jellyfin search failed for '{Query}'", query);
            return [];
        }
    }

    // Strips quality tags and dots from torrent names to get a clean search term.
    // "Foundation.S03.WEB-DLRip.HDRezka" → "Foundation"
    // "Game.of.Thrones.S01E01.1080p.BluRay" → "Game of Thrones"
    private static string ExtractTitle(string torrentName)
    {
        // Replace dots/underscores used as spaces
        var spaced = torrentName.Replace('.', ' ').Replace('_', ' ');

        // Cut at season/episode marker or common quality tokens
        var stopPattern = new System.Text.RegularExpressions.Regex(
            @"\b(S\d{1,2}(E\d{1,2})?|E\d{1,2}|\d{3,4}[pi]|WEB|BDRip|BluRay|HDTV|DVDRip|HDRezka|Blu-Ray|REMUX|WEBRip|WEB-DL)\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var match = stopPattern.Match(spaced);
        var title = match.Success ? spaced[..match.Index].Trim() : spaced.Trim();

        return string.IsNullOrWhiteSpace(title) ? torrentName : title;
    }

    public async Task<List<JellyfinItem>> GetRecentAsync(int limit = 10, CancellationToken ct = default)
    {
        if (!IsConfigured) return [];

        try
        {
            var url = $"/Items?api_key={_config.ApiKey}" +
                      $"&IncludeItemTypes=Movie,Episode,Series" +
                      $"&SortBy=DateCreated" +
                      $"&SortOrder=Descending" +
                      $"&Limit={limit}" +
                      $"&Recursive=true" +
                      $"&Fields=Overview,RunTimeTicks,ProductionYear";

            var response = await _http.GetStringAsync(url, ct);
            var result = JsonSerializer.Deserialize<JellyfinResponse>(response);
            return result?.Items ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Jellyfin GetRecent failed");
            return [];
        }
    }

    public async Task TriggerLibraryScanAsync(CancellationToken ct = default)
    {
        if (!IsConfigured) return;
        try
        {
            await _http.PostAsync($"/Library/Refresh?api_key={_config.ApiKey}", null, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to trigger Jellyfin library scan");
        }
    }

    public string GetStreamUrl(string itemId)
    {
        var baseUrl = !string.IsNullOrEmpty(_config.PublicUrl) ? _config.PublicUrl : _config.BaseUrl;
        return $"{baseUrl}/Videos/{itemId}/stream?api_key={_config.ApiKey}&static=true";
    }

    public string GetPosterUrl(string itemId)
        => $"{_config.BaseUrl}/Items/{itemId}/Images/Primary?api_key={_config.ApiKey}&width=300";
}
