using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.Console.Core;
using EFT.UI;

namespace DebugPlus.ConsoleCommands;

[ConsoleCommand("StartRaid", "", "", "Starts a raid with the specified parameters")]
public class StartRaidAsync : AsyncCommand
{
    private readonly string _mapId;
    private readonly bool _isBotsEnabled;
        
    public override object[] ArgumentsValue => [_mapId, _isBotsEnabled];

    public StartRaidAsync(
        [ConsoleArgument("factory4_day", "Map to start a raid on")] 
        string gameMap, 
        [ConsoleArgument(true, "Enable bots")] 
        bool enableBots
    )
    {
        _mapId  = gameMap;
        _isBotsEnabled = enableBots;
    }
        
    public override async Task Execute()
    {
        if (Singleton<GameWorld>.Instantiated)
        {
            ConsoleScreen.LogError("You cannot start a raid while already in a raid.");
            return;
        }
            
        if (TarkovApplication.Exist(out var app))
        {
            await app.InternalStartGame(_mapId, true, _isBotsEnabled);
            return;
        }
            
        ConsoleScreen.LogError("Could not locate TarkovApplication.");
    }
}