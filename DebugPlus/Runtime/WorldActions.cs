using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Comfort.Common;
using DebugPlus.Common;
using EFT;
using EFT.Interactive;
using EFT.Weather;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace DebugPlus.Runtime;

internal static class WorldActions
{
    internal static readonly HashSet<BotOwner> Paused = new();
    private static TOD_Time _time;
    private static bool _timeLocked;
    private static DateTime _timeBefore;
    private static DateTime _timeBase;
    private static float _timeStart;
    private static bool _timeFrozen;
    private static WeatherDebug _weather;
    private static bool _weatherEnabled;
    private static float _clouds, _rain, _fog, _wind, _thunder;
    internal static bool HasEnvironmentOverride => _time || _weather != null;

    public static async Task<CommandResult> Execute(WorkbenchRuntime runtime, CommandRequest request)
    {
        var a = request.Args;
        var game = Singleton<AbstractGame>.Instance as IBotGame;
        switch (request.Kind)
        {
            case CommandKind.SpawnBots:
                if (game?.BotsController?.BotSpawner == null) return CommandResult.Reject(request, "Bot spawning is unavailable in this raid.");
                if (!Enum.TryParse<WildSpawnType>(a.Role, true, out var role) || !Enum.IsDefined(typeof(WildSpawnType), role) || !Enum.TryParse<BotDifficulty>(a.Difficulty, true, out var difficulty)) return CommandResult.Reject(request, "Invalid bot role or difficulty.");
                var zones = LocationScene.GetAllObjectsAndWhenISayAllIActuallyMeanIt<BotZone>();
                var zone = a.ObjectId.Length == 0 ? zones.OrderBy(z => Vector3.Distance(z.transform.position, runtime.FindPlayer(request.ActorId)?.Position ?? Vector3.zero)).FirstOrDefault() : zones.FirstOrDefault(z => z.name == a.ObjectId || z.ShortName == a.ObjectId);
                if (!zone) return CommandResult.Reject(request, "No matching bot spawn zone is available.");
                await game.BotsController.BotSpawner.ActivateBotsByWave(new SpawnWave { BotsCount = a.Count, Side = role.ToString().IndexOf("usec", StringComparison.OrdinalIgnoreCase) >= 0 ? EPlayerSide.Usec : role.ToString().IndexOf("bear", StringComparison.OrdinalIgnoreCase) >= 0 ? EPlayerSide.Bear : EPlayerSide.Savage, SpawnAreaName = zone.name, Time = 0, WildSpawnType = role, IsPlayers = false, Difficulty = difficulty, ChanceGroup = 100, WithCheckMinMax = false });
                return CommandResult.Complete(request, "Spawn wave completed in " + zone.ShortName + ".");
            case CommandKind.PauseBots:
            case CommandKind.ResumeBots:
            case CommandKind.HealBots:
            case CommandKind.KillBots:
            case CommandKind.DespawnBots:
                var bots = WorkbenchRuntime.World.AllAlivePlayersList.Where(p => p && p.IsAI && (a.ObjectId == "all" || p.ProfileId == a.ObjectId || (a.ObjectId.Length == 0 && p.ProfileId == request.TargetId))).ToArray();
                if (bots.Length == 0) return CommandResult.Reject(request, "No matching live bots.");
                foreach (var bot in bots)
                {
                    var owner = bot.AIData?.BotOwner;
                    switch (request.Kind)
                    {
                        case CommandKind.PauseBots: if (owner) { owner.StopMove(); owner.ShootData?.EndShoot(); Paused.Add(owner); } break;
                        case CommandKind.ResumeBots: if (owner) Paused.Remove(owner); break;
                        case CommandKind.HealBots: bot.ActiveHealthController?.RestoreFullHealth(); break;
                        case CommandKind.KillBots: bot.ActiveHealthController?.Kill(EDamageType.Existence); break;
                        case CommandKind.DespawnBots:
                            if (game?.BotsController?.BotSpawner == null) return CommandResult.Reject(request, "Bot manager is unavailable.");
                            if (owner) Paused.Remove(owner);
                            game.BotsController.BotSpawner.DeletePlayer(bot); break;
                    }
                }
                return CommandResult.Complete(request, "Applied to " + bots.Length + " bot(s).");
            case CommandKind.Door:
                var door = WorkbenchRuntime.World.FindDoor(a.ObjectId) as Door;
                if (!door) return CommandResult.Reject(request, "Select a door by its exact identifier.");
                if (!Enum.TryParse<EInteractionType>(a.Action, true, out var action) || !new[] { "Open", "Close", "Lock", "Unlock", "Breach" }.Contains(a.Action, StringComparer.OrdinalIgnoreCase)) return CommandResult.Reject(request, "Unsupported door action.");
                var desired = action == EInteractionType.Lock ? EDoorState.Locked : action == EInteractionType.Close || action == EInteractionType.Unlock ? EDoorState.Shut : EDoorState.Open;
                if (door.DoorState == desired) return CommandResult.Complete(request, "Door is already in the requested state.");
                if (action == EInteractionType.Breach && !door.CanBeBreached) return CommandResult.Reject(request, "This door cannot be breached.");
                if (door.DoorState == EDoorState.Interacting) return CommandResult.Reject(request, "Wait for the current door interaction to finish.");
                if ((action == EInteractionType.Open || action == EInteractionType.Lock) && door.DoorState != EDoorState.Shut) return CommandResult.Reject(request, "This action requires a closed, unlocked door.");
                if (action == EInteractionType.Unlock && door.DoorState != EDoorState.Locked) return CommandResult.Reject(request, "This door is not locked.");
                if (action == EInteractionType.Close && door.DoorState != EDoorState.Open) return CommandResult.Reject(request, "This action requires an open door.");
                if (!door.IsInteractionSuitsCurrentState(action)) return CommandResult.Reject(request, "That action is not valid for the current door state.");
                switch (action)
                {
                    case EInteractionType.Open: door.Open(); break;
                    case EInteractionType.Close: door.Close(); break;
                    case EInteractionType.Lock: door.Lock(); break;
                    case EInteractionType.Unlock: door.Unlock(); break;
                    case EInteractionType.Breach: door.KickOpen(false); break;
                }
                return CommandResult.Complete(request, "Door " + a.Action.ToLowerInvariant() + " requested.");
            case CommandKind.SetTime:
                if (!TODSkyProvider.IsAvailable || !TODSkyProvider.Instance.CurrentTime) return CommandResult.Reject(request, "This map has no daylight controller.");
                if (a.Value < 0 || a.Value >= 24) return CommandResult.Reject(request, "Hour must be between 0 and 24.");
                if (!_time) { _time = TODSkyProvider.Instance.CurrentTime; _timeLocked = _time.LockCurrentTime; _timeBefore = TODSkyProvider.Instance.Cycle.DateTime; }
                TODSkyProvider.Instance.Cycle.Hour = a.Value;
                _time.LockCurrentTime = true;
                _timeBase = TODSkyProvider.Instance.Cycle.DateTime; _timeStart = Time.unscaledTime; _timeFrozen = a.Enabled;
                return CommandResult.Complete(request, "Daylight set to " + a.Value.ToString("F2") + (a.Enabled ? " and frozen." : "."));
            case CommandKind.SetWeather:
                if (!WeatherController.Instance || WeatherController.Instance.WeatherDebug == null) return CommandResult.Reject(request, "This map has no weather controller.");
                if (a.Weather == null || new[] { a.Weather.Clouds, a.Weather.Rain, a.Weather.Fog, a.Weather.Wind, a.Weather.Thunder }.Any(v => !Commands.Finite(v) || v < 0 || v > 1)) return CommandResult.Reject(request, "Weather channels must be between 0 and 1.");
                if (_weather == null)
                {
                    _weather = WeatherController.Instance.WeatherDebug; _weatherEnabled = _weather.Enabled;
                    _clouds = _weather.CloudDensity; _rain = _weather.Rain; _fog = _weather.Fog; _wind = _weather.WindMagnitude; _thunder = _weather.LightningThunderProbability;
                }
                _weather.Enabled = true; _weather.CloudDensity = a.Weather.Clouds * 2 - 1; _weather.Rain = a.Weather.Rain; _weather.Fog = Mathf.Lerp(.001f, .255f, a.Weather.Fog); _weather.WindMagnitude = a.Weather.Wind; _weather.LightningThunderProbability = a.Weather.Thunder;
                return CommandResult.Complete(request, "Weather override applied.");
            case CommandKind.RestoreEnvironment: RestoreEnvironment(); return CommandResult.Complete(request, "Normal environment control restored.");
            default: return CommandResult.Reject(request, "Unsupported world command.");
        }
    }
    private static void RestoreEnvironment()
    {
        if (_time && TODSkyProvider.IsAvailable)
        {
            TODSkyProvider.Instance.Cycle.DateTime = !_timeLocked && _time.GameDateTime != null ? _time.GameDateTime.CalculateTaxonomyDate(_time.GetTaxonomyTodSkyDate()) : _timeBefore;
            _time.LockCurrentTime = _timeLocked;
        }
        _time = null;
        if (_weather != null) { _weather.CloudDensity = _clouds; _weather.Rain = _rain; _weather.Fog = _fog; _weather.WindMagnitude = _wind; _weather.LightningThunderProbability = _thunder; _weather.Enabled = _weatherEnabled; }
        _weather = null;
    }
    internal static void Reset() { RestoreEnvironment(); Paused.Clear(); }
    internal static void Tick()
    {
        if (_time && !_timeFrozen && TODSkyProvider.IsAvailable)
            TODSkyProvider.Instance.Cycle.DateTime = _timeBase.AddSeconds((Time.unscaledTime - _timeStart) * (_time.GameDateTime?.TimeFactor ?? 1));
    }
}
internal sealed class PauseBotUpdatePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => AccessTools.Method(typeof(BotOwner), nameof(BotOwner.UpdateManual));
    [PatchPrefix] private static bool Prefix(BotOwner __instance) => !WorldActions.Paused.Contains(__instance);
}
