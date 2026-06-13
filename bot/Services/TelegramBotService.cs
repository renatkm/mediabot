using MediaBot.Configuration;
using MediaBot.Models;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace MediaBot.Services;

public class TelegramBotService : BackgroundService
{
    private readonly BotConfig _config;
    private readonly TorrentAggregator _aggregator;
    private readonly QBittorrentService _qbit;
    private readonly JellyfinService _jellyfin;
    private readonly BotStateService _state;
    private readonly DownloadMonitorService _monitor;
    private readonly UserRegistryService _registry;
    private readonly ILogger<TelegramBotService> _logger;
    private TelegramBotClient _bot = null!;

    public TelegramBotService(
        IOptions<BotConfig> config,
        TorrentAggregator aggregator,
        QBittorrentService qbit,
        JellyfinService jellyfin,
        BotStateService state,
        DownloadMonitorService monitor,
        UserRegistryService registry,
        ILogger<TelegramBotService> logger)
    {
        _config = config.Value;
        _aggregator = aggregator;
        _qbit = qbit;
        _jellyfin = jellyfin;
        _state = state;
        _monitor = monitor;
        _registry = registry;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _bot = new TelegramBotClient(_config.Token, cancellationToken: ct);
        _monitor.SetBotClient(_bot);

        var me = await _bot.GetMe();
        _logger.LogInformation("Bot started: @{Username}", me.Username);

        await _bot.SetMyCommands(
        [
            new BotCommand { Command = "search",    Description = "Найти и скачать торрент" },
            new BotCommand { Command = "find",      Description = "Найти в библиотеке Jellyfin" },
            new BotCommand { Command = "library",   Description = "Последние добавленные" },
            new BotCommand { Command = "downloads", Description = "Текущие загрузки" },
            new BotCommand { Command = "help",      Description = "Помощь" },
        ], cancellationToken: ct);

        _bot.OnError += OnError;
        _bot.OnMessage += OnMessage;
        _bot.OnUpdate += OnUpdate;

        await Task.Delay(Timeout.Infinite, ct);
    }

    private async Task OnError(Exception exception, HandleErrorSource source)
    {
        _logger.LogError(exception, "Telegram error from {Source}", source);
    }

    private async Task OnMessage(Message msg, UpdateType type)
    {
        if (msg.Text == null) return;

        var chatId = msg.Chat.Id;
        var text = msg.Text.Trim();

        // /start is always allowed — it's the registration entry point
        if (text.StartsWith("/start"))
        {
            try { await HandleStart(chatId, text); } catch (Exception ex) { _logger.LogError(ex, "Error in /start for {ChatId}", chatId); }
            return;
        }

        if (!IsAllowed(chatId))
        {
            await _bot.SendMessage(chatId, "⛔ Нет доступа. Отправьте /start <код приглашения>");
            return;
        }

        try
        {
            // If user sends any command while a torrent is awaiting file selection, abandon it
            if (text.StartsWith('/'))
            {
                var abandoned = _state.ConsumeDownloadContext(chatId);
                if (abandoned != null)
                    await _qbit.DeleteAsync(abandoned.Hash, deleteFiles: false);
            }

            if (text == "/help")
                await HandleHelp(chatId);
            else if (text.Equals("/search", StringComparison.OrdinalIgnoreCase))
            {
                _state.SetPendingInput(chatId, "search");
                await _bot.SendMessage(chatId, "🔍 Введите название для поиска:");
            }
            else if (text.StartsWith("/search ", StringComparison.OrdinalIgnoreCase))
                await HandleSearchCommand(chatId, text[8..].Trim());
            else if (text.Equals("/find", StringComparison.OrdinalIgnoreCase))
            {
                _state.SetPendingInput(chatId, "find");
                await _bot.SendMessage(chatId, "🎬 Введите название для поиска в библиотеке:");
            }
            else if (text.StartsWith("/find ", StringComparison.OrdinalIgnoreCase))
                await HandleFindCommand(chatId, text[6..].Trim());
            else if (text == "/library")
                await HandleLibraryCommand(chatId);
            else if (text == "/downloads")
                await HandleDownloadsCommand(chatId);
            else
            {
                var pending = _state.ConsumePendingInput(chatId);
                if (pending == "search")
                    await HandleSearchCommand(chatId, text);
                else if (pending == "find")
                    await HandleFindCommand(chatId, text);
                else
                    await _bot.SendMessage(chatId, "Используйте /help для списка команд.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling message in chat {ChatId}", chatId);
            await SafeSendAsync(chatId, "❌ Произошла ошибка. Попробуйте снова.");
        }
    }

    private async Task OnUpdate(Update update)
    {
        if (update.CallbackQuery is { } callbackQuery)
        {
            var chatId = callbackQuery.Message?.Chat.Id ?? 0;
            if (!IsAllowed(chatId)) return;

            try
            {
                await HandleCallbackAsync(callbackQuery);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling callback in chat {ChatId}", chatId);
                await SafeSendAsync(chatId, "❌ Произошла ошибка. Попробуйте снова.");
            }
        }
    }

    private async Task HandleCallbackAsync(CallbackQuery callback)
    {
        if (callback.Data == null || callback.Message == null) return;

        var chatId = callback.Message.Chat.Id;
        var data = callback.Data;

        await _bot.AnswerCallbackQuery(callback.Id);

        if (data.StartsWith("src:"))
            await HandleSourceSelected(chatId, callback.Message.MessageId, data);
        else if (data.StartsWith("page:"))
            await HandlePageSelected(chatId, callback.Message.MessageId, data);
        else if (data.StartsWith("torrent:"))
            await HandleTorrentSelected(chatId, callback.Message.MessageId, data);
        else if (data.StartsWith("file:"))
            await HandleFileSelected(chatId, callback.Message.MessageId, data);
        else if (data.StartsWith("play:"))
            await HandlePlaySelected(chatId, data);
        else if (data.StartsWith("moreeps:"))
            await HandleMoreEpisodesSelected(chatId, callback.Message.MessageId, data);
        else if (data.StartsWith("morefile:"))
            await HandleMoreFileSelected(chatId, callback.Message.MessageId, data);
    }

    // ── Commands ─────────────────────────────────────────────────────────

    private async Task HandleStart(long chatId, string text)
    {
        var code = text.Length > 7 ? text[7..].Trim() : "";

        if (IsAllowed(chatId))
        {
            await HandleHelp(chatId);
            return;
        }

        if (string.IsNullOrEmpty(_config.InviteCode) || code != _config.InviteCode)
        {
            await _bot.SendMessage(chatId,
                "👋 Привет! Для доступа к боту введите:\n/start <код приглашения>");
            return;
        }

        _registry.Register(chatId);
        _logger.LogInformation("New user registered: {ChatId}", chatId);
        await HandleHelp(chatId);
    }

    private async Task HandleHelp(long chatId)
    {
        var text = """
            🎬 <b>MediaBot</b> — ваш личный кинотеатр

            Команды:
            /search &lt;запрос&gt; — поиск в торрентах
            /find &lt;запрос&gt; — поиск в вашей библиотеке
            /library — последние добавленные
            /downloads — текущие загрузки
            """;
        await _bot.SendMessage(chatId, text, parseMode: ParseMode.Html);
    }

    private async Task HandleSearchCommand(long chatId, string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            await SafeSendAsync(chatId, "Укажите запрос: /search Начало");
            return;
        }

        var indexerNames = _aggregator.IndexerNames;
        var rows = new List<InlineKeyboardButton[]>
        {
            new[] { InlineKeyboardButton.WithCallbackData("🌍 Все источники", $"src:ALL:{query}") }
        };

        var indexerRow = new List<InlineKeyboardButton>();
        foreach (var name in indexerNames)
        {
            indexerRow.Add(InlineKeyboardButton.WithCallbackData(name, $"src:{name}:{query}"));
            if (indexerRow.Count == 2)
            {
                rows.Add(indexerRow.ToArray());
                indexerRow.Clear();
            }
        }
        if (indexerRow.Count > 0) rows.Add(indexerRow.ToArray());

        await _bot.SendMessage(
            chatId,
            $"🔍 Где искать <b>{HtmlEscape(query)}</b>?",
            parseMode: ParseMode.Html,
            replyMarkup: new InlineKeyboardMarkup(rows));
    }

    private async Task HandleFindCommand(long chatId, string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            await SafeSendAsync(chatId, "Укажите запрос: /find Матрица");
            return;
        }

        var items = await _jellyfin.SearchAsync(query);

        if (items.Count == 0)
        {
            await _bot.SendMessage(
                chatId,
                $"❌ <b>{HtmlEscape(query)}</b> не найдено в библиотеке.\n" +
                $"Поискать в торрентах? /search {query}",
                parseMode: ParseMode.Html);
            return;
        }

        var buttons = items.Take(5).Select(item =>
            new[] { InlineKeyboardButton.WithCallbackData(
                $"{item.TypeEmoji} {item.Name} ({item.Year})",
                $"play:{item.Id}") }
        ).ToArray();

        await _bot.SendMessage(
            chatId,
            $"Найдено {items.Count} результатов в библиотеке:",
            replyMarkup: new InlineKeyboardMarkup(buttons));
    }

    private async Task HandleLibraryCommand(long chatId)
    {
        if (!_jellyfin.IsConfigured)
        {
            await SafeSendAsync(chatId, "⚠️ Jellyfin не настроен. Укажите API ключ в конфигурации.");
            return;
        }

        var items = await _jellyfin.GetRecentAsync(10);

        if (items.Count == 0)
        {
            await SafeSendAsync(chatId, "Библиотека пуста.");
            return;
        }

        var buttons = items.Select(item =>
            new[] { InlineKeyboardButton.WithCallbackData(
                $"{item.TypeEmoji} {item.Name} ({item.Year})",
                $"play:{item.Id}") }
        ).ToArray();

        await _bot.SendMessage(
            chatId,
            "🎬 <b>Последние добавленные:</b>",
            parseMode: ParseMode.Html,
            replyMarkup: new InlineKeyboardMarkup(buttons));
    }

    private async Task HandleDownloadsCommand(long chatId)
    {
        List<TorrentInfo> torrents;
        try
        {
            torrents = await _qbit.GetTorrentsAsync();
        }
        catch
        {
            await SafeSendAsync(chatId, "⚠️ qBittorrent недоступен.");
            return;
        }

        if (torrents.Count == 0)
        {
            await SafeSendAsync(chatId, "Нет активных загрузок.");
            return;
        }

        var lines = new System.Text.StringBuilder();
        var buttons = new List<InlineKeyboardButton[]>();

        foreach (var t in torrents)
        {
            if (t.Progress >= 1.0)
            {
                lines.AppendLine($"✅ <b>{HtmlEscape(t.Name)}</b>");

                var items = await _jellyfin.SearchAsync(t.Name);
                var item = items.FirstOrDefault();
                if (item != null)
                    buttons.Add([InlineKeyboardButton.WithCallbackData($"▶️ {item.Name}", $"play:{item.Id}")]);

                // Check for skipped episodes
                var files = await _qbit.GetFilesAsync(t.Hash);
                var skipped = files.Where(f => IsVideoFile(f.Name) && f.Priority == 0).ToList();
                if (skipped.Count > 0)
                    buttons.Add([InlineKeyboardButton.WithCallbackData($"📥 Скачать ещё эпизоды ({skipped.Count})", $"moreeps:{t.Hash}")]);
            }
            else
            {
                lines.AppendLine($"⬇️ <b>{HtmlEscape(t.Name)}</b>");
                lines.AppendLine($"   {t.FormattedProgress} • {t.FormattedSpeed}");
            }
            lines.AppendLine();
        }

        var markup = buttons.Count > 0 ? new InlineKeyboardMarkup(buttons) : null;
        await _bot.SendMessage(chatId, lines.ToString().TrimEnd(), parseMode: ParseMode.Html, replyMarkup: markup);
    }

    // ── Callback handlers ─────────────────────────────────────────────────

    private async Task HandleSourceSelected(long chatId, int msgId, string data)
    {
        // data = "src:SOURCENAME:query"
        var parts = data.Split(':', 3);
        if (parts.Length < 3) return;

        var source = parts[1];
        var query = parts[2];

        await _bot.EditMessageText(chatId, msgId,
            $"⏳ Ищу <b>{HtmlEscape(query)}</b> в {HtmlEscape(source)}...",
            parseMode: ParseMode.Html);

        var results = await _aggregator.SearchAsync(
            query,
            source == "ALL" ? null : source);

        if (results.Count == 0)
        {
            await _bot.EditMessageText(chatId, msgId,
                $"❌ Ничего не найдено по запросу <b>{HtmlEscape(query)}</b>",
                parseMode: ParseMode.Html);
            return;
        }

        _state.SetSearchResults(chatId, results);

        await ShowResultsPage(chatId, msgId, results, page: 0);
    }

    private async Task HandleTorrentSelected(long chatId, int msgId, string data)
    {
        // data = "torrent:INDEX"
        var idx = int.Parse(data.Split(':')[1]);
        var result = _state.GetSearchResult(chatId, idx);

        if (result == null)
        {
            await SafeSendAsync(chatId, "Сессия устарела. Выполните поиск заново.");
            return;
        }

        await _bot.EditMessageText(chatId, msgId,
            $"⏳ Добавляю <b>{HtmlEscape(result.Title)}</b> и получаю список файлов...",
            parseMode: ParseMode.Html);

        string? hash;
        try
        {
            if (result.MagnetLink != null)
            {
                hash = await _qbit.AddTorrentAsync(magnetLink: result.MagnetLink);
            }
            else if (result.TorrentUrl != null)
            {
                using var httpClient = new HttpClient();
                var bytes = await httpClient.GetByteArrayAsync(result.TorrentUrl);
                hash = await _qbit.AddTorrentAsync(torrentBytes: bytes);
            }
            else
            {
                await SafeSendAsync(chatId, "❌ Нет ссылки для скачивания.");
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add torrent");
            await SafeSendAsync(chatId, "❌ Не удалось добавить торрент. Проверьте qBittorrent.");
            return;
        }

        if (hash == null)
        {
            await SafeSendAsync(chatId, "❌ Торрент не появился в qBittorrent. Попробуйте снова.");
            return;
        }

        var files = await _qbit.GetFilesAsync(hash);
        var videoFiles = files
            .Where(f => IsVideoFile(f.Name))
            .OrderBy(f => f.Name)
            .ToList();

        var ctx = new DownloadContext
        {
            Hash = hash,
            AllFiles = files,
            VideoFiles = videoFiles,
            TorrentName = result.Title
        };
        _state.SetDownloadContext(chatId, ctx);

        if (videoFiles.Count == 0)
        {
            await _qbit.ResumeAsync(hash);
            _state.RegisterPendingNotification(hash, chatId, result.Title);
            await _bot.EditMessageText(chatId, msgId,
                $"✅ <b>{HtmlEscape(result.Title)}</b> поставлен на скачку. Уведомлю когда готово!",
                parseMode: ParseMode.Html);
            return;
        }

        if (videoFiles.Count == 1)
        {
            await _qbit.SetFilePrioritiesAsync(hash, new[] { videoFiles[0].Index }, files.Count);
            await _qbit.ResumeAsync(hash);
            _state.RegisterPendingNotification(hash, chatId, result.Title);
            await _bot.EditMessageText(chatId, msgId,
                $"✅ Скачиваю <b>{HtmlEscape(result.Title)}</b>. Уведомлю когда готово!",
                parseMode: ParseMode.Html);
            return;
        }

        var fileButtons = videoFiles.Select(f =>
            new[] { InlineKeyboardButton.WithCallbackData(
                $"📺 {Path.GetFileName(f.Name)} ({f.FormattedSize})",
                $"file:{f.Index}") }
        ).ToList();

        fileButtons.Add(new[] { InlineKeyboardButton.WithCallbackData("📦 Скачать всё", "file:ALL") });

        await _bot.EditMessageText(
            chatId, msgId,
            $"<b>{HtmlEscape(result.Title)}</b>\n\nВыберите что скачать:",
            parseMode: ParseMode.Html,
            replyMarkup: new InlineKeyboardMarkup(fileButtons));
    }

    private async Task HandleFileSelected(long chatId, int msgId, string data)
    {
        // data = "file:INDEX" or "file:ALL"
        var part = data.Split(':', 2)[1];
        var ctx = _state.GetDownloadContext(chatId);

        if (ctx == null)
        {
            await SafeSendAsync(chatId, "Сессия устарела. Выполните поиск заново.");
            return;
        }

        if (part == "ALL")
        {
            await _qbit.ResumeAsync(ctx.Hash);
        }
        else
        {
            var fileIndex = int.Parse(part);
            await _qbit.SetFilePrioritiesAsync(ctx.Hash, new[] { fileIndex }, ctx.AllFiles.Count);
            await _qbit.ResumeAsync(ctx.Hash);
        }

        var name = ctx.TorrentName ?? ctx.Hash;
        _state.RegisterPendingNotification(ctx.Hash, chatId, name);

        await _bot.EditMessageText(chatId, msgId,
            $"✅ Загрузка начата! Уведомлю когда <b>{HtmlEscape(name)}</b> будет готово.",
            parseMode: ParseMode.Html);
    }

    private async Task HandlePlaySelected(long chatId, string data)
    {
        var itemId = data.Split(':', 2)[1];
        var streamUrl = _jellyfin.GetStreamUrl(itemId);
        var posterUrl = _jellyfin.GetPosterUrl(itemId);

        try
        {
            await _bot.SendPhoto(
                chatId,
                InputFile.FromUri(posterUrl),
                caption: $"<a href=\"{streamUrl}\">▶️ Смотреть в Jellyfin</a>",
                parseMode: ParseMode.Html);
        }
        catch
        {
            await SafeSendAsync(chatId, $"▶️ <a href=\"{streamUrl}\">Смотреть в Jellyfin</a>");
        }
    }

    private async Task HandleMoreEpisodesSelected(long chatId, int msgId, string data)
    {
        var hash = data.Split(':', 2)[1];
        var files = await _qbit.GetFilesAsync(hash);
        var skipped = files.Where(f => IsVideoFile(f.Name) && f.Priority == 0).OrderBy(f => f.Name).ToList();

        if (skipped.Count == 0)
        {
            await SafeSendAsync(chatId, "Все эпизоды уже скачаны.");
            return;
        }

        var fileButtons = skipped.Select(f =>
            new[] { InlineKeyboardButton.WithCallbackData(
                $"📺 {Path.GetFileName(f.Name)} ({f.FormattedSize})",
                $"morefile:{hash}:{f.Index}") }
        ).ToList();
        fileButtons.Add([InlineKeyboardButton.WithCallbackData("📦 Скачать все оставшиеся", $"morefile:{hash}:ALL")]);

        await _bot.EditMessageText(
            chatId, msgId,
            $"Выберите эпизод для скачивания ({skipped.Count} доступно):",
            replyMarkup: new InlineKeyboardMarkup(fileButtons));
    }

    private async Task HandleMoreFileSelected(long chatId, int msgId, string data)
    {
        // data = "morefile:<hash>:<index|ALL>"
        var parts = data.Split(':', 3);
        var hash = parts[1];
        var part = parts[2];

        var files = await _qbit.GetFilesAsync(hash);

        if (part == "ALL")
        {
            var skippedIndexes = files.Where(f => IsVideoFile(f.Name) && f.Priority == 0).Select(f => f.Index).ToArray();
            await _qbit.SetFilePrioritiesAsync(hash, skippedIndexes, files.Count);
        }
        else
        {
            var fileIndex = int.Parse(part);
            await _qbit.SetFilePrioritiesAsync(hash, [fileIndex], files.Count);
        }

        await _qbit.ResumeAsync(hash);
        await _bot.EditMessageText(chatId, msgId, "✅ Загрузка начата! Используйте /downloads для отслеживания.");
    }

    // ── Pagination ────────────────────────────────────────────────────────

    private const int PageSize = 5;

    private async Task ShowResultsPage(long chatId, int msgId, List<SearchResult> results, int page)
    {
        var totalPages = (int)Math.Ceiling(results.Count / (double)PageSize);
        var pageResults = results.Skip(page * PageSize).Take(PageSize).ToList();

        var lines = new System.Text.StringBuilder();
        lines.AppendLine($"🔍 Результаты {page * PageSize + 1}–{page * PageSize + pageResults.Count} из {results.Count}:\n");
        for (int i = 0; i < pageResults.Count; i++)
        {
            var r = pageResults[i];
            var globalIdx = page * PageSize + i;
            lines.AppendLine($"<b>{i + 1}.</b> {HtmlEscape(r.Title)}");
            lines.AppendLine($"   📦 {r.FormattedSize} | 🌱 {r.Seeders} | {r.Indexer}\n");
        }

        // Number buttons for items on this page
        var numberRow = pageResults.Select((_, i) =>
            InlineKeyboardButton.WithCallbackData($"{i + 1}", $"torrent:{page * PageSize + i}")
        ).ToList();

        // Prev / Next navigation
        var navRow = new List<InlineKeyboardButton>();
        if (page > 0)
            navRow.Add(InlineKeyboardButton.WithCallbackData("← Назад", $"page:{page - 1}"));
        if (page < totalPages - 1)
            navRow.Add(InlineKeyboardButton.WithCallbackData("Вперёд →", $"page:{page + 1}"));

        var rows = new List<IEnumerable<InlineKeyboardButton>> { numberRow };
        if (navRow.Count > 0) rows.Add(navRow);

        await _bot.EditMessageText(
            chatId, msgId,
            lines.ToString().TrimEnd(),
            parseMode: ParseMode.Html,
            replyMarkup: new InlineKeyboardMarkup(rows));
    }

    private async Task HandlePageSelected(long chatId, int msgId, string data)
    {
        // data = "page:N"
        var page = int.Parse(data.Split(':')[1]);
        var results = _state.GetAllSearchResults(chatId);
        if (results == null || results.Count == 0)
        {
            await SafeSendAsync(chatId, "Сессия устарела. Выполните поиск заново.");
            return;
        }
        await ShowResultsPage(chatId, msgId, results, page);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private bool IsAllowed(long chatId)
    {
        if (_registry.IsRegistered(chatId)) return true;
        if (_config.AllowedChatIds.Contains(chatId)) return true;
        // Open access only when neither invite code nor static list is configured
        return string.IsNullOrEmpty(_config.InviteCode) && _config.AllowedChatIds.Count == 0;
    }

    private static bool IsVideoFile(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".mkv" or ".mp4" or ".avi" or ".mov" or ".wmv" or ".m4v";
    }

    private static string HtmlEscape(string text)
        => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private async Task SafeSendAsync(long chatId, string text)
    {
        try
        {
            await _bot.SendMessage(chatId, text);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send message to {ChatId}", chatId);
        }
    }
}
