using System.Text.Json;
using MediaBot.Configuration;
using MediaBot.Models;
using Microsoft.Extensions.Options;

namespace MediaBot.Services;

public class QBittorrentService
{
    private readonly HttpClient _http;
    private readonly QBittorrentConfig _config;
    private readonly ILogger<QBittorrentService> _logger;
    private bool _loggedIn;

    public QBittorrentService(HttpClient http, IOptions<QBittorrentConfig> config, ILogger<QBittorrentService> logger)
    {
        _http = http;
        _config = config.Value;
        _http.BaseAddress = new Uri(_config.BaseUrl);
        _http.Timeout = TimeSpan.FromSeconds(30);
        _logger = logger;
    }

    private async Task EnsureLoggedInAsync(CancellationToken ct = default)
    {
        if (_loggedIn) return;

        // Try a lightweight API call first — succeeds when bypass auth is enabled
        var check = await _http.GetAsync("/api/v2/torrents/info", ct);
        if (check.IsSuccessStatusCode)
        {
            _loggedIn = true;
            _logger.LogInformation("qBittorrent: bypass auth active, no login needed");
            return;
        }

        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("username", _config.Username),
            new KeyValuePair<string, string>("password", _config.Password),
        });

        var response = await _http.PostAsync("/api/v2/auth/login", content, ct);
        var body = (await response.Content.ReadAsStringAsync(ct)).Trim();

        if (body == "Ok.")
        {
            _loggedIn = true;
            _logger.LogInformation("qBittorrent login successful");
        }
        else
        {
            throw new InvalidOperationException($"qBittorrent login failed: {body}. Check credentials or enable bypass auth in qBittorrent Settings → Web UI.");
        }
    }

    public async Task<string?> AddTorrentAsync(string? magnetLink = null, byte[]? torrentBytes = null, CancellationToken ct = default)
    {
        await EnsureLoggedInAsync(ct);

        var existingHashes = (await GetTorrentsAsync(ct))
            .Select(t => t.Hash)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        HttpContent content;
        if (torrentBytes != null)
        {
            var multipart = new MultipartFormDataContent();
            multipart.Add(new ByteArrayContent(torrentBytes), "torrents", "file.torrent");
            multipart.Add(new StringContent("true"), "paused");
            multipart.Add(new StringContent("/downloads"), "savepath");
            content = multipart;
        }
        else
        {
            content = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("urls", magnetLink!),
                new KeyValuePair<string, string>("savepath", "/downloads"),
                new KeyValuePair<string, string>("paused", "true"),
            });
        }

        await _http.PostAsync("/api/v2/torrents/add", content, ct);

        // Wait for the torrent to appear
        string? hash = null;
        for (int i = 0; i < 30; i++)
        {
            await Task.Delay(2000, ct);
            var torrents = await GetTorrentsAsync(ct);
            var newTorrent = torrents.FirstOrDefault(t => !existingHashes.Contains(t.Hash));
            if (newTorrent != null)
            {
                hash = newTorrent.Hash;
                _logger.LogInformation("Torrent added: {Name} ({Hash})", newTorrent.Name, newTorrent.Hash);
                break;
            }
        }

        if (hash == null)
        {
            _logger.LogWarning("Torrent did not appear in qBittorrent after 60 seconds");
            return null;
        }

        // For magnet links, wait until metadata is fetched (state leaves metaDL)
        // so that GetFilesAsync returns actual file list
        if (magnetLink != null)
        {
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(2000, ct);
                var torrents = await GetTorrentsAsync(ct);
                var torrent = torrents.FirstOrDefault(t => t.Hash == hash);
                if (torrent != null && torrent.State != "metaDL")
                {
                    _logger.LogInformation("Metadata ready for {Hash}, state: {State}", hash, torrent.State);
                    break;
                }
            }
        }

        return hash;
    }

    public async Task<List<TorrentInfo>> GetTorrentsAsync(CancellationToken ct = default)
    {
        await EnsureLoggedInAsync(ct);
        var response = await _http.GetStringAsync("/api/v2/torrents/info", ct);
        return JsonSerializer.Deserialize<List<TorrentInfo>>(response) ?? [];
    }

    public async Task<List<TorrentFile>> GetFilesAsync(string hash, CancellationToken ct = default)
    {
        await EnsureLoggedInAsync(ct);
        var response = await _http.GetStringAsync($"/api/v2/torrents/files?hash={hash}", ct);
        return JsonSerializer.Deserialize<List<TorrentFile>>(response) ?? [];
    }

    public async Task SetFilePrioritiesAsync(string hash, int[] enabledIndexes, int totalFiles, CancellationToken ct = default)
    {
        await EnsureLoggedInAsync(ct);

        var priorities = Enumerable.Range(0, totalFiles)
            .Select(i => enabledIndexes.Contains(i) ? "1" : "0")
            .ToArray();

        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("hash", hash),
            new KeyValuePair<string, string>("id", string.Join("|", Enumerable.Range(0, totalFiles))),
            new KeyValuePair<string, string>("priority", string.Join("|", priorities)),
        });

        await _http.PostAsync("/api/v2/torrents/filePrio", content, ct);
    }

    public async Task ResumeAsync(string hash, CancellationToken ct = default)
    {
        await EnsureLoggedInAsync(ct);
        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("hashes", hash),
        });
        await _http.PostAsync("/api/v2/torrents/resume", content, ct);
    }

    public async Task DeleteAsync(string hash, bool deleteFiles = false, CancellationToken ct = default)
    {
        await EnsureLoggedInAsync(ct);
        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("hashes", hash),
            new KeyValuePair<string, string>("deleteFiles", deleteFiles.ToString().ToLower()),
        });
        await _http.PostAsync("/api/v2/torrents/delete", content, ct);
    }
}
