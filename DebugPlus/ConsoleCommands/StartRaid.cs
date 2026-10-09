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
        var runtime = DebugPlus.Runtime.WorkbenchRuntime.Instance;
        if (!runtime) { ConsoleScreen.LogError("DebugPlus is not ready."); return; }
        var result = await runtime.Submit(DebugPlus.Common.CommandKind.StartRaid,
            new DebugPlus.Common.CommandArguments { MapId = _mapId, Enabled = _isBotsEnabled });
        if (result.Status == DebugPlus.Common.CommandStatus.Completed) ConsoleScreen.Log(result.Message);
        else ConsoleScreen.LogError(result.Message);
    }
}