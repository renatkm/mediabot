using System.Collections.Concurrent;
using MediaBot.Models;

namespace MediaBot.Services;

public class BotStateService
{
    // chatId -> search results (for torrent selection)
    private readonly ConcurrentDictionary<long, List<SearchResult>> _searchCache = new();

    // chatId -> download context (for file selection after torrent added)
    private readonly ConcurrentDictionary<long, DownloadContext> _downloadContexts = new();

    // torrent hash -> chatId (for completion notifications)
    private readonly ConcurrentDictionary<string, long> _pendingNotifications = new();

    // torrent hash -> human-readable name for notification message
    private readonly ConcurrentDictionary<string, string> _torrentNames = new();

    public void SetSearchResults(long chatId, List<SearchResult> results)
        => _searchCache[chatId] = results;

    public SearchResult? GetSearchResult(long chatId, int index)
    {
        if (_searchCache.TryGetValue(chatId, out var results) && index < results.Count)
            return results[index];
        return null;
    }

    public List<SearchResult>? GetAllSearchResults(long chatId)
        => _searchCache.TryGetValue(chatId, out var results) ? results : null;

    public void SetDownloadContext(long chatId, DownloadContext ctx)
        => _downloadContexts[chatId] = ctx;

    public DownloadContext? GetDownloadContext(long chatId)
        => _downloadContexts.TryGetValue(chatId, out var ctx) ? ctx : null;

    public void RegisterPendingNotification(string hash, long chatId, string name)
    {
        _pendingNotifications[hash] = chatId;
        _torrentNames[hash] = name;
    }

    public bool TryConsumePendingNotification(string hash, out long chatId, out string name)
    {
        chatId = 0;
        name = "";
        if (_pendingNotifications.TryRemove(hash, out chatId))
        {
            _torrentNames.TryRemove(hash, out name!);
            return true;
        }
        return false;
    }
}
