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
TELEGRAM_BOT_TOKEN=your_token_here   # from @BotFather
INVITE_CODE=your_secret_phrase       # family members use this to register
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
| `/help` | Show this list |

Send `/search` or `/find` without a title and the bot will prompt you to type it.

After a download completes, the bot sends a direct **Jellyfin stream link**. Use `/downloads` to watch already-downloaded content or to queue additional episodes from a series.

---

## Accessing Jellyfin from other devices

Any device on the same Wi-Fi can open Jellyfin at `http://<server-ip>:8096`. The Telegram bot also sends direct stream links that open in the Jellyfin app or browser.

---

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
│       ├── UserRegistryService.cs
│       └── TorrentAggregator.cs
├── config/               # Persistent config for each service (git-ignored)
├── docker-compose.yml
└── .env                  # Fill this in before starting
```
