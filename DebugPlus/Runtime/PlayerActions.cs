using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using DebugPlus.Common;
using EFT;
using EFT.Ballistics;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace DebugPlus.Runtime;

internal static class PlayerActions
{
    private static readonly Dictionary<string, (Vector3 Position, Vector2 Rotation)> Previous = new();
    private static readonly string[] Effects = { "LightBleeding", "HeavyBleeding", "Fracture", "Pain", "Tremor", "Exhaustion" };
    internal static readonly EBodyPart[] Parts = { EBodyPart.Head, EBodyPart.Chest, EBodyPart.Stomach, EBodyPart.LeftArm, EBodyPart.RightArm, EBodyPart.LeftLeg, EBodyPart.RightLeg };
    internal static void Reset() => Previous.Clear();
    internal static bool HasPrevious(string targetId) => Previous.ContainsKey(targetId);
    public static Task<CommandResult> Execute(WorkbenchRuntime runtime, Player player, CommandRequest request)
    {
        using var scope = new DebugHealthScope();
        var a = request.Args;
        var health = player.ActiveHealthController;
        var state = runtime.Overrides(player.ProfileId);
        if (health == null && request.Kind is not (CommandKind.RepairGear or CommandKind.ClearMalfunction or CommandKind.PreviewKit or CommandKind.Teleport or CommandKind.TeleportBack)) return Task.FromResult(CommandResult.Reject(request, "This target has no writable health controller."));
        var error = "";
        switch (request.Kind)
        {
            case CommandKind.PreviewKit:
                var summary = runtime.PreviewReplacement(player.ProfileId, a.Items, out var hash, a.FoundInRaid, a.ClearEquipment);
                return Task.FromResult(CommandResult.Complete(request, "Replacement preview ready.", Newtonsoft.Json.JsonConvert.SerializeObject(new InventoryPreview { Hash = hash, Summary = summary })));
            case CommandKind.GodMode: state.God = a.Enabled; break;
            case CommandKind.Stamina: state.Stamina = a.Enabled; break;
            case CommandKind.Oxygen: state.Oxygen = a.Enabled; break;
            case CommandKind.Energy: state.Energy = a.Enabled; break;
            case CommandKind.Hydration: state.Hydration = a.Enabled; break;
            case CommandKind.RefillVitals: Refill(health); break;
            case CommandKind.Recover:
                foreach (var p in Parts) { health.RemoveNegativeEffects(p); health.FullRestoreBodyPart(p); }
                health.RemoveNegativeEffects(EBodyPart.Common);
                Refill(health); break;
            case CommandKind.RemoveAllEffects:
                foreach (var p in Parts) health.RemoveNegativeEffects(p);
                health.RemoveNegativeEffects(EBodyPart.Common); break;
            case CommandKind.SetHealth:
            case CommandKind.Heal:
            case CommandKind.Damage:
            case CommandKind.RestoreLimb:
                if (!Enum.TryParse<EBodyPart>(a.BodyPart, out var part) || !Parts.Contains(part)) { error = "Choose a specific body part."; break; }
                if (a.Value < 0) { error = "Health values must be non-negative."; break; }
                if (request.Kind == CommandKind.RestoreLimb) { health.FullRestoreBodyPart(part); break; }
                var damageInfo = new DamageInfo { DamageType = EDamageType.Existence };
                if (request.Kind == CommandKind.Damage) { health.ApplyDamage(part, a.Value, damageInfo); break; }
                if (request.Kind == CommandKind.SetHealth && a.Value > 0 && health.IsBodyPartDestroyed(part)) health.FullRestoreBodyPart(part);
                var current = health.GetBodyPartHealth(part);
                var delta = request.Kind == CommandKind.SetHealth ? Mathf.Clamp(a.Value, 0, current.Maximum) - current.Current : a.Value;
                health.ChangeHealth(part, delta, damageInfo);
                if (request.Kind == CommandKind.SetHealth && a.Value == 0) health.DestroyBodyPart(part, EDamageType.Existence);
                break;
            case CommandKind.AddEffect:
            case CommandKind.RemoveEffect:
                if (!Effects.Contains(a.Effect) || !Enum.TryParse<EBodyPart>(a.BodyPart, out part)) { error = "Unsupported effect or body part."; break; }
                if (a.Effect == "Fracture" && part != EBodyPart.LeftArm && part != EBodyPart.RightArm && part != EBodyPart.LeftLeg && part != EBodyPart.RightLeg) { error = "Fractures require an arm or leg."; break; }
                var type = typeof(ActiveHealthController).GetNestedType(a.Effect, BindingFlags.Public | BindingFlags.NonPublic);
                var methodName = request.Kind == CommandKind.AddEffect ? "AddEffect" : "RemoveEffect";
                var method = typeof(ActiveHealthController).GetMethods().Single(m => m.Name == methodName && m.IsGenericMethodDefinition);
                method.MakeGenericMethod(type).Invoke(health, request.Kind == CommandKind.AddEffect ? new object[] { part, (float?)0, (float?)a.Duration, (float?)0, (float?)a.Strength, null } : new object[] { part });
                break;
            case CommandKind.RepairGear:
                foreach (var item in player.Inventory.AllRealPlayerItems)
                {
                    var repair = item.GetItemComponent<RepairableComponent>();
                    if (repair != null) repair.Durability = repair.MaxDurability;
                }
                break;
            case CommandKind.ClearMalfunction:
                var weapon = player.HandsController?.Item as Weapon;
                if (weapon == null) { error = "Equip a weapon first."; break; }
                weapon.MalfState.State = Weapon.EMalfunctionState.None;
                health?.RemoveMisfireEffect();
                break;
            case CommandKind.Teleport:
            case CommandKind.TeleportBack:
                error = Teleport(runtime, player, request); break;
            default: error = "This player command is not supported."; break;
        }
        return Task.FromResult(error.Length > 0 ? CommandResult.Reject(request, error) : CommandResult.Complete(request, "Applied to " + (player.Profile?.Info?.Nickname ?? player.ProfileId) + "."));
    }
    private static void Refill(ActiveHealthController health)
    {
        health.ChangeEnergy(health.Energy.Maximum - health.Energy.Current);
        health.ChangeHydration(health.Hydration.Maximum - health.Hydration.Current);
    }
    private static string Teleport(WorkbenchRuntime runtime, Player player, CommandRequest request)
    {
        var a = request.Args;
        if (a.MapId.Length > 0 && a.MapId != runtime.MapId) return "The destination belongs to another map.";
        var destination = new Vector3(a.X, a.Y, a.Z);
        var rotation = new Vector2(a.Yaw, 0);
        if (request.Kind == CommandKind.TeleportBack)
        {
            if (!Previous.TryGetValue(player.ProfileId, out var previous)) return "No previous teleport position in this raid.";
            destination = previous.Position; rotation = previous.Rotation;
        }
        else if (a.Action == "to-player" || a.Action == "bring-player")
        {
            var target = runtime.FindPlayer(a.Action == "bring-player" ? request.ActorId : a.ObjectId);
            if (!target) return "Destination player is unavailable.";
            destination = target.Position + target.Transform.Original.right * 1.2f; rotation = target.Rotation;
        }
        else if (a.Action == "spawnpoint")
        {
            var point = LocationScene.GetAllObjectsAndWhenISayAllIActuallyMeanIt<BotZone>().SelectMany(z => z.SpawnPoints).FirstOrDefault(p => p.Name == a.ObjectId);
            if (point == null) return "Spawn point is unavailable.";
            destination = point.Position;
        }
        else if (a.Action == "surface")
        {
            // Normally resolved by the requesting UI before multiplayer dispatch.
            if (!player.IsYourPlayer || !Camera.main || !Physics.Raycast(Camera.main.transform.position, Camera.main.transform.forward, out var hit, 1500, ~0, QueryTriggerInteraction.Ignore)) return "Aim at a solid surface first.";
            destination = hit.point + hit.normal * .15f;
        }
        if (!a.Exact)
        {
            if (!Physics.Raycast(destination + Vector3.up * 2f, Vector3.down, out var ground, 8f, ~0, QueryTriggerInteraction.Ignore)) return "No ground beneath that destination. Exact placement is available for authored coordinates.";
            destination = ground.point + Vector3.up * .12f;
            var hits = Physics.OverlapCapsule(destination + Vector3.up * .4f, destination + Vector3.up * 1.5f, .3f, ~0, QueryTriggerInteraction.Ignore);
            if (hits.Any(c => c && !c.transform.IsChildOf(player.Transform.Original))) return "There is not enough clearance for the player.";
        }
        Previous[player.ProfileId] = (player.Position, player.Rotation);
        player.Teleport(destination, true);
        player.Rotation = rotation;
        return "";
    }
}

internal sealed class DebugHealthScope : IDisposable
{
    [ThreadStatic] private static int _depth;
    public static bool Active => _depth > 0;
    public DebugHealthScope() => _depth++;
    public void Dispose() => _depth--;
}
