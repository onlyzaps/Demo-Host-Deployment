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
                    foreach(var el in elements) el.Remove();
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
    public override string ModuleVersion => "2.1.0";
    public override string ModuleAuthor => "GitHub Copilot";

    public CS2DemoBuddyConfig Config { get; set; } = new();
    private DemoHistoryTracker? HistoryTracker;
    private static readonly object _logLock = new object();

    private string CurrentDemoName = "";
    private bool IsRecording = false;
    private HashSet<string> _knownDemFiles = new();
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

            Task.Run(async () => {
                try {
                    using var client = new HttpClient();
                    client.DefaultRequestHeaders.Add("x-api-key", apiKey);
                    var json = System.Text.Json.JsonSerializer.Serialize(new {
                        serverName = serverName,
                        log = logEntry
                    });
                    var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                    await client.PostAsync(logEndpoint, content);
                } catch { } 
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
        Log("===== CS2DemoBuddy v2.1.0 LOADING =====");

        string configDir = Path.GetFullPath(Path.Combine(ModuleDirectory, "../../configs/plugins/CS2DemoBuddy"));
        if (!Directory.Exists(configDir)) Directory.CreateDirectory(configDir);
        HistoryTracker = new DemoHistoryTracker(configDir, Log);

        Log($"DIAG: Server.GameDirectory = {Server.GameDirectory}");
        Log($"DIAG: Environment.CurrentDirectory = {Environment.CurrentDirectory}");

        // --- Setup FileSystemWatchers to catch ANY .dem creation anywhere ---
        SetupFileWatchers();

        // --- CRITICAL: Force GOTV settings that are known to fix tv_record ---
        Server.ExecuteCommand("tv_enable 1");
        Server.ExecuteCommand("tv_delay 0");
        Server.ExecuteCommand("tv_deltacache -1");
        Server.ExecuteCommand("tv_snapshotrate 64");
        Server.ExecuteCommand("tv_transmitall 1");
        Log("DIAG: Sent tv_enable 1, tv_delay 0, tv_deltacache -1, tv_snapshotrate 64, tv_transmitall 1");

        SnapshotDemFiles("BOOT");

        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);

        RegisterEventHandler<EventRoundStart>((@event, info) => {
            if (!IsRecording)
            {
                try {
                    var players = Utilities.GetPlayers();
                    int humanCount = players.Count(p => p != null && p.IsValid && !p.IsBot && !p.IsHLTV);
                    if (humanCount > 0)
                    {
                        Log($"DIAG: RoundStart with {humanCount} human(s). Starting recording...");
                        StartRecording();
                    }
                } catch (Exception ex) {
                    Log($"RoundStart handler error: {ex.Message}");
                }
            }
            return HookResult.Continue;
        });

        RegisterEventHandler<EventCsWinPanelMatch>((@event, info) => {
            StopAndUploadDemo();
            return HookResult.Continue;
        });

        AddTimer(15.0f, PlayerCheckLoop, TimerFlags.REPEAT);
        AddTimer(3600.0f, RunGarbageCollection, TimerFlags.REPEAT);

        Log("===== CS2DemoBuddy v2.1.0 LOADED =====");
    }

    private void SetupFileWatchers()
    {
        // Watch the ENTIRE /CS2 tree for any .dem file creation
        try {
            string watchRoot = Directory.GetParent(Server.GameDirectory)?.FullName ?? Server.GameDirectory;
            _watcher = new FileSystemWatcher(watchRoot);
            _watcher.Filter = "*.dem";
            _watcher.IncludeSubdirectories = true;
            _watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.Size;
            _watcher.Created += (sender, e) => {
                lock (_watcherLock) { _watcherCreatedFiles.Add(e.FullPath); }
                Log($"WATCHER: .dem file CREATED at {e.FullPath}");
            };
            _watcher.Changed += (sender, e) => {
                Log($"WATCHER: .dem file CHANGED at {e.FullPath}");
            };
            _watcher.EnableRaisingEvents = true;
            Log($"DIAG: FileSystemWatcher active on {watchRoot} (recursive)");
        } catch (Exception ex) {
            Log($"DIAG: FileSystemWatcher setup FAILED: {ex.Message}");
        }

        // Also watch the CWD separately in case it's on a different mount
        try {
            string cwd = Environment.CurrentDirectory;
            var cwdWatcher = new FileSystemWatcher(cwd);
            cwdWatcher.Filter = "*.dem";
            cwdWatcher.IncludeSubdirectories = true;
            cwdWatcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.Size;
            cwdWatcher.Created += (sender, e) => {
                lock (_watcherLock) { _watcherCreatedFiles.Add(e.FullPath); }
                Log($"WATCHER-CWD: .dem file CREATED at {e.FullPath}");
            };
            cwdWatcher.EnableRaisingEvents = true;
            Log($"DIAG: CWD FileSystemWatcher active on {cwd}");
        } catch (Exception ex) {
            Log($"DIAG: CWD FileSystemWatcher FAILED: {ex.Message}");
        }
    }

    private void SnapshotDemFiles(string label)
    {
        try {
            string gameDir = Server.GameDirectory;
            string parentDir = Directory.GetParent(gameDir)?.FullName ?? gameDir;
            string csgoDir = Path.Combine(gameDir, "csgo");
            
            var allDems = new List<string>();
            
            string[] checkDirs = new[] {
                gameDir,
                parentDir,
                csgoDir,
                Path.Combine(gameDir, "bin", "linuxsteamrt64"),
                Path.Combine(gameDir, "bin", "win64"),
                Environment.CurrentDirectory
            };

            foreach (var dir in checkDirs.Distinct())
            {
                if (!Directory.Exists(dir)) continue;
                try {
                    foreach (var f in Directory.GetFiles(dir, "*.dem"))
                        if (!allDems.Contains(f)) allDems.Add(f);
                } catch { }
            }
            
            try {
                foreach (var f in Directory.GetFiles(parentDir, "*.dem", SearchOption.AllDirectories))
                    if (!allDems.Contains(f)) allDems.Add(f);
            } catch { }

            if (allDems.Count > 0) {
                string list = string.Join(", ", allDems.Select(f => { try { return $"{f} ({new FileInfo(f).Length}b)"; } catch { return f; } }));
                Log($"SNAPSHOT [{label}]: {allDems.Count} .dem files: {list}");
            } else {
                Log($"SNAPSHOT [{label}]: ZERO .dem files found.");
            }
            
            _knownDemFiles = new HashSet<string>(allDems);
        } catch (Exception ex) {
            Log($"SNAPSHOT [{label}] ERROR: {ex.Message}");
        }
    }

    private void PlayerCheckLoop()
    {
        if (IsRecording) return;

        try
        {
            var players = Utilities.GetPlayers();
            int humanCount = players.Count(p => p != null && p.IsValid && !p.IsBot && !p.IsHLTV);

            if (humanCount > 0)
            {
                Log($"DIAG: PlayerCheck found {humanCount} human(s). Starting recording...");
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

        string timestamp = DateTime.UtcNow.ToString("MMddyy_HHmmss");
        string mapName = Server.MapName;
        CurrentDemoName = $"{mapName}_{timestamp}";
        string demoFileName = $"{CurrentDemoName}.dem";

        HistoryTracker?.AddDemo(demoFileName, Config.ServerName);

        // Clear any stuck recording state
        Server.ExecuteCommand("tv_stoprecord");

        // Force GOTV settings again
        Server.ExecuteCommand("tv_enable 1");
        Server.ExecuteCommand("tv_delay 0");
        Server.ExecuteCommand("tv_transmitall 1");

        // Clear watcher list
        lock (_watcherLock) { _watcherCreatedFiles.Clear(); }

        SnapshotDemFiles("PRE-RECORD");

        // ---- TRY MULTIPLE tv_record APPROACHES ----

        // Approach 1: Simple name (what most plugins do)
        Log($"DIAG: [Approach 1] tv_record {CurrentDemoName}");
        Server.ExecuteCommand($"tv_record {CurrentDemoName}");

        IsRecording = true;
        Log($"Started recording demo: {demoFileName}");

        // After 5 seconds, check if it worked. If not, try more approaches.
        AddTimer(5.0f, () => {
            bool anyWatcherFiles;
            lock (_watcherLock) { anyWatcherFiles = _watcherCreatedFiles.Count > 0; }

            if (anyWatcherFiles)
            {
                string files;
                lock (_watcherLock) { files = string.Join(", ", _watcherCreatedFiles); }
                Log($"DIAG: [Approach 1] SUCCESS via watcher! Files: {files}");
                return;
            }

            Log("DIAG: [Approach 1] FAILED. No files detected by watchers. Stopping and trying approach 2...");
            Server.ExecuteCommand("tv_stoprecord");

            // Approach 2: Record with path relative to csgo/
            string relPath = $"../../csgo/{CurrentDemoName}_v2";
            Log($"DIAG: [Approach 2] tv_record {relPath}");
            Server.ExecuteCommand($"tv_record {relPath}");

            AddTimer(5.0f, () => {
                bool anyWatcher2;
                lock (_watcherLock) { anyWatcher2 = _watcherCreatedFiles.Count > 0; }

                if (anyWatcher2)
                {
                    string files2;
                    lock (_watcherLock) { files2 = string.Join(", ", _watcherCreatedFiles); }
                    Log($"DIAG: [Approach 2] SUCCESS! Files: {files2}");
                    return;
                }

                Log("DIAG: [Approach 2] FAILED. Trying approach 3...");
                Server.ExecuteCommand("tv_stoprecord");

                // Approach 3: Absolute path to csgo dir
                string csgoDir = Path.Combine(Server.GameDirectory, "csgo");
                string absPath = Path.Combine(csgoDir, $"{CurrentDemoName}_v3");
                Log($"DIAG: [Approach 3] tv_record {absPath}");
                Server.ExecuteCommand($"tv_record {absPath}");

                AddTimer(5.0f, () => {
                    bool anyWatcher3;
                    lock (_watcherLock) { anyWatcher3 = _watcherCreatedFiles.Count > 0; }

                    if (anyWatcher3)
                    {
                        string files3;
                        lock (_watcherLock) { files3 = string.Join(", ", _watcherCreatedFiles); }
                        Log($"DIAG: [Approach 3] SUCCESS! Files: {files3}");
                        return;
                    }

                    Log("DIAG: [Approach 3] FAILED. Trying approach 4...");
                    Server.ExecuteCommand("tv_stoprecord");

                    // Approach 4: Just a single word name, no special chars at all
                    string simpleName = "demobuddy_test";
                    Log($"DIAG: [Approach 4] tv_record {simpleName}");
                    Server.ExecuteCommand($"tv_record {simpleName}");

                    AddTimer(5.0f, () => {
                        bool anyWatcher4;
                        lock (_watcherLock) { anyWatcher4 = _watcherCreatedFiles.Count > 0; }

                        if (anyWatcher4)
                        {
                            string files4;
                            lock (_watcherLock) { files4 = string.Join(", ", _watcherCreatedFiles); }
                            Log($"DIAG: [Approach 4] SUCCESS! Files: {files4}");
                            return;
                        }

                        Log("DIAG: [Approach 4] FAILED. Trying approach 5 - tv_autorecord...");
                        Server.ExecuteCommand("tv_stoprecord");

                        // Approach 5: Let the engine auto-record
                        Server.ExecuteCommand("tv_autorecord 1");
                        Log("DIAG: [Approach 5] Set tv_autorecord 1. Checking in 10 seconds...");

                        AddTimer(10.0f, () => {
                            bool anyWatcher5;
                            lock (_watcherLock) { anyWatcher5 = _watcherCreatedFiles.Count > 0; }

                            if (anyWatcher5)
                            {
                                string files5;
                                lock (_watcherLock) { files5 = string.Join(", ", _watcherCreatedFiles); }
                                Log($"DIAG: [Approach 5] SUCCESS! tv_autorecord created: {files5}");
                            }
                            else
                            {
                                SnapshotDemFiles("ALL-APPROACHES-FAILED");
                                Log("DIAG: ALL 5 APPROACHES FAILED. tv_record is broken on this server instance.");
                                Log("DIAG: This is likely a hosting provider restriction or a CS2 engine bug.");
                                Log("DIAG: Try running 'tv_status' via RCON to see what the engine reports.");
                                Log("DIAG: Also check if your hosting provider allows GOTV recording.");

                                // Enumerate the csgo dir structure for clues
                                try {
                                    string csgo = Path.Combine(Server.GameDirectory, "csgo");
                                    if (Directory.Exists(csgo)) {
                                        var topItems = Directory.GetFileSystemEntries(csgo).Take(30);
                                        Log($"DIAG: /CS2/game/csgo/ contents (first 30): {string.Join(", ", topItems.Select(Path.GetFileName))}");
                                    }
                                    string cwd = Environment.CurrentDirectory;
                                    var cwdItems = Directory.GetFileSystemEntries(cwd).Take(20);
                                    Log($"DIAG: CWD contents (first 20): {string.Join(", ", cwdItems.Select(Path.GetFileName))}");
                                } catch (Exception ex) {
                                    Log($"DIAG: Dir enumeration error: {ex.Message}");
                                }
                            }
                        });
                    });
                });
            });
        });
    }

    private void OnMapStart(string mapName)
    {
        IsRecording = false;
        Log($"Map started: {mapName}");
        
        Server.ExecuteCommand("tv_enable 1");
        Server.ExecuteCommand("tv_delay 0");
        Server.ExecuteCommand("tv_transmitall 1");
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
        Log($"Stopped recording: {demoFileName}");

        // Capture watcher results on the main thread
        List<string> watcherFiles;
        lock (_watcherLock) { watcherFiles = new List<string>(_watcherCreatedFiles); }

        if (watcherFiles.Count > 0)
        {
            Log($"Watcher captured {watcherFiles.Count} file(s) during this session: {string.Join(", ", watcherFiles)}");
        }

        string gameDir = Server.GameDirectory;
        string parentDir = Directory.GetParent(gameDir)?.FullName ?? gameDir;

        Task.Run(async () =>
        {
            await Task.Delay(5000);

            // First check watcher results
            List<string> filesToUpload = new();
            lock (_watcherLock)
            {
                filesToUpload.AddRange(_watcherCreatedFiles.Where(File.Exists));
            }

            // Also do manual scan
            string[] scanDirs = new[] {
                gameDir,
                Path.Combine(gameDir, "csgo"),
                parentDir,
                Path.Combine(gameDir, "bin", "linuxsteamrt64"),
                Environment.CurrentDirectory
            };

            foreach (var dir in scanDirs.Distinct())
            {
                if (!Directory.Exists(dir)) continue;
                try {
                    foreach (var f in Directory.GetFiles(dir, "*.dem"))
                    {
                        if (!_knownDemFiles.Contains(f) && !filesToUpload.Contains(f))
                            filesToUpload.Add(f);
                    }
                } catch { }
            }

            // Deep recursive scan
            try {
                foreach (var f in Directory.GetFiles(parentDir, "*.dem", SearchOption.AllDirectories))
                {
                    if (!_knownDemFiles.Contains(f) && !filesToUpload.Contains(f))
                        filesToUpload.Add(f);
                }
            } catch { }

            if (filesToUpload.Count > 0)
            {
                Log($"Found {filesToUpload.Count} demo(s) to upload!");
                foreach (var filePath in filesToUpload)
                {
                    string fileName = Path.GetFileName(filePath);
                    try { Log($"Uploading: {filePath} ({new FileInfo(filePath).Length} bytes)"); } catch { }
                    await Task.Delay(1000);
                    UploadAndDeleteDemoRoutine(filePath, fileName);
                }
            }
            else
            {
                // Wait longer
                await Task.Delay(15000);

                lock (_watcherLock) {
                    filesToUpload.AddRange(_watcherCreatedFiles.Where(f => File.Exists(f) && !filesToUpload.Contains(f)));
                }

                if (filesToUpload.Count > 0)
                {
                    foreach (var filePath in filesToUpload)
                    {
                        string fileName = Path.GetFileName(filePath);
                        UploadAndDeleteDemoRoutine(filePath, fileName);
                    }
                }
                else
                {
                    Log($"ERROR: No demo files found after extended wait.");
                }
            }
        });
    }

    private void RunGarbageCollection()
    {
        Log("Running garbage collection...");
        string baseDir = Server.GameDirectory;
        string parentDir = Directory.GetParent(baseDir)?.FullName ?? baseDir;
        
        string[] possibleDirs = new[] {
            baseDir, parentDir,
            Path.Combine(baseDir, "csgo"),
            Path.Combine(baseDir, "bin", "linuxsteamrt64"),
            Environment.CurrentDirectory
        }.Distinct().ToArray();

        foreach (var dir in possibleDirs)
        {
            if (!Directory.Exists(dir)) continue;
            try {
                foreach (var filePath in Directory.GetFiles(dir, "*.dem"))
                {
                    string fileName = Path.GetFileName(filePath);
                    if (IsRecording && fileName == $"{CurrentDemoName}.dem") continue;

                    string? targetServer = HistoryTracker?.GetTargetServerName(fileName);
                    if (targetServer == null) {
                        Log($"GC: Deleting untracked {fileName}");
                        try { File.Delete(filePath); } catch { }
                    } else {
                        Log($"GC: Retrying upload for {fileName}");
                        UploadAndDeleteDemoRoutine(filePath, fileName, targetServer);
                    }
                }
            } catch { }
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
                _knownDemFiles.Remove(filePath);
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
