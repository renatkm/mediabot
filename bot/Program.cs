using MediaBot.Configuration;
using MediaBot.Services;
using MediaBot.Services.Indexers;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<BotConfig>(builder.Configuration.GetSection("Bot"));
builder.Services.Configure<QBittorrentConfig>(builder.Configuration.GetSection("QBittorrent"));
builder.Services.Configure<JellyfinConfig>(builder.Configuration.GetSection("Jellyfin"));
builder.Services.Configure<JackettConfig>(builder.Configuration.GetSection("Jackett"));
builder.Services.Configure<SearchConfig>(builder.Configuration.GetSection("Search"));

// All indexers go through Jackett (reliable keyword search)
builder.Services.AddHttpClient("jackett").ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(60));

builder.Services.AddSingleton<ITorrentIndexer>(sp =>
    new JackettIndexer(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("jackett"),
        sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<JackettConfig>>(),
        sp.GetRequiredService<ILogger<JackettIndexer>>(),
        indexerId: "rutracker",
        displayName: "RuTracker"));

builder.Services.AddSingleton<ITorrentIndexer>(sp =>
    new JackettIndexer(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("jackett"),
        sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<JackettConfig>>(),
        sp.GetRequiredService<ILogger<JackettIndexer>>(),
        indexerId: "noname-clubl",
        displayName: "NNM-Club"));

builder.Services.AddSingleton<ITorrentIndexer>(sp =>
    new JackettIndexer(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient("jackett"),
        sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<JackettConfig>>(),
        sp.GetRequiredService<ILogger<JackettIndexer>>(),
        indexerId: "eztv",
        displayName: "EZTV"));

builder.Services.AddSingleton<TorrentAggregator>();

// Core services
builder.Services.AddHttpClient<QBittorrentService>();
builder.Services.AddHttpClient<JellyfinService>();
builder.Services.AddSingleton<BotStateService>();
builder.Services.AddSingleton<UserRegistryService>();
builder.Services.AddSingleton<CategoryService>();

// Background services
builder.Services.AddSingleton<DownloadMonitorService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DownloadMonitorService>());
builder.Services.AddHostedService<TelegramBotService>();

var host = builder.Build();
await host.RunAsync();
