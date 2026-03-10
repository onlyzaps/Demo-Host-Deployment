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
    public override string ModuleVersion => "2.0.0";
    public override string ModuleAuthor => "GitHub Copilot";

    public CS2DemoBuddyConfig Config { get; set; } = new();
    private DemoHistoryTracker? HistoryTracker;
    private static readonly object _logLock = new object();

    private string CurrentDemoName = "";
    private bool IsRecording = false;
    private HashSet<string> _knownDemFiles = new();

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
        Log("===== CS2DemoBuddy v2.0.0 LOADING (Full Diagnostic Mode) =====");

        string configDir = Path.GetFullPath(Path.Combine(ModuleDirectory, "../../configs/plugins/CS2DemoBuddy"));
        if (!Directory.Exists(configDir)) Directory.CreateDirectory(configDir);
        HistoryTracker = new DemoHistoryTracker(configDir, Log);

        // --- DIAGNOSTIC: Log all paths the engine reports ---
        try {
            Log($"DIAG: Server.GameDirectory = {Server.GameDirectory}");
        } catch (Exception ex) {
            Log($"DIAG: Server.GameDirectory FAILED: {ex.Message}");
        }
        try {
            Log($"DIAG: Environment.CurrentDirectory = {Environment.CurrentDirectory}");
        } catch (Exception ex) {
            Log($"DIAG: Environment.CurrentDirectory FAILED: {ex.Message}");
        }
        try {
            Log($"DIAG: ModuleDirectory = {ModuleDirectory}");
        } catch (Exception ex) {
            Log($"DIAG: ModuleDirectory FAILED: {ex.Message}");
        }

        // --- DIAGNOSTIC: Test filesystem write access ---
        try {
            string testDir = Server.GameDirectory;
            string testFile = Path.Combine(testDir, "_cs2demobuddy_write_test.tmp");
            File.WriteAllText(testFile, "write test");
            File.Delete(testFile);
            Log($"DIAG: Filesystem write test PASSED in {testDir}");
        } catch (Exception ex) {
            Log($"DIAG: Filesystem write test FAILED: {ex.Message}");
        }

        // --- DIAGNOSTIC: Snapshot existing .dem files at boot ---
        SnapshotDemFiles("BOOT");

        // Force GOTV on with multiple approaches
        Server.ExecuteCommand("tv_enable 1");
        Server.ExecuteCommand("tv_autorecord 1");
        Log("DIAG: Sent tv_enable 1 and tv_autorecord 1");

        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);

        RegisterEventHandler<EventCsWinPanelMatch>((@event, info) => {
            StopAndUploadDemo();
            return HookResult.Continue;
        });

        AddTimer(10.0f, PlayerCheckLoop, TimerFlags.REPEAT);
        AddTimer(3600.0f, RunGarbageCollection, TimerFlags.REPEAT);

        Log("===== CS2DemoBuddy v2.0.0 LOADED =====");
    }

    private void SnapshotDemFiles(string label)
    {
        try {
            string gameDir = Server.GameDirectory;
            string parentDir = Directory.GetParent(gameDir)?.FullName ?? gameDir;
            
            var allDems = new List<string>();
            
            // Check multiple directories
            string[] checkDirs = new[] {
                gameDir,
                parentDir,
                Path.Combine(parentDir, "bin", "linuxsteamrt64"),
                Path.Combine(parentDir, "bin", "win64"),
                Environment.CurrentDirectory
            };

            foreach (var dir in checkDirs.Distinct())
            {
                if (!Directory.Exists(dir)) continue;
                try {
                    foreach (var f in Directory.GetFiles(dir, "*.dem"))
                        allDems.Add(f);
                } catch { }
            }
            
            // Also try recursive from parent
            try {
                foreach (var f in Directory.GetFiles(parentDir, "*.dem", SearchOption.AllDirectories))
                    if (!allDems.Contains(f)) allDems.Add(f);
            } catch { }

            if (allDems.Count > 0) {
                string list = string.Join(", ", allDems.Select(f => $"{f} ({new FileInfo(f).Length} bytes)"));
                Log($"SNAPSHOT [{label}]: Found {allDems.Count} .dem files: {list}");
            } else {
                Log($"SNAPSHOT [{label}]: ZERO .dem files found anywhere.");
            }
            
            _knownDemFiles = new HashSet<string>(allDems);
        } catch (Exception ex) {
            Log($"SNAPSHOT [{label}] ERROR: {ex.Message}");
        }
    }

    private List<string> FindNewDemFiles()
    {
        var newFiles = new List<string>();
        try {
            string gameDir = Server.GameDirectory;
            string parentDir = Directory.GetParent(gameDir)?.FullName ?? gameDir;
            
            string[] checkDirs = new[] {
                gameDir,
                parentDir,
                Path.Combine(parentDir, "bin", "linuxsteamrt64"),
                Path.Combine(parentDir, "bin", "win64"),
                Environment.CurrentDirectory
            };

            var currentDems = new HashSet<string>();
            
            foreach (var dir in checkDirs.Distinct())
            {
                if (!Directory.Exists(dir)) continue;
                try {
                    foreach (var f in Directory.GetFiles(dir, "*.dem"))
                        currentDems.Add(f);
                } catch { }
            }
            
            try {
                foreach (var f in Directory.GetFiles(parentDir, "*.dem", SearchOption.AllDirectories))
                    currentDems.Add(f);
            } catch { }

            foreach (var f in currentDems)
            {
                if (!_knownDemFiles.Contains(f))
                    newFiles.Add(f);
            }
        } catch { }
        return newFiles;
    }

    private void PlayerCheckLoop()
    {
        if (IsRecording) return;

        try
        {
            var players = Utilities.GetPlayers();
            int humanCount = players.Count(p => p != null && p.IsValid && !p.IsBot && !p.IsHLTV);
            bool gotvBotExists = players.Any(p => p != null && p.IsValid && p.IsHLTV);
            
            if (humanCount > 0 && !IsRecording)
            {
                Log($"DIAG: {humanCount} human(s) detected. GOTV bot present: {gotvBotExists}. Attempting to start recording...");
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

        string timestamp = DateTime.UtcNow.ToString("MM-dd-yy_HH-mm-ss");
        string mapName = Server.MapName;
        CurrentDemoName = $"{mapName}_{timestamp}";
        string demoFileName = $"{CurrentDemoName}.dem";

        HistoryTracker?.AddDemo(demoFileName, Config.ServerName);

        // Snapshot BEFORE recording attempt
        SnapshotDemFiles("PRE-RECORD");

        // Try multiple command variations to maximize chances
        Log($"DIAG: Attempting tv_record with name: {CurrentDemoName}");
        
        // Method 1: Direct command (no quotes)
        Server.ExecuteCommand($"tv_record {CurrentDemoName}");
        
        IsRecording = true;
        Log($"Started recording demo: {demoFileName}");

        // Verify recording actually started after 5 seconds
        AddTimer(5.0f, () => {
            var newFiles = FindNewDemFiles();
            if (newFiles.Count > 0) {
                Log($"DIAG: VERIFY SUCCESS! New .dem files appeared after tv_record: {string.Join(", ", newFiles)}");
            } else {
                Log("DIAG: VERIFY FAILED! No new .dem files appeared 5 seconds after tv_record.");
                Log("DIAG: Trying fallback - tv_record with Server.NextFrame...");
                
                // Fallback: try again via NextFrame to ensure it runs on the game thread
                Server.NextFrame(() => {
                    Server.ExecuteCommand("tv_enable 1");
                    Server.ExecuteCommand($"tv_record {CurrentDemoName}_retry");
                    Log($"DIAG: Sent retry tv_record via NextFrame: {CurrentDemoName}_retry");
                });
                
                // Check again after the retry
                AddTimer(5.0f, () => {
                    var retryFiles = FindNewDemFiles();
                    if (retryFiles.Count > 0) {
                        Log($"DIAG: RETRY SUCCESS! Files: {string.Join(", ", retryFiles)}");
                    } else {
                        Log("DIAG: RETRY ALSO FAILED. tv_record is completely non-functional.");
                        Log("DIAG: Listing all directories and their contents around GameDirectory for manual inspection...");
                        try {
                            string gd = Server.GameDirectory;
                            string pd = Directory.GetParent(gd)?.FullName ?? gd;
                            foreach (var dir in Directory.GetDirectories(pd)) {
                                try {
                                    int fileCount = Directory.GetFiles(dir).Length;
                                    Log($"DIAG: DIR {dir} ({fileCount} files)");
                                } catch { }
                            }
                        } catch (Exception ex) {
                            Log($"DIAG: Directory listing failed: {ex.Message}");
                        }
                    }
                });
            }
        });
    }

    private void OnMapStart(string mapName)
    {
        // Reset state but do NOT start recording - PlayerCheckLoop handles that
        IsRecording = false;
        Log($"Map started: {mapName}. Waiting for human players before recording.");
        
        // Re-force GOTV every map change
        Server.ExecuteCommand("tv_enable 1");
        Server.ExecuteCommand("tv_autorecord 1");
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
        Log($"Stopped recording: {demoFileName}. Scanning for files...");

        Task.Run(async () =>
        {
            await Task.Delay(5000); // Wait for engine to flush

            // Find ANY new .dem files that appeared since we started
            var newFiles = FindNewDemFiles();
            
            if (newFiles.Count > 0)
            {
                Log($"Found {newFiles.Count} new demo file(s) to upload!");
                foreach (var filePath in newFiles)
                {
                    string fileName = Path.GetFileName(filePath);
                    Log($"Uploading discovered file: {filePath} ({new FileInfo(filePath).Length} bytes)");
                    await Task.Delay(1000);
                    UploadAndDeleteDemoRoutine(filePath, fileName);
                }
            }
            else
            {
                // Extended search with longer wait
                Log("No new files found after 5s. Waiting 15 more seconds...");
                await Task.Delay(15000);
                
                newFiles = FindNewDemFiles();
                if (newFiles.Count > 0)
                {
                    Log($"Found {newFiles.Count} file(s) after extended wait!");
                    foreach (var filePath in newFiles)
                    {
                        string fileName = Path.GetFileName(filePath);
                        UploadAndDeleteDemoRoutine(filePath, fileName);
                    }
                }
                else
                {
                    SnapshotDemFiles("POST-RECORD-FAIL");
                    Log($"ERROR: No demo files created. tv_record is not working on this server.");
                }
            }
            
            // Update known files
            SnapshotDemFiles("POST-UPLOAD");
        });
    }

    private void RunGarbageCollection()
    {
        Log("Running garbage collection...");
        
        string baseDir = Server.GameDirectory;
        string parentDir = Directory.GetParent(baseDir)?.FullName ?? baseDir;
        
        string[] possibleDirs = new[] {
            baseDir, parentDir,
            Path.Combine(parentDir, "bin", "linuxsteamrt64"),
            Path.Combine(parentDir, "bin", "win64"),
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

            Log($"Uploading {fileName} ({new FileInfo(filePath).Length} bytes) to {Config.ApiUrl}...");
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
