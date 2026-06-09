using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace MediaBot.Services;

public class DownloadMonitorService : BackgroundService
{
    private readonly QBittorrentService _qbit;
    private readonly JellyfinService _jellyfin;
    private readonly BotStateService _state;
    private readonly ILogger<DownloadMonitorService> _logger;
    private TelegramBotClient? _bot;

    public DownloadMonitorService(
        QBittorrentService qbit,
        JellyfinService jellyfin,
        BotStateService state,
        ILogger<DownloadMonitorService> logger)
    {
        _qbit = qbit;
        _jellyfin = jellyfin;
        _state = state;
        _logger = logger;
    }

    public void SetBotClient(TelegramBotClient bot) => _bot = bot;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("Download monitor started");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckCompletedDownloadsAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in download monitor");
            }

            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        }
    }

    private async Task CheckCompletedDownloadsAsync(CancellationToken ct)
    {
        if (_bot == null) return;

        List<Models.TorrentInfo> torrents;
        try
        {
            torrents = await _qbit.GetTorrentsAsync(ct);
        }
        catch
        {
            return; // qBittorrent might not be ready yet
        }

        foreach (var torrent in torrents.Where(t => t.Progress >= 1.0))
        {
            if (_state.TryConsumePendingNotification(torrent.Hash, out var chatId, out var name))
            {
                _logger.LogInformation("Download complete: {Name}", name);

                try
                {
                    // Trigger Jellyfin scan so the new file appears immediately
                    await _jellyfin.TriggerLibraryScanAsync(ct);

                    // Give Jellyfin a moment to index before searching
                    await Task.Delay(5000, ct);

                    var items = await _jellyfin.SearchAsync(name, ct);
                    var item = items.FirstOrDefault();

                    string message;
                    if (item != null)
                    {
                        var streamUrl = _jellyfin.GetStreamUrl(item.Id);
                        message = $"✅ <b>{HtmlEscape(name)}</b> готово!\n<a href=\"{streamUrl}\">▶️ Смотреть в Jellyfin</a>";
                    }
                    else
                    {
                        message = $"✅ <b>{HtmlEscape(name)}</b> скачано!\nОткройте Jellyfin для просмотра.";
                    }

                    await _bot.SendMessage(chatId, message, parseMode: ParseMode.Html);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send completion notification for {Name}", name);
                }
            }
        }
    }

    private static string HtmlEscape(string text)
        => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
