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

    public void AddDemo(string fileName, string serverName)
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
                        new XAttribute("ServerName", serverName)));
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

    public string? GetTargetServerName(string fileName)
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_xmlFilePath)) return null;
                var doc = XDocument.Load(_xmlFilePath);
                var el = doc.Element("DemoHistory")?.Elements("Demo").FirstOrDefault(e => e.Attribute("FileName")?.Value == fileName);
                return el?.Attribute("ServerName")?.Value;
            }
            catch (Exception ex)
            {
                _logger($"XML Tracker Error (Get): {ex.Message}");
                return null;
            }
        }
    }
}

public class CS2DemoBuddyPlugin : BasePlugin, IPluginConfig<CS2DemoBuddyConfig>
{
    public override string ModuleName => "CS2DemoBuddy";
    public override string ModuleVersion => "3.0.0";
    public override string ModuleAuthor => "GitHub Copilot";

    public CS2DemoBuddyConfig Config { get; set; } = new();
    private DemoHistoryTracker? HistoryTracker;
    private static readonly object _logLock = new object();

    private string CurrentDemoName = "";
    private bool IsRecording = false;
    private DateTime _mapStartTime = DateTime.MinValue;
    private DateTime _lastStopTime = DateTime.MinValue;
    private const int MapCooldownSeconds = 30;
    private const int StopCooldownSeconds = 60;
    private const long MinDemoSizeBytes = 1_000_000; // 1MB — skip junk demos

    private FileSystemWatcher? _watcher;
    private List<string> _watcherCreatedFiles = new();
    private readonly object _watcherLock = new();

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
            string serverName = Config.ServerName;

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
        Log("===== CS2DemoBuddy v3.0.0 LOADING =====");

        string configDir = Path.GetFullPath(Path.Combine(ModuleDirectory, "../../configs/plugins/CS2DemoBuddy"));
        if (!Directory.Exists(configDir)) Directory.CreateDirectory(configDir);
        HistoryTracker = new DemoHistoryTracker(configDir, Log);

        SetupFileWatcher();

        // CRITICAL: tv_delay 0 is required for tv_record to produce files
        Server.ExecuteCommand("tv_enable 1");
        Server.ExecuteCommand("tv_delay 0");
        Server.ExecuteCommand("tv_snapshotrate 64");
        Server.ExecuteCommand("tv_transmitall 1");
        Server.ExecuteCommand("tv_relayvoice 1");

        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);

        RegisterEventHandler<EventRoundStart>((@event, info) =>
        {
            if (!IsRecording)
            {
                try
                {
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

        RegisterEventHandler<EventCsWinPanelMatch>((@event, info) =>
        {
            StopAndUploadDemo();
            return HookResult.Continue;
        });

        AddTimer(15.0f, PlayerCheckLoop, TimerFlags.REPEAT);
        AddTimer(3600.0f, RunGarbageCollection, TimerFlags.REPEAT);

        Log("===== CS2DemoBuddy v3.0.0 LOADED =====");
    }

    public override void Unload(bool hotReload)
    {
        Log("===== CS2DemoBuddy v3.0.0 UNLOADING =====");

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

        CurrentDemoName = "";
        HistoryTracker = null;

        Log("===== CS2DemoBuddy v3.0.0 UNLOADED =====");
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

    private bool IsMapReady()
    {
        return (DateTime.UtcNow - _mapStartTime).TotalSeconds >= MapCooldownSeconds;
    }

    private bool IsStopCooldownOver()
    {
        return (DateTime.UtcNow - _lastStopTime).TotalSeconds >= StopCooldownSeconds;
    }

    private void PlayerCheckLoop()
    {
        if (IsRecording) return;
        if (!IsMapReady()) return;
        if (!IsStopCooldownOver()) return;

        try
        {
            var players = Utilities.GetPlayers();
            int humanCount = players.Count(p => p != null && p.IsValid && !p.IsBot && !p.IsHLTV);

            if (humanCount > 0)
            {
                StartRecording();
            }
        }
        catch (Exception ex)
        {
            Log($"PlayerCheckLoop error: {ex.Message}");
        }
    }

    private void StartRecording()
    {
        if (IsRecording) return;
        if (!IsMapReady())
        {
            Log($"Skipping recording — map only loaded {(DateTime.UtcNow - _mapStartTime).TotalSeconds:F0}s ago (need {MapCooldownSeconds}s).");
            return;
        }
        if (!IsStopCooldownOver())
        {
            Log($"Skipping recording — only {(DateTime.UtcNow - _lastStopTime).TotalSeconds:F0}s since last stop (need {StopCooldownSeconds}s).");
            return;
        }

        string timestamp = DateTime.UtcNow.ToString("MMddyy_HHmmss");
        string mapName = Server.MapName;
        CurrentDemoName = $"{mapName}_{timestamp}";
        string demoFileName = $"{CurrentDemoName}.dem";

        HistoryTracker?.AddDemo(demoFileName, Config.ServerName);

        // Clear watcher list for this recording session
        lock (_watcherLock) { _watcherCreatedFiles.Clear(); }

        Server.ExecuteCommand($"tv_record {CurrentDemoName}");
        IsRecording = true;
        Log($"Started recording: {demoFileName}");
    }

    private void OnMapStart(string mapName)
    {
        IsRecording = false;
        _mapStartTime = DateTime.UtcNow;
        _lastStopTime = DateTime.MinValue; // Reset stop cooldown on new map
        Log($"Map started: {mapName} (recording cooldown {MapCooldownSeconds}s)");

        Server.ExecuteCommand("tv_enable 1");
        Server.ExecuteCommand("tv_delay 0");
        Server.ExecuteCommand("tv_transmitall 1");
        Server.ExecuteCommand("tv_relayvoice 1");
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
        _lastStopTime = DateTime.UtcNow;

        string demoFileName = $"{CurrentDemoName}.dem";
        Log($"Stopped recording: {demoFileName}");

        // Snapshot watcher results at stop time (before a new recording can start)
        List<string> watcherSnapshot;
        lock (_watcherLock) { watcherSnapshot = new List<string>(_watcherCreatedFiles); }

        string gameDir = Server.GameDirectory;
        string stoppedDemoName = demoFileName;

        Task.Run(async () =>
        {
            // The engine needs time to finalize the demo file after tv_stoprecord.
            // It can take 10+ seconds for the file to even appear on disk.
            await Task.Delay(15000);

            string? foundPath = null;

            // Check watcher results first
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
            }

            if (foundPath == null)
            {
                Log($"No demo file found for {stoppedDemoName} after extended wait.");
                return;
            }

            // Wait for the file to stop growing (engine may still be flushing)
            foundPath = await WaitForFileStable(foundPath);
            if (foundPath == null) return;

            var finalSize = new FileInfo(foundPath).Length;
            if (finalSize < MinDemoSizeBytes)
            {
                Log($"Skipping {stoppedDemoName} — too small ({finalSize / 1024}KB). Deleting junk demo.");
                try { File.Delete(foundPath); } catch { }
                HistoryTracker?.RemoveDemo(stoppedDemoName);
                return;
            }

            Log($"Demo ready: {stoppedDemoName} ({finalSize / (1024 * 1024.0):F1}MB)");
            UploadAndDeleteDemoRoutine(foundPath, stoppedDemoName);
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
            Path.Combine(Server.GameDirectory, "csgo"),
            Server.GameDirectory,
            Environment.CurrentDirectory
        }.Distinct().ToArray();

        foreach (var dir in possibleDirs)
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var filePath in Directory.GetFiles(dir, "*.dem"))
                {
                    string fileName = Path.GetFileName(filePath);
                    if (IsRecording && fileName == $"{CurrentDemoName}.dem") continue;

                    string? targetServer = HistoryTracker?.GetTargetServerName(fileName);
                    if (targetServer == null)
                    {
                        Log($"GC: Deleting untracked {fileName}");
                        try { File.Delete(filePath); } catch { }
                    }
                    else
                    {
                        Log($"GC: Retrying upload for {fileName}");
                        UploadAndDeleteDemoRoutine(filePath, fileName, targetServer);
                    }
                }
            }
            catch { }
        }
    }

    private async void UploadAndDeleteDemoRoutine(string filePath, string fileName, string? serverNameFallback = null)
    {
        if (!File.Exists(filePath)) return;

        string targetServerName = serverNameFallback ?? Config.ServerName;
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(10);
            client.DefaultRequestHeaders.Add("x-api-key", Config.ApiSecretKey);
            using var form = new MultipartFormDataContent();

            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var streamContent = new StreamContent(fileStream);

            form.Add(new StringContent(targetServerName), "serverName");
            streamContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
            form.Add(streamContent, "demo", fileName);

            Log($"Uploading {fileName} to {Config.ApiUrl}...");
            var response = await client.PostAsync(Config.ApiUrl, form);

            if (response.IsSuccessStatusCode)
            {
                Log($"Successfully uploaded {fileName}. Deleting local file.");
                fileStream.Close();
                File.Delete(filePath);
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
