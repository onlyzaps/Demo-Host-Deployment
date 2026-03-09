using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using System.Net.Http.Headers;

namespace CS2MapBuddy;

public class CS2MapBuddyPlugin : BasePlugin
{
    public override string ModuleName => "CS2MapBuddy";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "GitHub Copilot";

    private string CurrentDemoName = "";
    private bool IsRecording = false;
    private readonly string ApiUrl = "http://YOUR_LINUX_SERVER_IP:3000/upload"; // Change this
    
    public override void Load(bool hotReload)
    {
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        
        // Handle immediate game state transitions or vote plugin map changes
        RegisterEventHandler<EventCsWinPanelMatch>((@event, info) => {
            StopAndUploadDemo();
            return HookResult.Continue;
        });
    }

    private void OnMapStart(string mapName)
    {
        if (IsRecording) return;

        string timestamp = DateTime.UtcNow.ToString("MM-dd-yy_HH-mm-ss");
        CurrentDemoName = $"{mapName}_{timestamp}";
        Server.ExecuteCommand($"tv_record {CurrentDemoName}");
        IsRecording = true;
        
        Console.WriteLine($"[CS2MapBuddy] Started recording demo: {CurrentDemoName}.dem");
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

        Console.WriteLine($"[CS2MapBuddy] Stopped recording. Preparing to upload: {demoFileName}");

        // Use a timer to wait briefly to make sure the server finishes writing the file to disk
        AddTimer(3.0f, () => {
            UploadAndDeleteDemoRoutine(filePath, demoFileName);
        });
    }

    private async void UploadAndDeleteDemoRoutine(string filePath, string fileName)
    {
        if (!File.Exists(filePath))
        {
            Console.WriteLine($"[CS2MapBuddy] Error: Demo file not found at {filePath}");
            return;
        }

        try
        {
            using var client = new HttpClient();
            using var form = new MultipartFormDataContent();
            
            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            using var streamContent = new StreamContent(fileStream);
            
            streamContent.Headers.ContentType = MediaTypeHeaderValue.Parse("application/octet-stream");
            form.Add(streamContent, "demo", fileName);

            Console.WriteLine($"[CS2MapBuddy] Uploading {fileName} to {ApiUrl}...");
            var response = await client.PostAsync(ApiUrl, form);
            
            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[CS2MapBuddy] Successfully uploaded {fileName}. Deleting local file.");
                fileStream.Close();
                File.Delete(filePath);
            }
            else
            {
                Console.WriteLine($"[CS2MapBuddy] Failed to upload {fileName}. Status code: {response.StatusCode}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[CS2MapBuddy] Exception during upload: {ex.Message}");
        }
    }
}
