# CS2DemoBuddy

Automatic GOTV demo recording and uploading for CS2 community servers. Records every match, uploads the `.dem` file to your own web server, and lets players download them from a clean browser UI.

This is a two-part system:
- **CS2DemoBuddy Plugin** — A [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) plugin that runs on your CS2 game server.
- **DemoServer** — A lightweight Node.js web app (runs in Docker) that receives, stores, and serves demo files.

---

## How It Works (Step by Step)

Here's exactly what happens from the moment a player connects to the moment they download a demo.

### 1. Plugin Loads

When CS2DemoBuddy loads on the game server, it does a few things immediately:

- Sets up GOTV with the correct settings. The important ones:
  - `tv_enable 1` — Turns on GOTV.
  - `tv_delay 0` — **This is the critical one.** Without this, `tv_record` silently refuses to create any files.
  - `tv_transmitall 1` — Captures all player data, not just what GOTV normally broadcasts.
  - `tv_relayvoice 1` — Records voice chat into the demo.
- Creates a `FileSystemWatcher` on the `/csgo/` directory to detect `.dem` files the instant they're created.
- Starts a 15-second repeating timer that checks if any human players are on the server.
- Registers event hooks for round starts, match ends, and map changes.

### 2. A Player Joins

When a human player is detected (either by the 15-second timer or by a round starting), the plugin kicks off a recording:

- Generates a filename based on the current map and a UTC timestamp: `de_dust2_031026_143022`
- Tracks the filename in a local `demo_history.xml` file (so it knows which server this demo belongs to)
- Sends `tv_stoprecord` first to clear any stuck state
- Re-applies the GOTV settings (they can get reset by map changes)
- Clears the file watcher list for this session
- Sends `tv_record de_dust2_031026_143022`
- The `FileSystemWatcher` immediately picks up the new `.dem` file being created

Recording continues through warmup, the match, overtime — everything. Voice chat and text chat are all captured.

### 3. The Match Ends

A recording stops when any of these happen:
- The match ends (`EventCsWinPanelMatch`)
- The map changes (`OnMapEnd`)

When recording stops:

- The plugin sends `tv_stoprecord`
- Takes a snapshot of all files the `FileSystemWatcher` detected during this recording session
- Hands off to a background thread so the game server isn't blocked

### 4. Upload

On the background thread:

- Waits 5 seconds for the engine to finish writing the demo file
- Checks the watcher snapshot for the file path
- If the watcher didn't catch it (rare), falls back to scanning the known directories for the specific filename
- If still not found, waits another 15 seconds and tries one more time
- Once found, uploads the `.dem` file to the DemoServer via `POST /upload` with:
  - The file as multipart form data
  - The server name from config
  - An API key in the `x-api-key` header
- On success: deletes the local `.dem` file and removes it from the XML tracker
- On failure: leaves the file on disk for the garbage collector to retry later

### 5. Garbage Collection

Every hour, the plugin scans for leftover `.dem` files:

- If a file is tracked in `demo_history.xml` → retries the upload
- If a file is untracked (old junk from a previous session) → deletes it
- Never touches the currently-recording file

### 6. DemoServer Receives the File

The DemoServer is a Node.js/Express app. When it receives an upload:

- Validates the API key
- Creates a directory structure: `/<storage>/.ServerName/2026-03-10/`
- Saves the `.dem` file with its original name
- Also accepts log messages from the plugin via `POST /upload-log`, stored in `/<storage>/.logs/ServerName/2026-03-10.log`

### 7. Players Browse and Download

The DemoServer serves a web UI at port 8080. Players can:

- **Browse by server** — See all servers that have uploaded demos
- **Browse by date** — Pick a date to see that day's recordings
- **Download** — Click a button to download any `.dem` file directly
- **View logs** — Switch to the "Live Logs" tab to read plugin logs streamed from the game server

---

## Setup

### Prerequisites

- A CS2 game server with [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) installed
- A Linux server (VPS, dedicated, etc.) with Docker for the DemoServer
- GOTV must not be disabled by your hosting provider

### DemoServer Setup

1. Copy the `DemoServer/` folder to your Linux server.

2. Create a `.env` file in the DemoServer directory:
   ```
   API_SECRET_KEY=your-secret-key-here
   ```

3. Create the storage directory:
   ```bash
   mkdir -p /storage
   ```

4. Build and start:
   ```bash
   docker compose up -d --build
   ```

5. The server is now running on port 8080. Verify:
   ```bash
   curl http://localhost:8080
   ```

### Plugin Setup

1. Build the plugin:
   ```bash
   cd CS2DemoBuddy
   dotnet build --configuration Release
   ```

2. Copy the output from `bin/Release/net8.0/` to your CS2 server:
   ```
   csgo/addons/counterstrikesharp/plugins/CS2DemoBuddy/
   ```

3. Start your CS2 server. The plugin will generate a config file at:
   ```
   csgo/addons/counterstrikesharp/configs/plugins/CS2DemoBuddy/CS2DemoBuddy.json
   ```

4. Edit the config:
   ```json
   {
     "ServerName": "My_Server",
     "ApiUrl": "http://your-server-ip:8080/upload",
     "ApiSecretKey": "your-secret-key-here",
     "ConfigVersion": 1
   }
   ```
   - **ServerName**: Used to organize demos on the web UI. Spaces get replaced with underscores.
   - **ApiUrl**: Full URL to your DemoServer's upload endpoint.
   - **ApiSecretKey**: Must match the `API_SECRET_KEY` you set on the DemoServer.

5. Restart the CS2 server or reload the plugin.

---

## Config Reference

| Field | Default | Description |
|---|---|---|
| `ServerName` | `My_Server` | Name shown on the web UI. Spaces become underscores. |
| `ApiUrl` | `http://YOUR_LINUX_SERVER_IP:8080/upload` | Upload endpoint on your DemoServer. |
| `ApiSecretKey` | *(empty)* | Must match the DemoServer's `API_SECRET_KEY`. |

---

## API Endpoints

All mutating endpoints require the `x-api-key` header.

| Method | Endpoint | Description |
|---|---|---|
| `POST` | `/upload` | Upload a `.dem` file. Multipart form with `demo` (file) and `serverName` (text). |
| `POST` | `/upload-log` | Append a log entry. JSON body with `serverName` and `log`. |
| `GET` | `/api/servers` | List all servers that have uploaded demos. |
| `GET` | `/api/servers/:server/dates` | List all dates with demos for a server. |
| `GET` | `/api/servers/:server/dates/:date/demos` | List all demos for a server on a date. |
| `GET` | `/api/logs/servers` | List all servers with logs. |
| `GET` | `/api/logs/:server/dates` | List all log dates for a server. |
| `GET` | `/api/logs/:server/dates/:date/content` | Get log content for a specific date. |
| `GET` | `/demos/:path` | Direct download link for a demo file. |

---

## Storage Layout

```
/storage/
├── .My_Server/
│   ├── 2026-03-09/
│   │   ├── de_dust2_031026_143022.dem
│   │   └── cs_italy_031026_150512.dem
│   └── 2026-03-10/
│       └── de_mirage_031026_020109.dem
├── .Another_Server/
│   └── ...
└── .logs/
    ├── My_Server/
    │   ├── 2026-03-09.log
    │   └── 2026-03-10.log
    └── Another_Server/
        └── ...
```

Server directories are prefixed with `.` in storage. Log files are organized by server name and date.

---

## GOTV Settings Applied

These are set automatically by the plugin. You don't need to put them in your server config.

| CVar | Value | Why |
|---|---|---|
| `tv_enable` | `1` | Turns on GOTV |
| `tv_delay` | `0` | **Required.** Without this, tv_record won't create files. |
| `tv_deltacache` | `-1` | Disables delta frame caching |
| `tv_snapshotrate` | `64` | Capture rate for GOTV snapshots |
| `tv_transmitall` | `1` | Transmit all player data into the demo |
| `tv_relayvoice` | `1` | Record voice chat into the demo |

---

## Troubleshooting

**Demos aren't being created at all**
- Make sure `tv_enable 1` is set before the map loads. Some hosts override this.
- Check that `tv_delay` is `0`. This was the #1 cause of silent failures during development.
- Look for the GOTV bot in the player list. If there's no GOTV bot, your host may have GOTV disabled.

**Upload says "Connection refused"**
- Your DemoServer container isn't running. SSH into your server and run `docker compose up -d --build`.
- Check that port 8080 is open in your firewall / security group.

**File lock errors during upload**
- This can happen if a new recording starts before the old upload finishes. The plugin handles this — the GC will retry it in an hour.

**Voice chat not in demos**
- Make sure `tv_relayvoice 1` is set. The plugin does this automatically, but some server configs or other plugins might override it.

---

## Tech Stack

- **Plugin**: C# / .NET 8 / CounterStrikeSharp API v1.0.267
- **Server**: Node.js 18 / Express / Multer
- **Deployment**: Docker + Docker Compose
- **Frontend**: Vanilla HTML/CSS/JS with a glassmorphism UI theme

---

## License

Do whatever you want with it.
