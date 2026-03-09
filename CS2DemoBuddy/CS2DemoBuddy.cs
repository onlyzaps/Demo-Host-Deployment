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
    public override string ModuleVersion => "1.3.1";
    public override string ModuleAuthor => "GitHub Copilot";

    public CS2DemoBuddyConfig Config { get; set; } = new();
    private DemoHistoryTracker? HistoryTracker;
    private static readonly object _logLock = new object();

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

    private string CurrentDemoName = "";
    private bool IsRecording = false;

    public override void Load(bool hotReload)
    {
        Log("Plugin loading...");

        string configDir = Path.GetFullPath(Path.Combine(ModuleDirectory, "../../configs/plugins/CS2DemoBuddy"));
        if (!Directory.Exists(configDir)) Directory.CreateDirectory(configDir);
        HistoryTracker = new DemoHistoryTracker(configDir, Log);

        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);

        RegisterEventHandler<EventCsWinPanelMatch>((@event, info) => {
            StopAndUploadDemo();
            return HookResult.Continue;
        });

        Server.ExecuteCommand("tv_enable 1");
        Server.ExecuteCommand("tv_transmitall 1");

        AddTimer(3600.0f, RunGarbageCollection, TimerFlags.REPEAT);

        Log("Plugin completely loaded. Garbage collection initialized for 1 hour intervals.");
    }

    private void OnMapStart(string mapName)
    {
        if (IsRecording) return;

        string timestamp = DateTime.UtcNow.ToString("MM-dd-yy_HH-mm-ss");
        CurrentDemoName = $"{mapName}_{timestamp}";

        string demoFileName = $"{CurrentDemoName}.dem";
        HistoryTracker?.AddDemo(demoFileName, Config.ServerName);

        Server.ExecuteCommand($"tv_record {CurrentDemoName}");
        IsRecording = true;

        Log($"Started recording demo: {demoFileName}");
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
        Log($"Stopped recording. Awaiting disk flush before upload: {demoFileName}");

        string baseDir = Server.GameDirectory; 
        string parentDir = Directory.GetParent(baseDir)?.FullName ?? baseDir; 

        // Offload file finding and uploading to a separate task so we don't freeze the game tick
        Task.Run(async () =>
        {
            await Task.Delay(4000); // Initial grace period

            string[] searchPaths = new[]
            {
                Path.Combine(baseDir, demoFileName),
                Path.Combine(baseDir, "csgo", demoFileName),
                Path.Combine(parentDir, "csgo", demoFileName),
                Path.Combine(parentDir, "bin", "linuxsteamrt64", demoFileName),
                Path.Combine(parentDir, "bin", "win64", demoFileName)
            }.Distinct().ToArray();

            string? foundPath = null;

            // Give the engine up to 15 seconds to finish writing the demo flush to disk
            for (int i = 0; i < 5; i++)
            {
                foreach (var p in searchPaths)
                {
                    if (File.Exists(p) && new FileInfo(p).Length > 0)
                    {
                        foundPath = p;
                        break;
                    }
                }
                
                if (foundPath != null) break;
                await Task.Delay(3000);
            }

            if (foundPath != null)
            {
                Log($"Locating demo... success! Found demo file at: {foundPath}");
                UploadAndDeleteDemoRoutine(foundPath, demoFileName);
            }
            else
            {
                Log($"Error: Demo file not found after 15 seconds of waiting. Expected: {demoFileName}. File might be locked or directory is non-standard. The Garbage Collector will retry this in 60 minutes.");
            }
        });
    }

    private void RunGarbageCollection()
    {
        Log("Running scheduled garbage collection for demos...");
        
        string baseDir = Server.GameDirectory;
        string parentDir = Directory.GetParent(baseDir)?.FullName ?? baseDir;
        
        string[] possibleDirs = new[]
        {
            baseDir,
            Path.Combine(baseDir, "csgo"),
            Path.Combine(parentDir, "csgo"),
            Path.Combine(parentDir, "bin", "linuxsteamrt64"),
            Path.Combine(parentDir, "bin", "win64")
        }.Distinct().ToArray();

        int foundCount = 0;

        foreach (var dir in possibleDirs)
        {
            if (!Directory.Exists(dir)) continue;

            var demoFiles = Directory.GetFiles(dir, "*.dem");
            foreach (var filePath in demoFiles)
            {
                string fileName = Path.GetFileName(filePath);

                // Skip the currently actively recording demo
                if (IsRecording && fileName == $"{CurrentDemoName}.dem")
                    continue;

                foundCount++;

                string? targetServerName = HistoryTracker?.GetTargetServerName(fileName);
                if (targetServerName == null)
                {
                    Log($"GC: Deleting untracked demo file {fileName}");
                    try { File.Delete(filePath); } catch { }
                }
                else
                {
                    Log($"GC: Retrying failed upload for demo {fileName}");
                    UploadAndDeleteDemoRoutine(filePath, fileName, targetServerName);
                }
            }
        }
        
        Log($"Garbage collection finished scanning {possibleDirs.Length} paths. Processed {foundCount} stale files.");
    }

    private async void UploadAndDeleteDemoRoutine(string filePath, string fileName, string? serverNameFallback = null)
    {
        if (!File.Exists(filePath))
        {
            Log($"Upload Routine Error: Demo file not found at {filePath}");
            HistoryTracker?.RemoveDemo(fileName);
            return;
        }

        string targetServerName = serverNameFallback ?? Config.ServerName;

        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(10); // Allow time for large demo uploads
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
                Log($"Failed to upload {fileName}. Status code: {response.StatusCode} | {respError}");
            }
        }
        catch (Exception ex)
        {
            Log($"Exception during upload: {ex.Message}");
        }
    }
}
