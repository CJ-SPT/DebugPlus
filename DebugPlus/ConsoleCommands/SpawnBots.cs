using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.Console.Core;
using EFT.UI;

namespace DebugPlus.ConsoleCommands;

[ConsoleCommand("SpawnBots", "", "", "Spawns bots with specified WildSpawnType and number.")]
public class SpawnBotsAsync : AsyncCommand
{
    private readonly WildSpawnType _wildSpawnType;
    private readonly int _numberOfBots;

    public override object[] ArgumentsValue => [_wildSpawnType, _numberOfBots];

    public SpawnBotsAsync(
        [ConsoleArgument("assault", "Type of bots to spawn")] WildSpawnType wildSpawnType,
        [ConsoleArgument(5, "Number of bots to spawn")] int numberOfBots
    )
    {
        _wildSpawnType = wildSpawnType;
        _numberOfBots = numberOfBots;
    }

    public override async Task Execute()
    {
        var runtime = DebugPlus.Runtime.WorkbenchRuntime.Instance;
        if (!runtime) { ConsoleScreen.LogError("DebugPlus is not ready."); return; }
        var result = await runtime.Submit(DebugPlus.Common.CommandKind.SpawnBots,
            new DebugPlus.Common.CommandArguments { Role = _wildSpawnType.ToString(), Count = _numberOfBots, Difficulty = "hard" });
        if (result.Status == DebugPlus.Common.CommandStatus.Completed) ConsoleScreen.Log(result.Message);
        else ConsoleScreen.LogError(result.Message);
    }
}