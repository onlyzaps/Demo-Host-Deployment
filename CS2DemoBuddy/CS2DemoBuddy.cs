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
    public override string ModuleVersion => "3.1.0";
    public override string ModuleAuthor => "VinSix";

    public CS2DemoBuddyConfig Config { get; set; } = new();
    private DemoHistoryTracker? HistoryTracker;
    private static readonly object _logLock = new object();

    private string StorageServerName => $"DBS_{Config.ServerName}";

    private string CurrentDemoName = "";
    private bool IsRecording = false;
    private int _roundNumber = 0;
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
        Log("===== CS2DemoBuddy v3.1.0 LOADING =====");

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

        // Per-round recording: start a new demo each round
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
                        Log($"RoundStart with {humanCount} human(s). Starting recording...");
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

        // Per-round recording: stop and upload at end of each round
        RegisterEventHandler<EventRoundEnd>((@event, info) =>
        {
            if (IsRecording)
            {
                Log("RoundEnd — stopping recording for this round.");
                StopAndUploadDemo();
            }
            return HookResult.Continue;
        });

        // Safety net: stop recording on match end if RoundEnd didn't fire
        RegisterEventHandler<EventCsWinPanelMatch>((@event, info) =>
        {
            if (IsRecording)
            {
                Log("CsWinPanelMatch — stopping recording.");
                StopAndUploadDemo();
            }
            return HookResult.Continue;
        });

        AddTimer(2700.0f, RunGarbageCollection, TimerFlags.REPEAT);

        // Periodic source-file inventory reporting
        _inventoryCts = new CancellationTokenSource();
        _ = RunInventoryLoop(_inventoryCts.Token);

        Log("===== CS2DemoBuddy v3.1.0 LOADED =====");
    }

    public override void Unload(bool hotReload)
    {
        Log("===== CS2DemoBuddy v3.1.0 UNLOADING =====");

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

        Log("===== CS2DemoBuddy v3.1.0 UNLOADED =====");
    }

    private void ApplyGotvSettings()
    {
        Log("Applying GOTV settings (one-time at load)...");

        // Core GOTV enable + recording requirements
        Server.ExecuteCommand("tv_enable 1");
        Server.ExecuteCommand("tv_delay 0");           // CRITICAL: without this, tv_record produces empty files

        // Full data capture settings
        Server.ExecuteCommand("tv_transmitall 1");      // Transmit ALL entity updates to GOTV relay
        Server.ExecuteCommand("tv_relayvoice 1");       // Include voice in recording

        // Quality / rate settings — remove any throttling
        Server.ExecuteCommand("tv_snapshotrate 64");    // Match server tickrate
        Server.ExecuteCommand("tv_maxrate 0");          // No rate limit on GOTV stream
        Server.ExecuteCommand("tv_deltacache -1");      // Unlimited delta cache (was in v2.1.0)

        // Autorecord off — we manage recording ourselves
        Server.ExecuteCommand("tv_autorecord 0");

        Log("GOTV settings applied: tv_enable 1, tv_delay 0, tv_transmitall 1, tv_relayvoice 1, tv_snapshotrate 64, tv_maxrate 0, tv_deltacache -1");
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

        _roundNumber++;
        string mapName = Server.MapName;

        if (_roundNumber == 1)
        {
            string matchTimestamp = DateTime.UtcNow.ToString("HHmmss");
            _matchFolder = $"{mapName}-{matchTimestamp}";
            _matchDate = DateTime.UtcNow.ToString("yyyy-MM-dd");
        }

        string roundTimestamp = DateTime.UtcNow.ToString("HHmmss");
        CurrentDemoName = $"{mapName}_round_{_roundNumber}_{roundTimestamp}";
        string demoFileName = $"{CurrentDemoName}.dem";

        HistoryTracker?.AddDemo(demoFileName, StorageServerName, _matchFolder, _matchDate);

        // Clear watcher list for this recording session
        lock (_watcherLock) { _watcherCreatedFiles.Clear(); }

        Server.NextFrame(() =>
        {
            Server.ExecuteCommand($"tv_record {CurrentDemoName}");
            Log($"Started recording: {demoFileName} (Match: {_matchFolder}, Round: {_roundNumber})");
        });

        IsRecording = true;
    }

    private void OnMapStart(string mapName)
    {
        IsRecording = false;
        _roundNumber = 0;
        _matchFolder = "";
        _matchDate = "";
        Log($"Map started: {mapName}");
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

        // Snapshot watcher results at stop time (before a new recording can start)
        List<string> watcherSnapshot;
        lock (_watcherLock) { watcherSnapshot = new List<string>(_watcherCreatedFiles); }

        string gameDir = _gameDirectory;
        string stoppedDemoName = demoFileName;

        Task.Run(async () =>
        {
            // Wait 30s for engine to finalize the demo file after tv_stoprecord.
            await Task.Delay(30000);

            string? foundPath = null;

            // Check watcher results first
            if (watcherSnapshot.Count > 0)
            {
                Log($"Watcher snapshot has {watcherSnapshot.Count} file(s): {string.Join(", ", watcherSnapshot.Select(Path.GetFileName))}");
            }
            foreach (var f in watcherSnapshot)
            {
                if (File.Exists(f)) { foundPath = f; break; }
            }

            // Fallback: scan known directories for the specific file
            if (foundPath == null)
            {
                string[] scanDirs = new[] {
                    Path.Combine(gameDir, "csgo"),
                    gameDir,
                    Environment.CurrentDirectory
                };

                foreach (var dir in scanDirs.Distinct())
                {
                    if (!Directory.Exists(dir)) continue;
                    string candidate = Path.Combine(dir, stoppedDemoName);
                    if (File.Exists(candidate)) { foundPath = candidate; break; }
                }

                // Diagnostic: log what .dem files actually exist
                if (foundPath == null)
                {
                    foreach (var dir in scanDirs.Distinct())
                    {
                        if (!Directory.Exists(dir)) continue;
                        try
                        {
                            var demFiles = Directory.GetFiles(dir, "*.dem");
                            if (demFiles.Length > 0)
                                Log($"Scan [{dir}]: found {demFiles.Length} .dem file(s): {string.Join(", ", demFiles.Select(Path.GetFileName).Take(10))}");
                            else
                                Log($"Scan [{dir}]: no .dem files");
                        }
                        catch { }
                    }
                }
            }

            // Also check if the watcher caught it after the snapshot was taken
            if (foundPath == null)
            {
                lock (_watcherLock)
                {
                    foreach (var f in _watcherCreatedFiles)
                    {
                        if (Path.GetFileName(f) == stoppedDemoName && File.Exists(f))
                        { foundPath = f; break; }
                    }
                }
            }

            // Recursive search: the engine may write demos to a subdirectory
            if (foundPath == null)
            {
                string[] recurseDirs = new[] {
                    Path.Combine(gameDir, "csgo"),
                    gameDir
                };
                foreach (var dir in recurseDirs.Distinct())
                {
                    if (!Directory.Exists(dir)) continue;
                    try
                    {
                        var matches = Directory.GetFiles(dir, stoppedDemoName, SearchOption.AllDirectories);
                        if (matches.Length > 0)
                        {
                            foundPath = matches[0];
                            Log($"Found {stoppedDemoName} via recursive search: {foundPath}");
                            break;
                        }
                    }
                    catch { }
                }
            }

            if (foundPath == null)
            {
                // One more attempt after a longer wait
                await Task.Delay(30000);

                string[] scanDirs2 = new[] {
                    Path.Combine(gameDir, "csgo"),
                    gameDir,
                    Environment.CurrentDirectory
                };

                foreach (var dir in scanDirs2.Distinct())
                {
                    if (!Directory.Exists(dir)) continue;
                    string candidate = Path.Combine(dir, stoppedDemoName);
                    if (File.Exists(candidate)) { foundPath = candidate; break; }
                }

                // Second recursive search
                if (foundPath == null)
                {
                    foreach (var dir in scanDirs2.Distinct().Take(2))
                    {
                        if (!Directory.Exists(dir)) continue;
                        try
                        {
                            var matches = Directory.GetFiles(dir, stoppedDemoName, SearchOption.AllDirectories);
                            if (matches.Length > 0)
                            {
                                foundPath = matches[0];
                                Log($"Found {stoppedDemoName} via recursive search (2nd pass): {foundPath}");
                                break;
                            }
                        }
                        catch { }
                    }
                }
            }

            if (foundPath == null)
            {
                Log($"No demo file found for {stoppedDemoName} after extended wait.");
                lock (_pendingLock) { _pendingFiles.Remove(stoppedDemoName); }
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
