# NhBackup

Self-hosted tool for backing up your nhentai favorites to local storage with a web interface to browse them.

## What it does

- Periodically syncs your nhentai favorites via the [nhentai API v2](https://nhentai.net/api/v2/docs)
- Downloads all gallery images to disk
- Stores metadata (titles, tags, page counts) in a local database
- Serves everything through a web interface with authentication

## Userscript

Includes a browser userscript (Tampermonkey/Violentmonkey) that makes browsing already-synced galleries faster — while you're on nhentai.net, image requests are transparently served from your local instance instead of the CDN. Falls back to the original CDN for galleries that haven't been synced yet.

Install it from the `/install-script` page of your running instance.

## Installation

### Docker (recommended)

Create a `.env` file with your configuration:

```env
ApiKey=your_nhentai_api_key
DataFolder=/data
SyncIntevralHours=6
```

Then start with Docker Compose:

```yaml
services:
  nhbackup:
    image: sharpsalat/nhbackup:latest
    container_name: nhbackup
    restart: unless-stopped
    ports:
      - "8080:8080"
    volumes:
      - nhbackup-data:/data
    env_file:
      - .env
    healthcheck:
      test: ["CMD", "curl", "-f", "http://localhost:8080/healthz"]
      interval: 30s
      timeout: 10s
      retries: 3
      start_period: 15s
volumes:
  nhbackup-data:
```

```bash
docker compose up -d
```

The app will be available at `http://localhost:8080`.

### Configuration reference

| Key | Description |
|---|---|
| `ApiKey` | nhentai API key |
| `DataFolder` | Path for storing downloads and the database (default `/data` in Docker) |
| `SyncIntevralHours` | How often to sync (hours) |

## Stack

- ASP.NET Core, Entity Framework Core (SQLite), Bootstrap