using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace CS2DemoBuddy;

public class CS2DemoBuddyConfig : BasePluginConfig
{
    [JsonPropertyName("ServerName")]
    public string ServerName { get; set; } = "My_Server";

    [JsonPropertyName("ApiUrl")]
    public string ApiUrl { get; set; } = "http://YOUR_LINUX_SERVER_IP:8080/upload";

    [JsonPropertyName("ApiSecretKey")]
    public string ApiSecretKey { get; set; } = "";
}

public class DemoHistoryTracker
{
    private readonly string _xmlFilePath;
    private readonly object _lock = new object();
    private readonly Action<string> _logger;

    public DemoHistoryTracker(string configDirectory, Action<string> logger)
    {
        _logger = logger;
        _xmlFilePath = Path.Combine(configDirectory, "demo_history.xml");
        InitializeXml();
    }

    private void InitializeXml()
    {
        lock (_lock)
        {
            if (!File.Exists(_xmlFilePath))
            {
                var doc = new XDocument(new XElement("DemoHistory"));
                doc.Save(_xmlFilePath);
            }
        }
    }

    public void AddDemo(string fileName, string serverName, string matchFolder, string matchDate)
    {
        lock (_lock)
        {
            try
            {
                var doc = File.Exists(_xmlFilePath) ? XDocument.Load(_xmlFilePath) : new XDocument(new XElement("DemoHistory"));
                var root = doc.Element("DemoHistory") ?? new XElement("DemoHistory");
                if (doc.Element("DemoHistory") == null) doc.Add(root);

                if (!root.Elements("Demo").Any(e => e.Attribute("FileName")?.Value == fileName))
                {
                    root.Add(new XElement("Demo",
                        new XAttribute("FileName", fileName),
                        new XAttribute("ServerName", serverName),
                        new XAttribute("MatchFolder", matchFolder),
                        new XAttribute("MatchDate", matchDate)));
                    doc.Save(_xmlFilePath);
                }
            }
            catch (Exception ex)
            {
                _logger($"XML Tracker Error (Add): {ex.Message}");
            }
        }
    }

    public void RemoveDemo(string fileName)
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_xmlFilePath)) return;
                var doc = XDocument.Load(_xmlFilePath);
                var elements = doc.Element("DemoHistory")?.Elements("Demo").Where(e => e.Attribute("FileName")?.Value == fileName).ToList();
                if (elements != null && elements.Any())
                {
                    foreach (var el in elements) el.Remove();
                    doc.Save(_xmlFilePath);
                }
            }
            catch (Exception ex)
            {
                _logger($"XML Tracker Error (Remove): {ex.Message}");
            }
        }
    }

    public (string? serverName, string? matchFolder, string? matchDate) GetDemoInfo(string fileName)
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_xmlFilePath)) return (null, null, null);
                var doc = XDocument.Load(_xmlFilePath);
                var el = doc.Element("DemoHistory")?.Elements("Demo").FirstOrDefault(e => e.Attribute("FileName")?.Value == fileName);
                if (el == null) return (null, null, null);
                return (
                    el.Attribute("ServerName")?.Value,
                    el.Attribute("MatchFolder")?.Value,
                    el.Attribute("MatchDate")?.Value
                );
            }
            catch (Exception ex)
            {
                _logger($"XML Tracker Error (GetInfo): {ex.Message}");
                return (null, null, null);
            }
        }
    }
}

public class CS2DemoBuddyPlugin : BasePlugin, IPluginConfig<CS2DemoBuddyConfig>
{
    public override string ModuleName => "CS2DemoBuddy";
    public override string ModuleVersion => "3.3.1";
    public override string ModuleAuthor => "VinSix";

    public CS2DemoBuddyConfig Config { get; set; } = new();
    private DemoHistoryTracker? HistoryTracker;
    private static readonly object _logLock = new object();

    private string StorageServerName => $"DBS_{Config.ServerName}";

    private string CurrentDemoName = "";
    private bool IsRecording = false;
    private string _matchFolder = "";
    private string _matchDate = "";
    private const long MinDemoSizeBytes = 1_000_000; // 1MB — skip junk demos

    private FileSystemWatcher? _watcher;
    private List<string> _watcherCreatedFiles = new();
    private readonly object _watcherLock = new();
    private readonly HashSet<string> _pendingFiles = new();
    private readonly object _pendingLock = new();
    private CancellationTokenSource? _inventoryCts;
    private string _gameDirectory = "";

    private void Log(string message)
    {
        Console.WriteLine($"[CS2DemoBuddy] {message}");
        string logEntry = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";

        try
        {
            string configDir = Path.GetFullPath(Path.Combine(ModuleDirectory, "../../configs/plugins/CS2DemoBuddy"));
            string logsDir = Path.Combine(configDir, "logs");
            if (!Directory.Exists(logsDir)) Directory.CreateDirectory(logsDir);

            string logFile = Path.Combine(logsDir, $"CS2DemoBuddy_{DateTime.UtcNow:yyyy-MM-dd}.log");
            lock (_logLock)
            {
                File.AppendAllText(logFile, logEntry);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CS2DemoBuddy] Failed to write to log: {ex.Message}");
        }

        try
        {
            string logEndpoint = Config.ApiUrl.Replace("/upload", "/upload-log");
            string apiKey = Config.ApiSecretKey;
            string serverName = StorageServerName;

            Task.Run(async () =>
            {
                try
                {
                    using var client = new HttpClient();
                    client.DefaultRequestHeaders.Add("x-api-key", apiKey);
                    var json = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        serverName = serverName,
                        log = logEntry
                    });
                    var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                    await client.PostAsync(logEndpoint, content);
                }
                catch { }
            });
        }
        catch { }
    }

    public void OnConfigParsed(CS2DemoBuddyConfig config)
    {
        if (config.ServerName.Contains(" "))
        {
            config.ServerName = config.ServerName.Replace(" ", "_");
        }
        Config = config;
    }

    public override void Load(bool hotReload)
    {
        Log("===== CS2DemoBuddy v3.3.1 LOADING =====");

        // Cache game directory for background thread access
        _gameDirectory = Server.GameDirectory;

        string configDir = Path.GetFullPath(Path.Combine(ModuleDirectory, "../../configs/plugins/CS2DemoBuddy"));
        if (!Directory.Exists(configDir)) Directory.CreateDirectory(configDir);
        HistoryTracker = new DemoHistoryTracker(configDir, Log);

        SetupFileWatcher();

        // Clear any stuck recording from a previous session or crash
        Server.ExecuteCommand("tv_stoprecord");

        // Apply GOTV settings once at load — not per-round or per-map
        ApplyGotvSettings();

        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);

        // Per-match recording: start on first non-warmup round, record
        // continuously until map ends. CS2 engine only flushes .dem files
        // to disk on map change — per-round recording is not possible.
        RegisterEventHandler<EventRoundStart>((@event, info) =>
        {
            if (!IsRecording)
            {
                try
                {
                    if (IsWarmup())
                    {
                        Log("RoundStart during warmup — skipping recording.");
                        return HookResult.Continue;
                    }

                    var players = Utilities.GetPlayers();
                    int humanCount = players.Count(p => p != null && p.IsValid && !p.IsBot && !p.IsHLTV);
                    if (humanCount > 0)
                    {
                        Log($"RoundStart with {humanCount} human(s). Starting match recording...");
                        StartRecording();
                    }
                }
                catch (Exception ex)
                {
                    Log($"RoundStart handler error: {ex.Message}");
                }
            }
            return HookResult.Continue;
        });

        // No RoundEnd stop — CS2 only writes .dem files on map change.
        // Recording runs continuously from first live round until OnMapEnd.

        // Safety net: log match end (recording continues until OnMapEnd)
        RegisterEventHandler<EventCsWinPanelMatch>((@event, info) =>
        {
            if (IsRecording)
            {
                Log("CsWinPanelMatch — match ended, recording continues until map change.");
            }
            return HookResult.Continue;
        });

        AddTimer(2700.0f, RunGarbageCollection, TimerFlags.REPEAT);

        // Periodic source-file inventory reporting
        _inventoryCts = new CancellationTokenSource();
        _ = RunInventoryLoop(_inventoryCts.Token);

        Log("===== CS2DemoBuddy v3.3.1 LOADED =====");
    }

    public override void Unload(bool hotReload)
    {
        Log("===== CS2DemoBuddy v3.3.1 UNLOADING =====");

        // Stop any active recording
        if (IsRecording)
        {
            Server.ExecuteCommand("tv_stoprecord");
            IsRecording = false;
        }

        // Dispose FileSystemWatcher
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }

        // Clear watcher state
        lock (_watcherLock)
        {
            _watcherCreatedFiles.Clear();
        }

        // Cancel inventory loop
        if (_inventoryCts != null)
        {
            _inventoryCts.Cancel();
            _inventoryCts.Dispose();
            _inventoryCts = null;
        }

        CurrentDemoName = "";
        HistoryTracker = null;

        Log("===== CS2DemoBuddy v3.3.1 UNLOADED =====");
    }

    private void ApplyGotvSettings()
    {
        Log("Applying GOTV settings...");

        // IMPORTANT: tv_enable is a startup-only cvar in CS2. This command
        // alone will NOT spawn the GOTV bot if it wasn't enabled at server
        // launch. You MUST add +tv_enable 1 to your server startup command
        // line (or server.cfg) for GOTV to work.
        Server.ExecuteCommand("tv_enable 1");

        // Zero delay — demo data is written immediately to the GOTV buffer
        Server.ExecuteCommand("tv_delay 0");

        // tv_transmitall controls what is sent over the NETWORK to live
        // GOTV spectator clients — NOT what tv_record writes to disk.
        // tv_record captures the full server state (all players, all ticks)
        // regardless of this setting. With 20+ players, transmitall 1
        // overwhelms the GOTV relay and produces ~100KB junk demo files.
        // All major plugins (MatchZy, Get5) use 0. Demos still support
        // switching between any player's POV.
        Server.ExecuteCommand("tv_transmitall 0");

        // Immediate file writing — helps ensure demo is flushed on map change
        Server.ExecuteCommand("tv_record_immediate 1");

        Server.ExecuteCommand("tv_relayvoice 1");

        // Quality / rate settings
        Server.ExecuteCommand("tv_snapshotrate 32");
        Server.ExecuteCommand("tv_maxrate 0");
        Server.ExecuteCommand("tv_deltacache -1");

        // Autorecord off — we manage recording ourselves
        Server.ExecuteCommand("tv_autorecord 0");

        Log("GOTV settings applied: tv_enable 1, tv_delay 0, tv_transmitall 0, tv_record_immediate 1, tv_snapshotrate 32, tv_maxrate 0");
    }

    private void SetupFileWatcher()
    {
        try
        {
            string watchRoot = Path.Combine(Server.GameDirectory, "csgo");
            if (!Directory.Exists(watchRoot))
                watchRoot = Server.GameDirectory;

            _watcher = new FileSystemWatcher(watchRoot);
            _watcher.Filter = "*.dem";
            _watcher.IncludeSubdirectories = true;
            _watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.Size;
            _watcher.Created += (sender, e) =>
            {
                lock (_watcherLock) { _watcherCreatedFiles.Add(e.FullPath); }
                Log($"Demo file detected: {e.FullPath}");
            };
            _watcher.EnableRaisingEvents = true;
            Log($"FileSystemWatcher active on {watchRoot}");
        }
        catch (Exception ex)
        {
            Log($"FileSystemWatcher setup failed: {ex.Message}");
        }
    }

    private bool IsWarmup()
    {
        try
        {
            var rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;
            return rules?.WarmupPeriod ?? false;
        }
        catch
        {
            return false;
        }
    }

    private void StartRecording()
    {
        if (IsRecording) return;

        string mapName = Server.MapName;
        string matchTimestamp = DateTime.UtcNow.ToString("HHmmss");
        _matchFolder = $"{mapName}-{matchTimestamp}";
        _matchDate = DateTime.UtcNow.ToString("yyyy-MM-dd");

        CurrentDemoName = $"{mapName}_{matchTimestamp}";
        string demoFileName = $"{CurrentDemoName}.dem";

        HistoryTracker?.AddDemo(demoFileName, StorageServerName, _matchFolder, _matchDate);

        // Clear watcher list for this recording session
        lock (_watcherLock) { _watcherCreatedFiles.Clear(); }

        Server.NextFrame(() =>
        {
            Server.ExecuteCommand($"tv_record {CurrentDemoName}");
            Log($"Started recording: {demoFileName} (Match: {_matchFolder})");
        });

        IsRecording = true;
    }

    private void OnMapStart(string mapName)
    {
        IsRecording = false;
        _matchFolder = "";
        _matchDate = "";
        Log($"Map started: {mapName}");

        // Re-apply GOTV settings every map start. Server.cfg and other
        // configs can override our settings between maps. This ensures
        // tv_delay 0 and tv_transmitall 1 are always active.
        ApplyGotvSettings();
    }

    private void OnMapEnd()
    {
        StopAndUploadDemo();
    }

    private void StopAndUploadDemo()
    {
        if (!IsRecording) return;

        Server.ExecuteCommand("tv_stoprecord");
        IsRecording = false;

        string demoFileName = $"{CurrentDemoName}.dem";
        string stoppedMatchFolder = _matchFolder;
        string stoppedMatchDate = _matchDate;
        Log($"Stopped recording: {demoFileName}");

        // Track this demo as pending so GC won't touch it
        lock (_pendingLock) { _pendingFiles.Add(demoFileName); }

        // Snapshot watcher results at stop time
        List<string> watcherSnapshot;
        lock (_watcherLock) { watcherSnapshot = new List<string>(_watcherCreatedFiles); }

        string gameDir = _gameDirectory;
        string stoppedDemoName = demoFileName;

        Task.Run(async () =>
        {
            // CS2 engine only flushes .dem files to disk on map change,
            // NOT on tv_stoprecord. StopAndUploadDemo is called from OnMapEnd,
            // so the file should appear shortly after. Wait then retry.
            await Task.Delay(30000);

            string? foundPath = FindDemoFile(stoppedDemoName, gameDir, watcherSnapshot);

            // Second attempt — file may still be writing
            if (foundPath == null)
            {
                Log($"First search for {stoppedDemoName} failed, waiting 30s more...");
                await Task.Delay(30000);
                foundPath = FindDemoFile(stoppedDemoName, gameDir, null);
            }

            // Third attempt — extended wait as final fallback
            if (foundPath == null)
            {
                Log($"Second search for {stoppedDemoName} failed, waiting 60s more...");
                await Task.Delay(60000);
                foundPath = FindDemoFile(stoppedDemoName, gameDir, null);
            }

            if (foundPath == null)
            {
                Log($"No demo file found for {stoppedDemoName} after extended wait. Will retry during GC.");
                // Don't remove from pending — GC will find and retry
                return;
            }

            // Wait for the file to stop growing (engine may still be flushing)
            foundPath = await WaitForFileStable(foundPath);
            if (foundPath == null)
            {
                lock (_pendingLock) { _pendingFiles.Remove(stoppedDemoName); }
                return;
            }

            var finalSize = new FileInfo(foundPath).Length;
            if (finalSize < MinDemoSizeBytes)
            {
                Log($"Skipping {stoppedDemoName} — too small ({finalSize / 1024}KB). Will be cleaned up by GC.");
                HistoryTracker?.RemoveDemo(stoppedDemoName);
                lock (_pendingLock) { _pendingFiles.Remove(stoppedDemoName); }
                return;
            }

            Log($"Demo ready: {stoppedDemoName} ({finalSize / (1024 * 1024.0):F1}MB)");
            await UploadDemoRoutine(foundPath, stoppedDemoName, null, stoppedMatchFolder, stoppedMatchDate);
            lock (_pendingLock) { _pendingFiles.Remove(stoppedDemoName); }
        });
    }

    private string? FindDemoFile(string demoName, string gameDir, List<string>? watcherSnapshot)
    {
        // Check watcher results
        if (watcherSnapshot != null)
        {
            if (watcherSnapshot.Count > 0)
                Log($"Watcher snapshot: {string.Join(", ", watcherSnapshot.Select(Path.GetFileName))}");
            foreach (var f in watcherSnapshot)
            {
                if (File.Exists(f) && Path.GetFileName(f) == demoName) return f;
            }
        }

        // Check late watcher results
        lock (_watcherLock)
        {
            foreach (var f in _watcherCreatedFiles)
            {
                if (Path.GetFileName(f) == demoName && File.Exists(f)) return f;
            }
        }

        // Direct path check in known directories
        string[] scanDirs = new[] {
            Path.Combine(gameDir, "csgo"),
            gameDir,
            Environment.CurrentDirectory
        };

        foreach (var dir in scanDirs.Distinct())
        {
            if (!Directory.Exists(dir)) continue;
            string candidate = Path.Combine(dir, demoName);
            if (File.Exists(candidate)) return candidate;
        }

        // Recursive search
        foreach (var dir in scanDirs.Distinct().Take(2))
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                var matches = Directory.GetFiles(dir, demoName, SearchOption.AllDirectories);
                if (matches.Length > 0)
                {
                    Log($"Found {demoName} via recursive search: {matches[0]}");
                    return matches[0];
                }
            }
            catch { }
        }

        // Diagnostic: log what .dem files exist
        foreach (var dir in scanDirs.Distinct())
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                var demFiles = Directory.GetFiles(dir, "*.dem");
                if (demFiles.Length > 0)
                    Log($"Scan [{dir}]: {demFiles.Length} .dem file(s): {string.Join(", ", demFiles.Select(Path.GetFileName).Take(10))}");
            }
            catch { }
        }

        return null;
    }

    private async Task<string?> WaitForFileStable(string filePath)
    {
        // Poll the file size every 5 seconds. Once it stops changing for 2 consecutive
        // checks (10 seconds of no growth), the engine is done writing.
        const int maxAttempts = 24; // 2 minutes max
        long lastSize = -1;
        int stableCount = 0;

        for (int i = 0; i < maxAttempts; i++)
        {
            await Task.Delay(5000);

            if (!File.Exists(filePath))
            {
                Log($"File disappeared while waiting: {Path.GetFileName(filePath)}");
                return null;
            }

            try
            {
                long currentSize = new FileInfo(filePath).Length;
                if (currentSize == lastSize && currentSize > 0)
                {
                    stableCount++;
                    if (stableCount >= 2)
                    {
                        Log($"File stable at {currentSize / (1024 * 1024.0):F1}MB after {(i + 1) * 5}s.");
                        return filePath;
                    }
                }
                else
                {
                    stableCount = 0;
                }
                lastSize = currentSize;
            }
            catch
            {
                stableCount = 0;
            }
        }

        // Timed out but file exists — upload what we have
        Log($"File size did not stabilize after 2 minutes, uploading anyway ({lastSize / (1024 * 1024.0):F1}MB).");
        return File.Exists(filePath) ? filePath : null;
    }

    private void RunGarbageCollection()
    {
        Log("Running garbage collection...");
        string[] possibleDirs = new[] {
            Path.Combine(_gameDirectory, "csgo"),
            _gameDirectory,
            Environment.CurrentDirectory
        }.Distinct().ToArray();

        // Snapshot pending files so we don't delete anything mid-upload
        HashSet<string> pendingSnapshot;
        lock (_pendingLock) { pendingSnapshot = new HashSet<string>(_pendingFiles); }

        foreach (var dir in possibleDirs)
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var filePath in Directory.GetFiles(dir, "*.dem"))
                {
                    string fileName = Path.GetFileName(filePath);

                    // Skip the demo currently being recorded
                    if (IsRecording && fileName == $"{CurrentDemoName}.dem") continue;

                    // Skip demos currently being stabilized or uploaded
                    if (pendingSnapshot.Contains(fileName)) continue;

                    var (targetServer, gcMatchFolder, gcMatchDate) = HistoryTracker?.GetDemoInfo(fileName) ?? (null, null, null);
                    if (targetServer == null)
                    {
                        // Untracked: either already uploaded or junk — safe to delete
                        Log($"GC: Deleting untracked {fileName}");
                        try { File.Delete(filePath); } catch { }
                    }
                    else
                    {
                        // Still tracked: upload failed or hasn't been attempted yet
                        Log($"GC: Retrying upload for {fileName}");
                        lock (_pendingLock) { _pendingFiles.Add(fileName); }
                        _ = RetryUploadAndRelease(filePath, fileName, targetServer, gcMatchFolder, gcMatchDate);
                    }
                }
            }
            catch { }
        }
    }

    private async Task RetryUploadAndRelease(string filePath, string fileName, string targetServer, string? matchFolder, string? matchDate)
    {
        try
        {
            await UploadDemoRoutine(filePath, fileName, targetServer, matchFolder, matchDate);
        }
        finally
        {
            lock (_pendingLock) { _pendingFiles.Remove(fileName); }
        }
    }

    private async Task RunInventoryLoop(CancellationToken ct)
    {
        // Initial delay before first report
        try { await Task.Delay(15000, ct); } catch (TaskCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ReportSourceFiles();
            }
            catch (Exception ex)
            {
                Log($"Inventory report error: {ex.Message}");
            }

            try { await Task.Delay(60000, ct); } catch (TaskCanceledException) { return; }
        }
    }

    private async Task ReportSourceFiles()
    {
        string[] scanDirs = new[] {
            Path.Combine(_gameDirectory, "csgo"),
            _gameDirectory,
            Environment.CurrentDirectory
        }.Distinct().ToArray();

        var files = new List<object>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in scanDirs)
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var filePath in Directory.GetFiles(dir, "*.dem"))
                {
                    string fileName = Path.GetFileName(filePath);
                    if (!seen.Add(fileName)) continue;
                    try
                    {
                        var info = new FileInfo(filePath);
                        files.Add(new { name = fileName, sizeBytes = info.Length, modified = info.LastWriteTimeUtc.ToString("o") });
                    }
                    catch { }
                }
            }
            catch { }
        }

        string endpoint = Config.ApiUrl.Replace("/upload", "/upload-source-files");
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Add("x-api-key", Config.ApiSecretKey);

        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            serverName = StorageServerName,
            files = files
        });
        var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        await client.PostAsync(endpoint, content);
    }

    private async Task UploadDemoRoutine(string filePath, string fileName, string? serverNameFallback = null, string? matchFolder = null, string? matchDate = null)
    {
        if (!File.Exists(filePath)) return;

        string targetServerName = serverNameFallback ?? StorageServerName;
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(10);
            client.DefaultRequestHeaders.Add("x-api-key", Config.ApiSecretKey);
            using var form = new MultipartFormDataContent();

            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var streamContent = new StreamContent(fileStream);

            form.Add(new StringContent(targetServerName), "serverName");
            form.Add(new StringContent(matchFolder ?? ""), "matchFolder");
            form.Add(new StringContent(matchDate ?? DateTime.UtcNow.ToString("yyyy-MM-dd")), "matchDate");
            streamContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
            form.Add(streamContent, "demo", fileName);

            Log($"Uploading {fileName} to {Config.ApiUrl}...");
            var response = await client.PostAsync(Config.ApiUrl, form);

            if (response.IsSuccessStatusCode)
            {
                Log($"Successfully uploaded {fileName}. File will be cleaned up by GC.");
                HistoryTracker?.RemoveDemo(fileName);
            }
            else
            {
                string respError = await response.Content.ReadAsStringAsync();
                Log($"Failed to upload {fileName}. Status: {response.StatusCode} | {respError}");
            }
        }
        catch (Exception ex)
        {
            Log($"Exception during upload of {fileName}: {ex.Message}");
        }
    }
}
