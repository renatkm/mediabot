# MediaBot

A Telegram bot for family use that searches torrents, downloads them via qBittorrent, and sends back a Jellyfin stream link — all from a single message.

## Stack

| Service | Purpose | Port |
|---|---|---|
| **MediaBot** (.NET 8) | Telegram bot | — |
| **Jackett** | Torrent indexer (RuTracker, NNM-Club, EZTV) | 9117 |
| **FlareSolverr** | Cloudflare bypass for Jackett | 8191 |
| **qBittorrent** | Torrent client | 8080 |
| **Jellyfin** | Media server | 8096 |

## Setup

### 1. Copy and fill in `.env`

```bash
cp .env .env.local  # optional, or edit .env directly
```

Fill in the required values:

```env
TELEGRAM_BOT_TOKEN=       # from @BotFather
ALLOWED_CHAT_IDS=         # your Telegram chat ID (from @userinfobot)
QBITTORRENT_PASSWORD=     # set after first run (see below)
JACKETT_API_KEY=          # from Jackett UI top-right corner
JELLYFIN_API_KEY=         # from Jellyfin Dashboard → API Keys
JELLYFIN_PUBLIC_URL=      # e.g. http://192.168.1.100:8096
TZ=Europe/Moscow
```

### 2. Start services

```bash
docker compose up -d
```

### 3. First-run configuration

**qBittorrent** — get the temporary password from logs, then change it:
```bash
docker logs media-qbittorrent
# open http://localhost:8080, go to Settings → Web UI, set a new password
# update QBITTORRENT_PASSWORD in .env, then: docker compose up -d bot
```

**Jackett** — open http://localhost:9117, add indexers (RuTracker, NNM-Club — you need accounts there), copy the API key to `.env`.

**Jellyfin** — complete setup at http://localhost:8096, then Dashboard → API Keys → create a key and copy it to `.env`.

### 4. Restart bot after filling `.env`

```bash
docker compose up -d bot
```

## Usage

Send any movie or show title to the bot in Telegram. It searches all configured indexers, lets you pick a result, downloads it, and replies with a Jellyfin stream link once ready.

## Project structure

```
├── bot/                  # .NET 8 bot source
│   ├── Configuration/    # Config models
│   ├── Models/           # Domain models
│   └── Services/
│       ├── Indexers/     # Jackett + EZTV indexer adapters
│       ├── TelegramBotService.cs
│       ├── QBittorrentService.cs
│       ├── JellyfinService.cs
│       ├── DownloadMonitorService.cs
│       └── TorrentAggregator.cs
├── config/               # Persistent config for each service (git-ignored)
├── downloads/            # Shared volume for downloads (git-ignored)
├── docker-compose.yml
└── .env                  # Fill this in before starting
```
