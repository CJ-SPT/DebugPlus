using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.Console.Core;
using EFT.UI;

namespace DebugPlus.ConsoleCommands;

[ConsoleCommand("recreate_backend", "", "", "Reloads all data from server")]
public class ReloadFromServerAsync : AsyncCommand
{
    public override object[] ArgumentsValue => [];
        
    public override async Task Execute()
    {
        if (Singleton<GameWorld>.Instantiated)
        {
            ConsoleScreen.LogError("You can only reload data while not in raid.");
            return;
        }
            
        if (TarkovApplication.Exist(out var app))
        {
            await app.RecreateCurrentBackend();
            return;
        }
            
        ConsoleScreen.LogError("Could not locate TarkovApplication.");
    }
}