using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using System.Net.Http.Headers;
using System.Text.Json.Serialization;

namespace CS2DemoBuddy;

public class CS2DemoBuddyConfig : BasePluginConfig
{
    // IMPORTANT: No spaces are allowed in the server name. Please use underscores instead of spaces, e.g. "My_Awesome_Server"
    [JsonPropertyName("ServerName")]
    public string ServerName { get; set; } = "My_Server"; 

    [JsonPropertyName("ApiUrl")]
    public string ApiUrl { get; set; } = "http://YOUR_LINUX_SERVER_IP:8080/upload";

    [JsonPropertyName("ApiSecretKey")]
    public string ApiSecretKey { get; set; } = "";
}

public class CS2DemoBuddyPlugin : BasePlugin, IPluginConfig<CS2DemoBuddyConfig>
{
    public override string ModuleName => "CS2DemoBuddy";
    public override string ModuleVersion => "1.1.0";
    public override string ModuleAuthor => "GitHub Copilot";

    public CS2DemoBuddyConfig Config { get; set; } = new();

    public void OnConfigParsed(CS2DemoBuddyConfig config)
    {
        // Enforce no spaces rule
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
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        
        // Handle immediate game state transitions or vote plugin map changes
        RegisterEventHandler<EventCsWinPanelMatch>((@event, info) => {
            StopAndUploadDemo();
            return HookResult.Continue;
        });

        // Ensure voice and text are recorded via SourceTV configs
        Server.ExecuteCommand("tv_enable 1");
        Server.ExecuteCommand("tv_transmitall 1");
    }

    private void OnMapStart(string mapName)
    {
        if (IsRecording) return;

        string timestamp = DateTime.UtcNow.ToString("MM-dd-yy_HH-mm-ss");
        CurrentDemoName = $"{mapName}_{timestamp}";
        Server.ExecuteCommand($"tv_record {CurrentDemoName}");
        IsRecording = true;
        
        Console.WriteLine($"[CS2DemoBuddy] Started recording demo: {CurrentDemoName}.dem");
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
        string filePath = Path.Combine(Server.GameDirectory, "csgo", demoFileName); // Path mostly standard for CS2 root

        Console.WriteLine($"[CS2DemoBuddy] Stopped recording. Preparing to upload: {demoFileName}");

        // Use a timer to wait briefly to make sure the server finishes writing the file to disk
        AddTimer(3.0f, () => {
            UploadAndDeleteDemoRoutine(filePath, demoFileName);
        });
    }

    private async void UploadAndDeleteDemoRoutine(string filePath, string fileName)
    {
        if (!File.Exists(filePath))
        {
            Console.WriteLine($"[CS2DemoBuddy] Error: Demo file not found at {filePath}");
            return;
        }

        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("x-api-key", Config.ApiSecretKey);
            using var form = new MultipartFormDataContent();
            
            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            using var streamContent = new StreamContent(fileStream);
            
            // Add server name BEFORE the file so the server's multer can read it during file save routing
            form.Add(new StringContent(Config.ServerName), "serverName");
            
            streamContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
            form.Add(streamContent, "demo", fileName);

            Console.WriteLine($"[CS2DemoBuddy] Uploading {fileName} to {Config.ApiUrl}...");
            var response = await client.PostAsync(Config.ApiUrl, form);
            
            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[CS2DemoBuddy] Successfully uploaded {fileName}. Deleting local file.");
                fileStream.Close();
                File.Delete(filePath);
            }
            else
            {
                Console.WriteLine($"[CS2DemoBuddy] Failed to upload {fileName}. Status code: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CS2DemoBuddy] Exception during upload: {ex.Message}");
        }
    }
}
