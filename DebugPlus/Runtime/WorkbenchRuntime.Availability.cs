using System;
using System.Linq;
using Comfort.Common;
using DebugPlus.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.Weather;

namespace DebugPlus.Runtime;

public sealed partial class WorkbenchRuntime
{
    public AvailabilityContext Availability(string targetId) => new()
    {
        InRaid = InRaid, InWorld = World != null, ProfileReady = Session?.Profile?.Inventory != null,
        Authorized = IsAuthorized, IsCoop = IsCoop, CoopReady = CoopReady,
        OwnTarget = targetId == LocalProfileId, TargetAlive = FindPlayer(targetId)?.HealthController?.IsAlive == true
    };
    public string PageUnavailable(string page, string targetId) => WorkbenchAvailability.Page(page, Availability(targetId));

    public string CommandUnavailable(CommandKind kind, CommandArguments args, string targetId)
    {
        var context = Availability(targetId);
        var reason = Commands.Scope(kind) == CommandScope.Menu ? WorkbenchAvailability.Menu(context)
            : Commands.IsInventory(kind) || kind == CommandKind.PreviewKit ? WorkbenchAvailability.Inventory(context)
            : WorkbenchAvailability.Raid(context, Commands.Scope(kind) == CommandScope.Player);
        if (reason.Length > 0) return reason;
        var player = InRaid ? FindPlayer(targetId) : null;
        if (Commands.IsInventory(kind) || kind == CommandKind.PreviewKit)
        {
            if (InRaid && player?.InventoryController == null) return "This player's inventory is unavailable.";
            if (InRaid && InventoryActions.IsUncertain(targetId)) return "Inventory synchronization needs reconciliation. Further inventory changes are unavailable this raid.";
            if (InRaid && player.InventoryController.IsInventoryBlocked()) return "Wait for the player's current inventory action to finish.";
        }
        switch (kind)
        {
            case CommandKind.ClearMalfunction:
                if (player?.HandsController?.Item is not Weapon) return "The selected player must have a weapon in their hands.";
                break;
            case CommandKind.TeleportBack:
                if ((!IsCoop || player?.IsYourPlayer == true) && !PlayerActions.HasPrevious(targetId)) return "This player has no previous debug teleport in this raid.";
                break;
            case CommandKind.SetTime:
                if (!TODSkyProvider.IsAvailable || !TODSkyProvider.Instance.CurrentTime) return "This map has no daylight controls.";
                break;
            case CommandKind.SetWeather:
                if (!WeatherController.Instance || WeatherController.Instance.WeatherDebug == null) return "Weather controls are unavailable on this map.";
                break;
            case CommandKind.RestoreEnvironment:
                if (!WorldActions.HasEnvironmentOverride && !IsCoop) return "Time and weather are already using normal controls.";
                break;
            case CommandKind.SpawnBots:
                if (!IsCoop && (Singleton<AbstractGame>.Instance as IBotGame)?.BotsController?.BotSpawner == null) return "Bot spawning is unavailable in this raid.";
                if (BotZones.Count == 0) return "This map has no supported bot spawn areas.";
                break;
        }
        if (args == null) return ""; // Base availability for fields and sections.
        switch (kind)
        {
            case CommandKind.SpawnItem:
                if (string.IsNullOrWhiteSpace(args.TemplateId)) return "Select an item from the grid first.";
                break;
            case CommandKind.SpawnKit:
            case CommandKind.EquipKit:
            case CommandKind.PreviewKit:
                if (args.Items == null || (args.Items.Count == 0 && !(args.ClearEquipment && kind is CommandKind.PreviewKit or CommandKind.EquipKit))) return "Select a kit and include at least one equipment slot.";
                if (kind == CommandKind.EquipKit && string.IsNullOrWhiteSpace(args.ExpectedInventoryHash)) return "Preview the replacement before equipping this kit.";
                break;
            case CommandKind.Teleport:
                if (args.Action == "to-player" && !FindPlayer(args.ObjectId)) return "Choose an available destination player.";
                if (args.Action == "spawnpoint" && !SpawnPoints.Any(p => p.Id == args.ObjectId)) return "Choose an available map spawn point.";
                if (args.Action == "surface" && !UnityEngine.Camera.main) return "A player camera is required for aimed teleporting.";
                break;
            case CommandKind.Door:
                var door = World?.FindDoor(args.ObjectId);
                if (!door) return "Select an available door first.";
                if (!Enum.TryParse<EInteractionType>(args.Action, true, out var action) || !door.IsInteractionSuitsCurrentState(action)) return "This action is unavailable in the door's current state.";
                if (args.Action == "Breach" && (door is not EFT.Interactive.Door breachDoor || !breachDoor.CanBeBreached)) return "This door cannot be breached.";
                if (door.DoorState == EFT.Interactive.EDoorState.Interacting) return "Wait for the current door interaction to finish.";
                if ((args.Action == "Open" || args.Action == "Lock") && door.DoorState != EFT.Interactive.EDoorState.Shut) return "This action requires a closed, unlocked door.";
                if (args.Action == "Unlock" && door.DoorState != EFT.Interactive.EDoorState.Locked) return "This door is not locked.";
                if (args.Action == "Close" && door.DoorState != EFT.Interactive.EDoorState.Open) return "This door is not open.";
                break;
            case CommandKind.PauseBots:
            case CommandKind.ResumeBots:
            case CommandKind.HealBots:
            case CommandKind.KillBots:
            case CommandKind.DespawnBots:
                if (!Targets.Any(t => t.IsBot && (args.ObjectId == "all" || t.Id == args.ObjectId))) return "Select a live bot or a non-empty bot group.";
                break;
            case CommandKind.AddEffect:
                if (args.Effect == "Fracture" && args.BodyPart is not ("LeftArm" or "RightArm" or "LeftLeg" or "RightLeg")) return "Fractures can only be added to an arm or leg.";
                break;
        }
        if (kind is CommandKind.SetHealth or CommandKind.Heal or CommandKind.Damage && args.Value < 0) return "Health and damage values must be zero or greater.";
        if (kind == CommandKind.SetTime && (args.Value < 0 || args.Value >= 24)) return "Choose an hour between 0 and 24.";
        if (kind == CommandKind.Teleport && args.MapId.Length > 0 && args.MapId != MapId) return "This destination belongs to another map.";
        return Commands.Validate(new CommandRequest { SessionId = InRaid ? SessionId : Policy.SessionId, ActorId = LocalProfileId, TargetId = targetId, Kind = kind, Args = args });
    }
    public string CaptureUnavailable(string targetId)
    {
        if (InRaid && FindPlayer(targetId)?.HealthController?.IsAlive != true) return "Select a live player to capture equipment.";
        if (!InRaid && (World || targetId != LocalProfileId)) return "Capture your PMC equipment from the main menu or select a player in a raid.";
        var equipment = InRaid ? FindPlayer(targetId)?.Equipment : Session?.Profile?.Inventory?.Equipment;
        return equipment?.Slots.Any(s => !ItemTrees.ExcludedSlots.Contains(s.ID) && s.ContainedItem != null) == true ? "" : "No capturable equipment is available.";
    }
    public bool HasNativeBuilds => Session?.EquipmentBuildsStorage?.EquipmentBuilds?.Count > 0 || Session?.WeaponBuildsStorage?.WeaponBuilds?.Count > 0;
}
