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
    public override string ModuleVersion => "5.0.0";
    public override string ModuleAuthor => "VinSix";

    public CS2DemoBuddyConfig Config { get; set; } = new();
    private DemoHistoryTracker? HistoryTracker;
    private static readonly object _logLock = new object();

    private string StorageServerName => $"DBS_{Config.ServerName}";

    // ── Recording state ────────────────────────────────────────────────
    private string _currentDemoName = "";
    private bool _isRecording = false;
    private bool _isRecordingForbidden = true;
    private bool _isChangingLevel = false;
    private bool _matchEndedAwaitingMapChange = false;
    private string _matchFolder = "";
    private string _matchDate = "";
    private string _demoDir = "";          // resolved once at load

    // ── Timers ─────────────────────────────────────────────────────────

    private CounterStrikeSharp.API.Modules.Timers.Timer? _playerMonitorTimer;
    private DateTime? _emptyServerSince;

    // ── Upload / GC plumbing ───────────────────────────────────────────
    private const long MinDemoSizeBytes = 1_000_000; // 1 MB
    private readonly HashSet<string> _pendingFiles = new();
    private readonly object _pendingLock = new();
    private CancellationTokenSource? _inventoryCts;
    private string _gameDirectory = "";

    // ═══════════════════════════════════════════════════════════════════
    //  Logging
    // ═══════════════════════════════════════════════════════════════════

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

    // ═══════════════════════════════════════════════════════════════════
    //  Config
    // ═══════════════════════════════════════════════════════════════════

    public void OnConfigParsed(CS2DemoBuddyConfig config)
    {
        if (config.ServerName.Contains(" "))
            config.ServerName = config.ServerName.Replace(" ", "_");
        Config = config;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Load / Unload
    // ═══════════════════════════════════════════════════════════════════

    public override void Load(bool hotReload)
    {
        Log($"===== CS2DemoBuddy v{ModuleVersion} LOADING =====");

        _gameDirectory = Server.GameDirectory;
        _demoDir = Path.Combine(_gameDirectory, "csgo");

        string configDir = Path.GetFullPath(Path.Combine(ModuleDirectory, "../../configs/plugins/CS2DemoBuddy"));
        if (!Directory.Exists(configDir)) Directory.CreateDirectory(configDir);
        HistoryTracker = new DemoHistoryTracker(configDir, Log);

        // Clear any stuck recording from a previous session / crash
        Server.ExecuteCommand("tv_stoprecord -instance 1");



        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        RegisterListener<Listeners.OnServerHibernationUpdate>(OnServerHibernationUpdate);

        // ── Changelevel interception: stop recording before map changes ──
        AddCommandListener("changelevel", CommandListener_Changelevel, HookMode.Pre);
        AddCommandListener("ds_workshop_changelevel", CommandListener_Changelevel, HookMode.Pre);
        AddCommandListener("map", CommandListener_Changelevel, HookMode.Pre);
        AddCommandListener("host_workshop_map", CommandListener_Changelevel, HookMode.Pre);

        // ── Recording trigger: first non-warmup round with humans ──
        RegisterEventHandler<EventRoundStart>((@event, info) =>
        {
            try
            {
                Log($"[DEBUG] EventRoundStart fired — _isChangingLevel={_isChangingLevel} _isRecording={_isRecording} _isRecordingForbidden={_isRecordingForbidden} _matchEndedAwaitingMapChange={_matchEndedAwaitingMapChange}");
                if (_isChangingLevel || _matchEndedAwaitingMapChange) return HookResult.Continue;

                _isRecordingForbidden = false;
                if (!_isRecording)
                {
                    if (IsWarmup())
                    {
                        Log("RoundStart during warmup — skipping.");
                        return HookResult.Continue;
                    }
                    int humans = CountHumans();
                    if (humans > 0)
                    {
                        Log($"RoundStart with {humans} human(s) — starting recording.");
                        StartRecording();
                    }
                }
            }
            catch (Exception ex) { Log($"EventRoundStart exception: {ex.Message}"); }
            return HookResult.Continue;
        });

        // ── Match ended: stop recording ──
        RegisterEventHandler<EventCsWinPanelMatch>((@event, info) =>
        {
            try
            {
                Log($"[DEBUG] EventCsWinPanelMatch fired — _isRecording={_isRecording} _isChangingLevel={_isChangingLevel} _isRecordingForbidden={_isRecordingForbidden} _currentDemoName={_currentDemoName}");
                Log("Match ended — stopping recording.");
                _isRecordingForbidden = true;
                _matchEndedAwaitingMapChange = true;
                StopAndUploadDemo("Match ended");
                Log($"[DEBUG] EventCsWinPanelMatch handler done — _isRecording={_isRecording} _isRecordingForbidden={_isRecordingForbidden}");
            }
            catch (Exception ex) { Log($"EventCsWinPanelMatch exception: {ex.Message}"); }
            return HookResult.Continue;
        });

        // ── New match: allow recording again ──
        RegisterEventHandler<EventBeginNewMatch>((@event, info) =>
        {
            try
            {
                Log($"[DEBUG] EventBeginNewMatch fired — _isChangingLevel={_isChangingLevel} _isRecording={_isRecording} _isRecordingForbidden={_isRecordingForbidden} _matchEndedAwaitingMapChange={_matchEndedAwaitingMapChange}");
                if (_isChangingLevel || _matchEndedAwaitingMapChange) return HookResult.Continue;

                Log("New match started.");
                _isRecordingForbidden = false;
                if (CountHumans() > 0 && !IsWarmup())
                    StartRecording();
            }
            catch (Exception ex) { Log($"EventBeginNewMatch exception: {ex.Message}"); }
            return HookResult.Continue;
        });

        // ── Match end restart: block recording during restart ──
        RegisterEventHandler<EventCsMatchEndRestart>((@event, info) =>
        {
            try
            {
                Log($"[DEBUG] EventCsMatchEndRestart fired — _isRecording={_isRecording} _isChangingLevel={_isChangingLevel} _isRecordingForbidden={_isRecordingForbidden}");
                Log("Match end restart.");
                _isRecordingForbidden = true;
            }
            catch (Exception ex) { Log($"EventCsMatchEndRestart exception: {ex.Message}"); }
            return HookResult.Continue;
        });

        // ── Resume after empty-server stop / player connect ──
        RegisterEventHandler<EventPlayerConnectFull>((@event, info) =>
        {
            try
            {
                Log($"[DEBUG] EventPlayerConnectFull fired — _isChangingLevel={_isChangingLevel} _isRecordingForbidden={_isRecordingForbidden} _isRecording={_isRecording} _matchEndedAwaitingMapChange={_matchEndedAwaitingMapChange}");
                if (_isChangingLevel || _isRecordingForbidden || _matchEndedAwaitingMapChange) return HookResult.Continue;

                var player = @event.Userid;
                if (player == null || !player.IsValid || player.IsBot || player.IsHLTV)
                    return HookResult.Continue;

                if (!_isRecording)
                {
                    int humans = CountHumans();
                    bool warmup = IsWarmup();
                    Log($"[DEBUG] PlayerConnectFull — humans={humans} warmup={warmup}");
                    if (humans > 0 && !warmup)
                    {
                        Log($"Player connected ({humans} human(s)) — starting recording.");
                        StartRecording();
                    }
                }
            }
            catch (Exception ex) { Log($"EventPlayerConnectFull exception: {ex.Message}"); }
            return HookResult.Continue;
        });

        // ── Player disconnect: stop if no humans left ──
        RegisterEventHandler<EventPlayerDisconnect>((@event, info) =>
        {
            try
            {
                Log($"[DEBUG] EventPlayerDisconnect fired — _isRecording={_isRecording} _isChangingLevel={_isChangingLevel} _isRecordingForbidden={_isRecordingForbidden} _matchEndedAwaitingMapChange={_matchEndedAwaitingMapChange}");

                // Check plugin state FIRST — during map shutdown the engine fires
                // disconnect events while player entities are being torn down.
                // Accessing @event.Userid properties on freed native memory segfaults.
                if (!_isRecording || _isChangingLevel || _isRecordingForbidden || _matchEndedAwaitingMapChange)
                {
                    Log($"[DEBUG] EventPlayerDisconnect — early exit (state guard)");
                    return HookResult.Continue;
                }

                // Wrap player entity access in its own try-catch — the native
                // pointer behind @event.Userid can be freed during engine teardown
                // even before our state flags are set.
                bool isHuman = false;
                try
                {
                    var player = @event.Userid;
                    bool isNull = player == null;
                    bool isValid = !isNull && player!.IsValid;
                    bool isBot = isValid && player!.IsBot;
                    bool isHltv = isValid && player!.IsHLTV;
                    Log($"[DEBUG] EventPlayerDisconnect — player null={isNull} valid={isValid} bot={isBot} hltv={isHltv}");
                    isHuman = !isNull && isValid && !isBot && !isHltv;
                }
                catch (Exception ex)
                {
                    Log($"[DEBUG] EventPlayerDisconnect — exception reading player entity (engine teardown?): {ex.Message}");
                    return HookResult.Continue;
                }

                if (!isHuman) return HookResult.Continue;

                Log($"[DEBUG] EventPlayerDisconnect — scheduling NextFrame check for human player");
                Server.NextFrame(() =>
                {
                    try
                    {
                        Log($"[DEBUG] EventPlayerDisconnect NextFrame — _isChangingLevel={_isChangingLevel} _isRecordingForbidden={_isRecordingForbidden} _isRecording={_isRecording} _matchEndedAwaitingMapChange={_matchEndedAwaitingMapChange}");
                        if (_isChangingLevel || _isRecordingForbidden || !_isRecording || _matchEndedAwaitingMapChange)
                        {
                            Log($"[DEBUG] EventPlayerDisconnect NextFrame — early exit (state guard)");
                            return;
                        }

                        int humans = CountHumans();
                        Log($"[DEBUG] EventPlayerDisconnect NextFrame — humans={humans}");
                        if (humans == 0)
                        {
                            Log("All players disconnected — stopping recording.");
                            StopAndUploadDemo("All players disconnected");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log($"PlayerDisconnect NextFrame error: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Log($"EventPlayerDisconnect handler exception: {ex.Message}");
            }
            return HookResult.Continue;
        });

        // ── Hot reload: pick up mid-match ──
        if (hotReload)
        {
            AddTimer(1.0f, () =>
            {
                _isRecordingForbidden = false;
                if (!_isRecording && !IsWarmup() && CountHumans() > 0)
                {
                    Log($"Hot reload — starting recording.");
                    StartRecording();
                }
            });
        }

        // ── GC every 45 minutes ──
        AddTimer(2700.0f, RunGarbageCollection, TimerFlags.REPEAT);

        // ── Periodic inventory reporting ──
        _inventoryCts = new CancellationTokenSource();
        _ = RunInventoryLoop(_inventoryCts.Token);

        Log($"===== CS2DemoBuddy v{ModuleVersion} LOADED =====");
    }

    public override void Unload(bool hotReload)
    {
        Log($"===== CS2DemoBuddy v{ModuleVersion} UNLOADING =====");

        if (_isRecording)
        {
            Server.ExecuteCommand("tv_stoprecord -instance 1");
            _isRecording = false;
        }

        _inventoryCts?.Cancel();
        _inventoryCts?.Dispose();
        _inventoryCts = null;

        KillTimers();

        _currentDemoName = "";
        HistoryTracker = null;

        Log($"===== CS2DemoBuddy v{ModuleVersion} UNLOADED =====");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Map lifecycle
    // ═══════════════════════════════════════════════════════════════════

    private void OnMapStart(string mapName)
    {
        try
        {
            Log($"[DEBUG] OnMapStart({mapName}) — BEFORE reset: _isRecording={_isRecording} _isChangingLevel={_isChangingLevel} _isRecordingForbidden={_isRecordingForbidden} _matchEndedAwaitingMapChange={_matchEndedAwaitingMapChange} _currentDemoName={_currentDemoName}");
            // Reset state — OnMapEnd already stopped recording, but this is
            // a safety net in case OnMapEnd didn't fire.
            _isRecording = false;
            _isRecordingForbidden = false;
            _isChangingLevel = false;
            _matchEndedAwaitingMapChange = false;
            _matchFolder = "";
            _matchDate = "";
            KillTimers();

            Log($"Map started: {mapName}");
        }
        catch (Exception ex) { Log($"OnMapStart exception: {ex.Message}"); }
    }

    private void OnMapEnd()
    {
        try
        {
            Log($"[DEBUG] OnMapEnd fired — _isRecording={_isRecording} _isChangingLevel={_isChangingLevel} _isRecordingForbidden={_isRecordingForbidden} _currentDemoName={_currentDemoName}");
            _isChangingLevel = true;
            _isRecordingForbidden = true;
            _matchEndedAwaitingMapChange = true;
            // Skip tv_stoprecord — the engine is already shutting down HLTV
            // (CHLTVServer::Shutdown). Issuing the command here can crash.
            StopAndUploadDemo("Map ended", skipStopCommand: true);
            Log($"[DEBUG] OnMapEnd done — _isRecording={_isRecording}");
        }
        catch (Exception ex) { Log($"OnMapEnd exception: {ex.Message}"); }
    }

    private void OnServerHibernationUpdate(bool isHibernating)
    {
        try
        {
            Log($"[DEBUG] OnServerHibernationUpdate({isHibernating}) — _isRecording={_isRecording} _isChangingLevel={_isChangingLevel} _isRecordingForbidden={_isRecordingForbidden}");
            Log($"Server hibernation update: {(isHibernating ? "started" : "ended")}");
            if (isHibernating)
            {
                _isRecordingForbidden = true;
                StopAndUploadDemo("Server hibernating");
            }
            else
            {
                _isRecordingForbidden = false;
            }
        }
        catch (Exception ex) { Log($"OnServerHibernationUpdate exception: {ex.Message}"); }
    }

    // Allowed changelevel commands — reject anything else to prevent command injection
    private static readonly HashSet<string> AllowedChangelevelCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "changelevel", "ds_workshop_changelevel", "map", "host_workshop_map"
    };

    private HookResult CommandListener_Changelevel(CCSPlayerController? player, CommandInfo commandInfo)
    {
        try
        {
            Log($"[DEBUG] CommandListener_Changelevel — _isChangingLevel={_isChangingLevel} _isRecording={_isRecording} argCount={commandInfo.ArgCount}");
            if (_isChangingLevel)
            {
                Log($"[DEBUG] CommandListener_Changelevel — already changing level, passing through");
                return HookResult.Continue;
            }

            if (_isRecording && commandInfo.ArgCount >= 2)
            {
                string command = commandInfo.GetArg(0);
                string map = commandInfo.GetArg(1);

                // Validate the command is one we expect
                if (!AllowedChangelevelCommands.Contains(command))
                {
                    Log($"Changelevel interceptor: unexpected command '{command}' — ignoring.");
                    return HookResult.Continue;
                }

                // Sanitize the map name — allow only alphanumeric, underscores, hyphens, slashes, dots
                if (!System.Text.RegularExpressions.Regex.IsMatch(map, @"^[\w\-./]+$"))
                {
                    Log($"Changelevel interceptor: invalid map name '{map}' — ignoring.");
                    return HookResult.Continue;
                }

                Log($"Intercepted changelevel: {command} {map}");
                _isRecordingForbidden = true;
                _isChangingLevel = true;
                _matchEndedAwaitingMapChange = true;
                StopAndUploadDemo("Changelevel");

                // Delay the actual changelevel so the recording flushes cleanly
                AddTimer(3.0f, () =>
                {
                    Server.ExecuteCommand($"{command} {map}");
                });
                return HookResult.Stop;
            }
        }
        catch (Exception ex) { Log($"CommandListener_Changelevel exception: {ex.Message}"); }
        return HookResult.Continue;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Recording — start / stop
    // ═══════════════════════════════════════════════════════════════════

    private void StartRecording()
    {
        if (_isRecording || _isRecordingForbidden) return;

        string mapName = Server.MapName;
        string ts = DateTime.UtcNow.ToString("HHmmss");
        _matchFolder = $"{mapName}-{ts}";
        _matchDate = DateTime.UtcNow.ToString("yyyy-MM-dd");
        _currentDemoName = $"{mapName}_{ts}";

        string demoFileName = $"{_currentDemoName}.dem";
        HistoryTracker?.AddDemo(demoFileName, StorageServerName, _matchFolder, _matchDate);

        _isRecording = true;

        // Frame 1: Enable HLTV, set quality/voice cvars
        Server.NextFrame(() =>
        {
            Server.ExecuteCommand("tv_enable 1");
            Server.ExecuteCommand("tv_record_immediate 1");
            Server.ExecuteCommand("tv_snapshotrate 64");
            Server.ExecuteCommand("tv_transmitall 1");
            Server.ExecuteCommand("tv_relayvoice 1");

            // Frame 2: Actually start recording (engine needs a tick to process HLTV enable)
            Server.NextFrame(() =>
            {
                Server.ExecuteCommand($"tv_record {_currentDemoName} -instance 1");
                Log($"▶ Recording started: {demoFileName}  (folder: {_matchFolder})");
            });
        });

        // ── Player monitor: stop after 5 min with 0 humans ──
        KillTimers();
        _emptyServerSince = null;
        _playerMonitorTimer = AddTimer(30.0f, () =>
        {
            try
            {
                if (!_isRecording || _isChangingLevel || _isRecordingForbidden || _matchEndedAwaitingMapChange) return;
                int humans = CountHumans();
                if (humans == 0)
                {
                    if (_emptyServerSince == null)
                    {
                        _emptyServerSince = DateTime.UtcNow;
                        Log("0 players — 5-minute empty-server countdown started.");
                    }
                    else if ((DateTime.UtcNow - _emptyServerSince.Value).TotalSeconds >= 300)
                    {
                        Log("Server empty 5+ min — stopping recording.");
                        StopAndUploadDemo("Empty server");
                    }
                }
                else if (_emptyServerSince != null)
                {
                    Log($"Player returned ({humans}) — countdown cancelled.");
                    _emptyServerSince = null;
                }
            }
            catch (Exception ex) { Log($"Player monitor timer exception: {ex.Message}"); }
        }, TimerFlags.REPEAT);
    }

    private void StopAndUploadDemo(string reason, bool skipStopCommand = false)
    {
        Log($"[DEBUG] StopAndUploadDemo('{reason}', skipStop={skipStopCommand}) — _isRecording={_isRecording} _isChangingLevel={_isChangingLevel} _isRecordingForbidden={_isRecordingForbidden} _currentDemoName={_currentDemoName}");
        if (!_isRecording)
        {
            Log($"[DEBUG] StopAndUploadDemo — not recording, returning early");
            return;
        }

        KillTimers();

        if (!skipStopCommand)
        {
            try
            {
                Log($"[DEBUG] StopAndUploadDemo — executing tv_stoprecord");
                Server.ExecuteCommand("tv_stoprecord -instance 1");
            }
            catch (Exception ex)
            {
                Log($"tv_stoprecord error during '{reason}': {ex.Message}");
            }
        }
        else
        {
            Log($"[DEBUG] StopAndUploadDemo — skipping tv_stoprecord (engine teardown)");
        }
        _isRecording = false;

        string demoFileName = $"{_currentDemoName}.dem";
        string stoppedFolder = _matchFolder;
        string stoppedDate = _matchDate;
        Log($"■ Recording stopped ({reason}): {demoFileName}");

        lock (_pendingLock) { _pendingFiles.Add(demoFileName); }

        string gameDir = _gameDirectory;
        string demoDir = _demoDir;

        Task.Run(async () =>
        {
            try
            {
                // CS2 flushes the .dem file when tv_stoprecord runs (or on map change).
                // Give the engine a moment, then look for it.
                await Task.Delay(10000);

                string? path = FindDemoFile(demoFileName, demoDir, gameDir);

                if (path == null)
                {
                    Log($"Demo not found after 10 s, waiting 30 s more...");
                    await Task.Delay(30000);
                    path = FindDemoFile(demoFileName, demoDir, gameDir);
                }

                if (path == null)
                {
                    Log($"Demo not found after 40 s, waiting 60 s more...");
                    await Task.Delay(60000);
                    path = FindDemoFile(demoFileName, demoDir, gameDir);
                }

                if (path == null)
                {
                    Log($"Could not find {demoFileName} after extended wait — GC will retry.");
                    return;
                }

                path = await WaitForFileStable(path);
                if (path == null)
                {
                    lock (_pendingLock) { _pendingFiles.Remove(demoFileName); }
                    return;
                }

                long finalSize = new FileInfo(path).Length;
                Log($"Demo on disk: {demoFileName} = {finalSize / 1024}KB ({finalSize / (1024 * 1024.0):F1}MB)");

                if (finalSize < MinDemoSizeBytes)
                {
                    Log($"Skipping {demoFileName} — too small. GC will clean up.");
                    HistoryTracker?.RemoveDemo(demoFileName);
                    lock (_pendingLock) { _pendingFiles.Remove(demoFileName); }
                    return;
                }

                await UploadDemoRoutine(path, demoFileName, null, stoppedFolder, stoppedDate);
                lock (_pendingLock) { _pendingFiles.Remove(demoFileName); }
            }
            catch (Exception ex)
            {
                Log($"Background upload task error for {demoFileName}: {ex.Message}");
                lock (_pendingLock) { _pendingFiles.Remove(demoFileName); }
            }
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════

    private void KillTimers()
    {
        _playerMonitorTimer?.Kill();
        _playerMonitorTimer = null;
        _emptyServerSince = null;
    }

    private int CountHumans()
    {
        try
        {
            return Utilities.GetPlayers()
                .Count(p => p != null && p.IsValid && !p.IsBot && !p.IsHLTV);
        }
        catch { return 0; }
    }

    private bool IsWarmup()
    {
        try
        {
            var rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
                .FirstOrDefault()?.GameRules;
            return rules?.WarmupPeriod ?? false;
        }
        catch { return false; }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Demo file discovery
    // ═══════════════════════════════════════════════════════════════════

    private string? FindDemoFile(string demoName, string demoDir, string gameDir)
    {
        // Check the two most likely directories
        foreach (var dir in new[] { demoDir, gameDir }.Distinct())
        {
            string candidate = Path.Combine(dir, demoName);
            if (File.Exists(candidate))
            {
                Log($"Found {demoName} in {dir}");
                return candidate;
            }
        }

        // Recursive fallback in csgo/
        try
        {
            var matches = Directory.GetFiles(demoDir, demoName, SearchOption.AllDirectories);
            if (matches.Length > 0)
            {
                Log($"Found {demoName} via recursive search: {matches[0]}");
                return matches[0];
            }
        }
        catch { }

        // Log what .dem files DO exist for troubleshooting
        foreach (var dir in new[] { demoDir, gameDir }.Distinct())
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                var files = Directory.GetFiles(dir, "*.dem");
                if (files.Length > 0)
                    Log($"  [{dir}]: {string.Join(", ", files.Select(Path.GetFileName).Take(10))}");
            }
            catch { }
        }

        return null;
    }

    private async Task<string?> WaitForFileStable(string filePath)
    {
        const int maxAttempts = 24; // 2 minutes
        long lastSize = -1;
        int stableCount = 0;

        for (int i = 0; i < maxAttempts; i++)
        {
            await Task.Delay(5000);
            if (!File.Exists(filePath))
            {
                Log($"File disappeared: {Path.GetFileName(filePath)}");
                return null;
            }

            try
            {
                long sz = new FileInfo(filePath).Length;
                if (sz == lastSize && sz > 0)
                {
                    if (++stableCount >= 2)
                    {
                        Log($"File stable at {sz / (1024 * 1024.0):F1}MB after {(i + 1) * 5}s.");
                        return filePath;
                    }
                }
                else { stableCount = 0; }
                lastSize = sz;
            }
            catch { stableCount = 0; }
        }

        Log($"File did not stabilize after 2 min — uploading anyway ({lastSize / (1024 * 1024.0):F1}MB).");
        return File.Exists(filePath) ? filePath : null;
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Garbage collection
    // ═══════════════════════════════════════════════════════════════════

    private void RunGarbageCollection()
    {
        Log("Running garbage collection...");
        string[] dirs = new[] { _demoDir, _gameDirectory }.Distinct().ToArray();

        HashSet<string> pending;
        lock (_pendingLock) { pending = new HashSet<string>(_pendingFiles); }

        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var filePath in Directory.GetFiles(dir, "*.dem"))
                {
                    string fileName = Path.GetFileName(filePath);
                    if (_isRecording && fileName == $"{_currentDemoName}.dem") continue;
                    if (pending.Contains(fileName)) continue;

                    var (srv, folder, date) = HistoryTracker?.GetDemoInfo(fileName) ?? (null, null, null);
                    if (srv == null)
                    {
                        Log($"GC: Deleting untracked {fileName}");
                        try { File.Delete(filePath); } catch { }
                    }
                    else
                    {
                        Log($"GC: Retrying upload for {fileName}");
                        lock (_pendingLock) { _pendingFiles.Add(fileName); }
                        _ = RetryUploadAndRelease(filePath, fileName, srv, folder, date);
                    }
                }
            }
            catch { }
        }
    }

    private async Task RetryUploadAndRelease(string filePath, string fileName,
        string targetServer, string? matchFolder, string? matchDate)
    {
        try { await UploadDemoRoutine(filePath, fileName, targetServer, matchFolder, matchDate); }
        catch (Exception ex) { Log($"GC retry upload error for {fileName}: {ex.Message}"); }
        finally { lock (_pendingLock) { _pendingFiles.Remove(fileName); } }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Inventory reporting
    // ═══════════════════════════════════════════════════════════════════

    private async Task RunInventoryLoop(CancellationToken ct)
    {
        try
        {
            await Task.Delay(15000, ct);

            while (!ct.IsCancellationRequested)
            {
                try { await ReportSourceFiles(); }
                catch (Exception ex) { Log($"Inventory error: {ex.Message}"); }

                await Task.Delay(60000, ct);
            }
        }
        catch (TaskCanceledException) { }
        catch (Exception ex) { Log($"Inventory loop fatal: {ex.Message}"); }
    }

    private async Task ReportSourceFiles()
    {
        string[] dirs = new[] { _demoDir, _gameDirectory }.Distinct().ToArray();
        var files = new List<object>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (var fp in Directory.GetFiles(dir, "*.dem"))
                {
                    string fn = Path.GetFileName(fp);
                    if (!seen.Add(fn)) continue;
                    try
                    {
                        var info = new FileInfo(fp);
                        files.Add(new { name = fn, sizeBytes = info.Length, modified = info.LastWriteTimeUtc.ToString("o") });
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

    // ═══════════════════════════════════════════════════════════════════
    //  Upload
    // ═══════════════════════════════════════════════════════════════════

    private async Task UploadDemoRoutine(string filePath, string fileName,
        string? serverNameFallback = null, string? matchFolder = null, string? matchDate = null)
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
                Log($"Uploaded {fileName} successfully.");
                HistoryTracker?.RemoveDemo(fileName);
                try
                {
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                        Log($"Deleted local demo: {fileName}");
                    }
                }
                catch (Exception delEx)
                {
                    Log($"Failed to delete local demo {fileName}: {delEx.Message}");
                }
            }
            else
            {
                string err = await response.Content.ReadAsStringAsync();
                Log($"Upload failed for {fileName}: {response.StatusCode} | {err}");
            }
        }
        catch (Exception ex)
        {
            Log($"Upload exception for {fileName}: {ex.Message}");
        }
    }
}
