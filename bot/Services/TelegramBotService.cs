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
    private readonly CategoryService _categories;
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
        CategoryService categories,
        ILogger<TelegramBotService> logger)
    {
        _config = config.Value;
        _aggregator = aggregator;
        _qbit = qbit;
        _jellyfin = jellyfin;
        _state = state;
        _monitor = monitor;
        _registry = registry;
        _categories = categories;
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
            new BotCommand { Command = "ebooks",    Description = "Электронные книги" },
            new BotCommand { Command = "help",      Description = "Помощь" },
        ], cancellationToken: ct);

        // Scan available download categories at startup
        _categories.ScanCategories();
        _logger.LogInformation("Scanned categories: {Categories}", string.Join(", ", _categories.GetCategories().Keys));

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
            // If user sends a command while file selection is pending, clean up the abandoned torrent
            if (text.StartsWith('/') && !text.StartsWith("/start"))
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
                if (await RequireFullAsync(chatId))
                {
                    _state.SetPendingInput(chatId, "find");
                    await _bot.SendMessage(chatId, "🎬 Введите название для поиска в библиотеке:");
                }
            }
            else if (text.StartsWith("/find ", StringComparison.OrdinalIgnoreCase))
            {
                if (await RequireFullAsync(chatId))
                    await HandleFindCommand(chatId, text[6..].Trim());
            }
            else if (text == "/library")
            {
                if (await RequireFullAsync(chatId))
                    await HandleLibraryCommand(chatId);
            }
            else if (text == "/downloads")
            {
                if (await RequireFullAsync(chatId))
                    await HandleDownloadsCommand(chatId);
            }
            else if (text == "/ebooks")
            {
                if (await RequireFullAsync(chatId))
                    await HandleEbooksCommand(chatId);
            }
            else
            {
                var pending = _state.ConsumePendingInput(chatId);
                if (pending == "search")
                    await HandleSearchCommand(chatId, text);
                else if (pending == "find" && await RequireFullAsync(chatId))
                    await HandleFindCommand(chatId, text);
                else if (pending != "find")
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

        // Search navigation and grabbing the .torrent file are open to everyone;
        // anything that downloads to or streams from the server requires full access.
        if (data.StartsWith("src:"))
            await HandleSourceSelected(chatId, callback.Message.MessageId, data);
        else if (data.StartsWith("page:"))
            await HandlePageSelected(chatId, callback.Message.MessageId, data);
        else if (data.StartsWith("getfile:"))
            await HandleGetTorrentFile(chatId, data);
        else if (data.StartsWith("torrent:"))
        {
            if (await RequireFullAsync(chatId))
                await HandleTorrentSelected(chatId, callback.Message.MessageId, data);
        }
        else if (data.StartsWith("category:"))
        {
            if (await RequireFullAsync(chatId))
                await HandleCategorySelected(chatId, callback.Message.MessageId, data);
        }
        else if (data.StartsWith("file:"))
        {
            if (await RequireFullAsync(chatId))
                await HandleFileSelected(chatId, callback.Message.MessageId, data);
        }
        else if (data.StartsWith("filepage:"))
        {
            if (await RequireFullAsync(chatId))
                await HandleFilePageSelected(chatId, callback.Message.MessageId, data);
        }
        else if (data.StartsWith("play:"))
        {
            if (await RequireFullAsync(chatId))
                await HandlePlaySelected(chatId, data);
        }
        else if (data.StartsWith("moreeps:"))
        {
            if (await RequireFullAsync(chatId))
                await HandleMoreEpisodesSelected(chatId, callback.Message.MessageId, data);
        }
        else if (data.StartsWith("morefile:"))
        {
            if (await RequireFullAsync(chatId))
                await HandleMoreFileSelected(chatId, callback.Message.MessageId, data);
        }
        else if (data.StartsWith("ebook:"))
        {
            if (await RequireFullAsync(chatId))
                await HandleEbookSelected(chatId, data);
        }
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

        if (!string.IsNullOrEmpty(_config.InviteCode) && code == _config.InviteCode)
        {
            _registry.Register(chatId, UserRegistryService.RoleFull);
            _logger.LogInformation("New full user registered: {ChatId}", chatId);
            await HandleHelp(chatId);
        }
        else if (!string.IsNullOrEmpty(_config.GuestInviteCode) && code == _config.GuestInviteCode)
        {
            _registry.Register(chatId, UserRegistryService.RoleGuest);
            _logger.LogInformation("New guest user registered: {ChatId}", chatId);
            await HandleHelp(chatId);
        }
        else
        {
            await _bot.SendMessage(chatId,
                "👋 Привет! Для доступа к боту введите:\n/start <код приглашения>");
        }
    }

    private async Task HandleHelp(long chatId)
    {
        string text;
        if (IsFullUser(chatId))
        {
            text = """
                🎬 <b>MediaBot</b> — ваш личный кинотеатр

                Команды:
                /search &lt;запрос&gt; — поиск в торрентах
                /find &lt;запрос&gt; — поиск в вашей библиотеке
                /library — последние добавленные
                /downloads — текущие загрузки
                /ebooks — электронные книги
                """;
        }
        else
        {
            text = """
                🎬 <b>MediaBot</b> — поиск торрентов

                Команды:
                /search &lt;запрос&gt; — поиск торрентов

                После поиска нажмите 📎, чтобы получить .torrent файл.
                """;
        }
        await _bot.SendMessage(chatId, text, parseMode: ParseMode.Html);
    }

    private async Task HandleSearchCommand(long chatId, string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            await SafeSendAsync(chatId, "Укажите запрос: /search Начало");
            return;
        }

        // Store the query in state so it stays out of callback_data (Telegram caps that at 64 bytes)
        _state.SetPendingQuery(chatId, query);

        var indexerNames = _aggregator.IndexerNames;
        var rows = new List<InlineKeyboardButton[]>
        {
            new[] { InlineKeyboardButton.WithCallbackData("🌍 Все источники", "src:ALL") }
        };

        var indexerRow = new List<InlineKeyboardButton>();
        foreach (var name in indexerNames)
        {
            indexerRow.Add(InlineKeyboardButton.WithCallbackData(name, $"src:{name}"));
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

    private async Task HandleEbooksCommand(long chatId)
    {
        var downloadDir = "/downloads";
        var ebookExtensions = new[] { ".pdf", ".epub", ".mobi", ".azw", ".azw3" };

        try
        {
            var files = Directory.GetFiles(downloadDir, "*", SearchOption.AllDirectories)
                .Where(f => ebookExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .OrderBy(f => Path.GetFileName(f))
                .ToList();

            if (files.Count == 0)
            {
                await SafeSendAsync(chatId, "📚 Нет файлов электронных книг в библиотеке.");
                return;
            }

            var buttons = files.Select((file, idx) =>
                new[] { InlineKeyboardButton.WithCallbackData(
                    $"📄 {Path.GetFileName(file)}",
                    $"ebook:{idx}") }
            ).ToList();

            await _bot.SendMessage(
                chatId,
                $"📚 <b>Электронные книги ({files.Count})</b>\n\nВыберите файл для скачивания:",
                parseMode: ParseMode.Html,
                replyMarkup: new InlineKeyboardMarkup(buttons));

            _state.SetEbookFiles(chatId, files);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing ebooks");
            await SafeSendAsync(chatId, "❌ Ошибка при чтении библиотеки.");
        }
    }

    private async Task HandleEbookSelected(long chatId, string data)
    {
        var idx = int.Parse(data.Split(':')[1]);
        var files = _state.GetEbookFiles(chatId);

        if (files == null || idx >= files.Count)
        {
            await SafeSendAsync(chatId, "Сессия устарела. Используйте /ebooks снова.");
            return;
        }

        var filePath = files[idx];
        var fileName = Path.GetFileName(filePath);

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            await _bot.SendDocument(
                chatId,
                InputFile.FromStream(stream, fileName),
                caption: $"📚 <b>{HtmlEscape(fileName)}</b>",
                parseMode: ParseMode.Html);

            _logger.LogInformation("Ebook sent: chatId={ChatId} file={FileName}", chatId, fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send ebook {File} to {ChatId}", filePath, chatId);
            await SafeSendAsync(chatId, "❌ Не удалось отправить файл.");
        }
    }

    // ── Callback handlers ─────────────────────────────────────────────────

    private async Task HandleSourceSelected(long chatId, int msgId, string data)
    {
        // data = "src:SOURCENAME"; the query is held in state
        var parts = data.Split(':', 2);
        if (parts.Length < 2) return;

        var source = parts[1];
        var query = _state.GetPendingQuery(chatId);

        if (string.IsNullOrEmpty(query))
        {
            await _bot.EditMessageText(chatId, msgId,
                "Сессия устарела. Выполните поиск заново через /search.",
                parseMode: ParseMode.Html);
            return;
        }

        _logger.LogInformation("Search request: chatId={ChatId} role={Role} source={Source} query=\"{Query}\"",
            chatId, _registry.GetRole(chatId) ?? "admin", source, query);

        await _bot.EditMessageText(chatId, msgId,
            $"⏳ Ищу <b>{HtmlEscape(query)}</b> в {HtmlEscape(source)}...",
            parseMode: ParseMode.Html);

        var results = await _aggregator.SearchAsync(
            query,
            source == "ALL" ? null : source);

        _logger.LogInformation("Search result: chatId={ChatId} query=\"{Query}\" found={Count}",
            chatId, query, results.Count);

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

        // Ask for category before adding torrent - use dynamic categories from disk
        var availableCategories = _categories.GetCategories();
        var categoryButtons = availableCategories.Select(kvp =>
            new[] { InlineKeyboardButton.WithCallbackData(kvp.Value, $"category:{kvp.Key}:{idx}") }
        ).ToArray();

        await _bot.EditMessageText(
            chatId, msgId,
            $"<b>{HtmlEscape(result.Title)}</b>\n\nВыберите категорию:",
            parseMode: ParseMode.Html,
            replyMarkup: new InlineKeyboardMarkup(categoryButtons));
    }

    private async Task HandleCategorySelected(long chatId, int msgId, string data)
    {
        // data = "category:TYPE:INDEX"
        var parts = data.Split(':', 3);
        var category = parts[1];
        var idx = int.Parse(parts[2]);

        var result = _state.GetSearchResult(chatId, idx);
        if (result == null)
        {
            await SafeSendAsync(chatId, "Сессия устарела. Выполните поиск заново.");
            return;
        }

        // If user selects a different torrent while one is pending, clean up the old one
        var abandoned = _state.ConsumeDownloadContext(chatId);
        if (abandoned != null)
            await _qbit.DeleteAsync(abandoned.Hash, deleteFiles: false);

        await _bot.EditMessageText(chatId, msgId,
            $"⏳ Добавляю <b>{HtmlEscape(result.Title)}</b> и получаю список файлов...",
            parseMode: ParseMode.Html);

        string? hash;
        try
        {
            if (result.MagnetLink == null && result.TorrentUrl == null)
            {
                await SafeSendAsync(chatId, "❌ Нет ссылки для скачивания.");
                return;
            }

            byte[]? bytes = null;
            if (result.MagnetLink == null)
            {
                using var httpClient = new HttpClient();
                bytes = await httpClient.GetByteArrayAsync(result.TorrentUrl);
            }

            // Already in qBittorrent: the normal flow would time out, so offer its skipped episodes
            var infoHash = QBittorrentService.GetInfoHash(result.MagnetLink, bytes);
            if (infoHash != null && (await _qbit.GetTorrentsAsync()).Any(t => t.Hash.Equals(infoHash, StringComparison.OrdinalIgnoreCase)))
            {
                await HandleMoreEpisodesSelected(chatId, msgId, $"moreeps:{infoHash}");
                return;
            }

            hash = await _qbit.AddTorrentAsync(result.MagnetLink, bytes);
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
            TorrentName = result.Title,
            Category = category
        };
        _state.SetDownloadContext(chatId, ctx);

        if (videoFiles.Count == 0)
        {
            await StartDownload(chatId, msgId, ctx, null);
            return;
        }

        if (videoFiles.Count == 1)
        {
            await StartDownload(chatId, msgId, ctx, new[] { videoFiles[0].Index });
            return;
        }

        await ShowFilesPage(chatId, msgId, result.Title, videoFiles, 0);
    }

    private async Task StartDownload(long chatId, int msgId, DownloadContext ctx, int[]? enabledIndexes)
    {
        if (enabledIndexes != null && enabledIndexes.Length > 0)
            await _qbit.SetFilePrioritiesAsync(ctx.Hash, enabledIndexes, ctx.AllFiles.Count);

        // Move torrent to category folder
        var savePath = GetCategoryPath(ctx.Category, ctx.TorrentName);
        await _qbit.MoveTorrentAsync(ctx.Hash, savePath);

        await _qbit.ResumeAsync(ctx.Hash);
        _state.RegisterPendingNotification(ctx.Hash, chatId, ctx.TorrentName!);
        _state.ConsumeDownloadContext(chatId);

        await _bot.EditMessageText(chatId, msgId,
            $"✅ Скачиваю <b>{HtmlEscape(ctx.TorrentName ?? "торрент")}</b> в {HtmlEscape(ctx.Category ?? "other")}. Уведомлю когда готово!",
            parseMode: ParseMode.Html);
    }

    private static string GetCategoryPath(string? category, string? torrentName)
    {
        var basePath = "/downloads";
        var categoryFolder = category ?? "other";
        var sanitized = SanitizeFileName(torrentName ?? "torrent");
        return $"{basePath}/{categoryFolder}/{sanitized}";
    }

    private async Task HandleGetTorrentFile(long chatId, string data)
    {
        // data = "getfile:INDEX"
        var idx = int.Parse(data.Split(':')[1]);
        var result = _state.GetSearchResult(chatId, idx);

        if (result == null)
        {
            await SafeSendAsync(chatId, "Сессия устарела. Выполните поиск заново.");
            return;
        }

        if (result.TorrentUrl != null)
        {
            try
            {
                using var httpClient = new HttpClient();
                var bytes = await httpClient.GetByteArrayAsync(result.TorrentUrl);

                // Some indexers return a magnet redirect instead of a .torrent payload
                if (bytes.Length < 50 || !LooksLikeTorrentFile(bytes))
                {
                    if (result.MagnetLink != null)
                    {
                        await SendMagnetLink(chatId, result);
                        return;
                    }
                    await SafeSendAsync(chatId, "❌ Не удалось получить .torrent файл.");
                    return;
                }

                var fileName = SanitizeFileName(result.Title) + ".torrent";
                using var stream = new MemoryStream(bytes);
                await _bot.SendDocument(
                    chatId,
                    InputFile.FromStream(stream, fileName),
                    caption: $"📎 <b>{HtmlEscape(result.Title)}</b>",
                    parseMode: ParseMode.Html);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch torrent file");
                if (result.MagnetLink != null)
                    await SendMagnetLink(chatId, result);
                else
                    await SafeSendAsync(chatId, "❌ Не удалось получить .torrent файл.");
            }
        }
        else if (result.MagnetLink != null)
        {
            await SendMagnetLink(chatId, result);
        }
        else
        {
            await SafeSendAsync(chatId, "❌ Нет ссылки для скачивания.");
        }
    }

    private async Task SendMagnetLink(long chatId, SearchResult result)
    {
        await _bot.SendMessage(
            chatId,
            $"🧲 <b>{HtmlEscape(result.Title)}</b>\n\n<code>{HtmlEscape(result.MagnetLink!)}</code>",
            parseMode: ParseMode.Html);
    }

    private static bool LooksLikeTorrentFile(byte[] bytes)
        => bytes.Length > 0 && bytes[0] == (byte)'d';  // bencoded dictionaries start with 'd'

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return clean.Length > 100 ? clean[..100] : clean;
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

        int[]? enabledIndexes = null;
        if (part != "ALL")
        {
            // The button carries the position in VideoFiles, qBittorrent needs the file's own index
            enabledIndexes = new[] { ctx.VideoFiles[int.Parse(part)].Index };
        }

        await StartDownload(chatId, msgId, ctx, enabledIndexes);
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
        // SetFilePrioritiesAsync skips everything not listed, so keep the files already enabled
        var enabled = files.Where(f => f.Priority > 0).Select(f => f.Index);

        if (part == "ALL")
        {
            var skippedIndexes = files.Where(f => IsVideoFile(f.Name) && f.Priority == 0).Select(f => f.Index);
            await _qbit.SetFilePrioritiesAsync(hash, enabled.Concat(skippedIndexes).ToArray(), files.Count);
        }
        else
        {
            var fileIndex = int.Parse(part);
            await _qbit.SetFilePrioritiesAsync(hash, enabled.Append(fileIndex).ToArray(), files.Count);
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
        lines.AppendLine("⬇️ — скачать через qBittorrent · 📎 — получить .torrent файл");

        // Number buttons add to qBittorrent; 📎 buttons send the .torrent file to Telegram
        var numberRow = pageResults.Select((_, i) =>
            InlineKeyboardButton.WithCallbackData($"⬇️ {i + 1}", $"torrent:{page * PageSize + i}")
        ).ToList();

        var fileRow = pageResults.Select((_, i) =>
            InlineKeyboardButton.WithCallbackData($"📎 {i + 1}", $"getfile:{page * PageSize + i}")
        ).ToList();

        // Prev / Next navigation
        var navRow = new List<InlineKeyboardButton>();
        if (page > 0)
            navRow.Add(InlineKeyboardButton.WithCallbackData("← Назад", $"page:{page - 1}"));
        if (page < totalPages - 1)
            navRow.Add(InlineKeyboardButton.WithCallbackData("Вперёд →", $"page:{page + 1}"));

        var rows = new List<IEnumerable<InlineKeyboardButton>> { numberRow, fileRow };
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

    private async Task ShowFilesPage(long chatId, int msgId, string torrentTitle, List<TorrentFile> videoFiles, int page)
    {
        var totalPages = (int)Math.Ceiling(videoFiles.Count / (double)PageSize);
        var pageFiles = videoFiles.Skip(page * PageSize).Take(PageSize).ToList();

        var lines = new System.Text.StringBuilder();
        lines.AppendLine($"<b>{HtmlEscape(torrentTitle)}</b>\n");
        if (totalPages > 1)
            lines.AppendLine($"📄 Файлы {page * PageSize + 1}–{page * PageSize + pageFiles.Count} из {videoFiles.Count}:\n");
        else
            lines.AppendLine("📄 Выберите файлы:\n");

        for (int i = 0; i < pageFiles.Count; i++)
        {
            var f = pageFiles[i];
            var fileName = Path.GetFileName(f.Name);
            lines.AppendLine($"<b>{i + 1}.</b> {HtmlEscape(fileName)} ({f.FormattedSize})");
        }

        var fileButtons = pageFiles.Select((_, i) =>
            InlineKeyboardButton.WithCallbackData($"📺 {i + 1}", $"file:{page * PageSize + i}")
        ).ToList();

        var allButton = new List<InlineKeyboardButton> { InlineKeyboardButton.WithCallbackData("📦 Скачать всё", "file:ALL") };

        var navRow = new List<InlineKeyboardButton>();
        if (page > 0)
            navRow.Add(InlineKeyboardButton.WithCallbackData("← Назад", $"filepage:{page - 1}"));
        if (page < totalPages - 1)
            navRow.Add(InlineKeyboardButton.WithCallbackData("Вперёд →", $"filepage:{page + 1}"));

        var rows = new List<IEnumerable<InlineKeyboardButton>> { fileButtons, allButton };
        if (navRow.Count > 0)
            rows.Add(navRow);

        await _bot.EditMessageText(
            chatId, msgId,
            lines.ToString().TrimEnd(),
            parseMode: ParseMode.Html,
            replyMarkup: new InlineKeyboardMarkup(rows));
    }

    private async Task HandleFilePageSelected(long chatId, int msgId, string data)
    {
        // data = "filepage:N"
        var page = int.Parse(data.Split(':')[1]);
        var ctx = _state.GetDownloadContext(chatId);
        if (ctx?.VideoFiles == null || ctx.VideoFiles.Count == 0)
        {
            await SafeSendAsync(chatId, "Сессия устарела. Выполните поиск заново.");
            return;
        }
        await ShowFilesPage(chatId, msgId, ctx.TorrentName!, ctx.VideoFiles, page);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private bool IsAllowed(long chatId)
    {
        if (_registry.IsRegistered(chatId)) return true;
        if (_config.AllowedChatIds.Contains(chatId)) return true;
        return IsOpenAccess();
    }

    // Full users can download to the server; guests can only search and grab .torrent files
    private bool IsFullUser(long chatId)
    {
        if (_config.AllowedChatIds.Contains(chatId)) return true;
        if (_registry.IsFull(chatId)) return true;
        return IsOpenAccess();
    }

    // No access control configured at all → everyone is treated as a full user
    private bool IsOpenAccess()
        => string.IsNullOrEmpty(_config.InviteCode)
        && string.IsNullOrEmpty(_config.GuestInviteCode)
        && _config.AllowedChatIds.Count == 0;

    private async Task<bool> RequireFullAsync(long chatId)
    {
        if (IsFullUser(chatId)) return true;
        await SafeSendAsync(chatId, "⛔ Эта функция доступна только для полного доступа.");
        return false;
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
