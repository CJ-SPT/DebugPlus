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
        [ConsoleArgument("assault", "Type of bots to spawn")] 
        WildSpawnType wildSpawnType, 
        [ConsoleArgument(5, "Number of bots to spawn")] 
        int numberOfBots
    )
    {
        _wildSpawnType = wildSpawnType;
        _numberOfBots = numberOfBots;
    }
        
    public override async Task Execute()
    {
        if (!Singleton<IBotGame>.Instantiated)
        {
            ConsoleScreen.LogError("You can only spawn bots while in raid.");
            return;
        }
            
        if (_numberOfBots <= 0)
        {
            ConsoleScreen.LogError($"Invalid number: {_numberOfBots}. Please enter a valid positive integer.");
            return;
        }
            
        var newBotData = new BotWaveDataClass
        {
            BotsCount = _numberOfBots,
            Side = EPlayerSide.Savage,
            SpawnAreaName = "",
            Time = 0f,
            WildSpawnType = _wildSpawnType,
            IsPlayers = false,
            Difficulty = BotDifficulty.hard,
            ChanceGroup = 100f,
            WithCheckMinMax = false
        };
        
        ConsoleScreen.Log($"Spawning {_numberOfBots} bots... please wait.");
        
        var botController = (IBotGame)Singleton<AbstractGame>.Instance;
        await botController.BotsController.BotSpawner.ActivateBotsByWave(newBotData);
            
        ConsoleScreen.Log($"SpawnNPC completed. {_numberOfBots} bots spawned.");
    }
}