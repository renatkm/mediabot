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

## Requirements

- Linux machine (Ubuntu 22.04+ recommended)
- [Docker](https://docs.docker.com/engine/install/) + [Docker Compose](https://docs.docker.com/compose/install/)
- A Telegram account
- Accounts on [RuTracker](https://rutracker.org) and/or [NNM-Club](https://nnmclub.to) (optional, for those indexers)

## Setup

### 1. Create a Telegram bot

1. Open Telegram and message [@BotFather](https://t.me/BotFather)
2. Send `/newbot`, follow the prompts, copy the **bot token**

### 2. Configure `.env`

Edit the `.env` file in the project root:

```env
TELEGRAM_BOT_TOKEN=your_token_here      # from @BotFather
INVITE_CODE=your_secret_phrase          # family members use this (full access)
GUEST_INVITE_CODE=optional_guest_code   # friends use this (search & .torrent files only)
```

Everything else is filled in during first-run steps below.

### 3. Prepare storage

By default, downloads go to `./downloads/` (next to `docker-compose.yml`).

To use an external drive, the drive must be formatted **ext4** or **exFAT** (not FAT32 — 4 GB file size limit). Mount it and update the four volume entries in `docker-compose.yml`:

```yaml
- /mnt/your-drive/Media:/downloads   # qbittorrent, jackett, bot
- /mnt/your-drive/Media:/media:ro    # jellyfin
```

To auto-mount on boot, add to `/etc/fstab`:
```
UUID=<disk-uuid>  /mnt/your-drive  ext4  defaults,nofail  0  2
```
Get the UUID with `blkid /dev/sdX1`.

### 4. Start all services

```bash
docker compose up -d
```

### 5. Configure qBittorrent

1. Get the one-time temporary password from the logs:
   ```bash
   docker logs media-qbittorrent 2>&1 | grep -i "temporary password"
   ```
2. Open **http://localhost:8080**, log in as `admin` with that password
3. Go to **Settings → Web UI**, set a permanent password, click Save
4. Update `.env`:
   ```env
   QBITTORRENT_PASSWORD=your_new_password
   ```

### 6. Configure Jackett

1. Open **http://localhost:9117**
2. Copy the **API Key** from the top-right corner, add it to `.env`:
   ```env
   JACKETT_API_KEY=paste_key_here
   ```
3. Click **Add Indexer** and add the trackers you want (RuTracker, NNM-Club, EZTV). RuTracker and NNM-Club require accounts on those sites.

### 7. Configure Jellyfin

1. Open **http://localhost:8096** and complete the setup wizard
   - When asked for a media library, add the `/media` folder
   - Language/metadata: your preference
2. After setup, go to **Dashboard → API Keys → +**, create a key named `MediaBot`, copy it to `.env`:
   ```env
   JELLYFIN_API_KEY=paste_key_here
   ```
3. Set the public URL so stream links work on other devices on the network:
   ```env
   JELLYFIN_PUBLIC_URL=http://192.168.1.X:8096
   ```
   Replace `192.168.1.X` with your server's local IP (`ip -4 addr show | grep -v docker`).

### 8. Apply configuration and restart the bot

```bash
docker compose up -d bot
```

### 9. Register in the bot

Open the bot in Telegram and send:
```
/start your_secret_phrase
```

You'll see the help menu — setup is complete. Share the same `/start your_secret_phrase` message with family members so they can register themselves.

---

## Usage

| Command | Description |
|---|---|
| `/search <title>` | Search torrents and download |
| `/find <title>` | Search your Jellyfin library |
| `/library` | Browse recently added |
| `/downloads` | Active and completed downloads |
| `/ebooks` | Download and send ebook files |
| `/help` | Show this list |

Send `/search` or `/find` without a title and the bot will prompt you to type it.

### Download Workflow

1. **Search**: Send `/search <title>` to find torrents
2. **Select**: Choose from search results (numbered buttons)
3. **Categorize**: Bot asks "What type?" — select from available categories
4. **Pick files**: For multi-file content (seasons), select which files to download
5. **Auto-organize**: Files go to `/downloads/<category>/<torrent-name>/`
6. **Stream**: Once ready, bot sends a Jellyfin link

### Categorization

Downloads are automatically organized by category. The bot scans your `/downloads` folder at startup and detects available categories:

```
/downloads/
  ├── movies/           → 🎬 Movies
  ├── series/           → 📺 TV Series
  ├── music/            → 🎵 Music
  ├── ebooks/           → 📚 Ebooks
  └── audiobooks/       → 📁 Custom categories (detected automatically)
```

**How it works:**

- When you select a torrent, the bot shows **only the category folders that exist** on your disk
- Each download creates its own subfolder: `/downloads/series/Breaking Bad S01/`
- For series, all episodes go to the same folder — future seasons automatically use separate folders (S02, S03, etc.)
- You can create custom categories by making new folders in `/downloads/` — the bot will detect them on next startup

**Example:**

- Download S01 of Breaking Bad → creates `/downloads/series/Breaking Bad S01/`
- Download S02 → creates `/downloads/series/Breaking Bad S02/`
- Both stay organized and Jellyfin scans them automatically

### User Roles

The bot supports two access levels via invite codes:

**Full Access** (`INVITE_CODE`):
- `/search` — Download torrents
- `/find`, `/library` — Stream from Jellyfin
- `/downloads` — Manage active downloads
- `/ebooks` — Send ebook files
- Full episode/file selection

**Guest Access** (`GUEST_INVITE_CODE`):
- `/search` — Search torrents only
- 📎 Button — Download `.torrent` files directly
- **Blocked**: Server downloads, library streaming, ebook access

Share `INVITE_CODE` with trusted family; use `GUEST_INVITE_CODE` for friends who want `.torrent` files but not server storage.

**To upgrade a guest to full access:**

Edit `config/bot/users.json` and change their role from `"guest"` to `"full"`, then restart the bot.

---

## Accessing Jellyfin from other devices

Any device on the same Wi-Fi can open Jellyfin at `http://<server-ip>:8096`. The Telegram bot also sends direct stream links that open in the Jellyfin app or browser.

To enable DLNA streaming (cast to Smart TVs, receivers), open Jellyfin Dashboard → **Playback** → **DLNA** → enable "Enable DLNA Server".

---

## User Guides & Documentation

### Jellyfin (Media Server)

- **[Official Jellyfin Documentation](https://jellyfin.org/docs/)** — Complete guide to features, administration, and client apps
- **[Adding Libraries](https://jellyfin.org/docs/general/administration/libraries/)** — How to add media folders (Movies, TV, Music, Books)
- **[Playback Settings](https://jellyfin.org/docs/general/administration/playback/)** — Transcoding, streaming quality, subtitles
- **[Client Apps](https://jellyfin.org/docs/general/clients/)** — Available on Android, iOS, Roku, Smart TVs, and browsers
- **[DLNA Guide](https://jellyfin.org/docs/general/administration/dlna/)** — Cast media to devices on your network

### qBittorrent (Torrent Client)

- **[Official qBittorrent Documentation](https://github.com/qbittorrent/qBittorrent/wiki)** — Setup and usage guide
- **[Web UI Guide](https://github.com/qbittorrent/qBittorrent/wiki/Web-UI-Translation-Guide)** — Web interface features and settings
- **[Settings Reference](https://github.com/qbittorrent/qBittorrent/wiki/Settings)** — Detailed settings explanation
- **[Search Plugins](https://github.com/qbittorrent/search-plugins)** — For searching via Jackett

### Jackett (Torrent Indexer)

- **[Jackett GitHub](https://github.com/Jackett/Jackett)** — Main repository and documentation
- **[Adding Indexers](https://github.com/Jackett/Jackett#indexers)** — How to add torrent sites to Jackett

### Telegram Bot Setup

- **[@BotFather](https://t.me/BotFather)** — Create and manage your bot token here
- **[@userinfobot](https://t.me/userinfobot)** — Get your Telegram chat ID for configuration

---

## Project structure

```
├── bot/                  # .NET 8 bot source
│   ├── Configuration/    # Config models
│   ├── Models/           # Domain models (SearchResult, TorrentFile, etc.)
│   └── Services/
│       ├── Indexers/     # Jackett + EZTV indexer adapters
│       ├── TelegramBotService.cs      # Main bot message/command handler
│       ├── QBittorrentService.cs      # Torrent client API wrapper
│       ├── JellyfinService.cs         # Media server integration
│       ├── DownloadMonitorService.cs  # Track torrent completion & send notifications
│       ├── UserRegistryService.cs     # User registration & roles (full/guest)
│       ├── BotStateService.cs         # Session state (search results, pending queries)
│       ├── CategoryService.cs         # Auto-detect download categories
│       └── TorrentAggregator.cs       # Search across multiple indexers
├── config/               # Persistent config for each service (git-ignored)
│   ├── bot/users.json                 # Registered user IDs and roles
│   ├── jackett/                       # Jackett config
│   ├── qbittorrent/                   # qBittorrent state
│   └── jellyfin/                      # Jellyfin database & metadata
├── docker-compose.yml    # Service definitions
├── .env                  # Environment variables (fill before running)
└── .gitignore           # Excludes .env, config/, downloads/
```
