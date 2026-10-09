using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using Comfort.Common;
using UnityEngine;

namespace DebugPlus.Runtime;

public sealed partial class WorkbenchRuntime
{
    private Door[] _doors = Array.Empty<Door>();
    private GameWorld _doorWorld;
    private void ResetDoorCache() { _doors = Array.Empty<Door>(); _doorWorld = null; }

    public static string FriendlyName(string value) => Regex.Replace(Regex.Replace(value ?? "", "([a-z])([A-Z])", "$1 $2").Replace('_', ' '), "\\s+", " ").Trim();
    public string MapName => MapId switch
    {
        "bigmap" => "Customs", "factory4_day" => "Factory (day)", "factory4_night" => "Factory (night)",
        "Interchange" or "interchange" => "Interchange", "RezervBase" or "rezervbase" => "Reserve",
        "TarkovStreets" or "tarkovstreets" => "Streets of Tarkov", "laboratory" => "The Lab",
        "sandbox" => "Ground Zero", "sandbox_high" => "Ground Zero (high level)",
        "woods" => "Woods", "shoreline" => "Shoreline", "lighthouse" => "Lighthouse", _ => FriendlyName(MapId)
    };
    public string ItemName(string templateId)
    {
        if (string.IsNullOrWhiteSpace(templateId)) return "Unknown item";
        if (Singleton<ItemFactory>.Instantiated && InventoryActions.Factory.ItemTemplates.TryGetValue(templateId, out var template)) return TemplateName(template);
        var name = (templateId + " Name").Localized();
        return name == templateId + " Name" || name == templateId ? "Unnamed item" : name;
    }
    private static string TemplateName(ItemTemplate template)
    {
        var key = template.NameLocalizationKey;
        var localized = key.Localized();
        if (!string.IsNullOrWhiteSpace(localized) && localized != key) return localized;
        var fallback = string.IsNullOrWhiteSpace(template.Name) ? template._name : template.Name;
        return string.IsNullOrWhiteSpace(fallback) || Regex.IsMatch(fallback, "^[a-fA-F0-9]{24}( Name)?$") ? FriendlyName(template.GetType().Name.Replace("Template", "")) : FriendlyName(fallback);
    }
    public string ReadablePreview(string summary)
    {
        var text = Regex.Replace(summary ?? "", @"\s*\[[a-fA-F0-9]{24}\]", "");
        text = Regex.Replace(text, @"\b[a-fA-F0-9]{24}\b", match => ItemName(match.Value));
        foreach (var slot in new[] { "FirstPrimaryWeapon", "SecondPrimaryWeapon", "TacticalVest", "ArmorVest", "Headwear", "FaceCover", "Earpiece", "Eyewear", "Holster", "Backpack", "Scabbard" })
            text = text.Replace(slot, FriendlySlot(slot));
        return text;
    }
    public static string FriendlySlot(string slot) => slot switch
    {
        "FirstPrimaryWeapon" => "Primary weapon", "SecondPrimaryWeapon" => "Secondary weapon", "TacticalVest" => "Chest rig",
        "ArmorVest" => "Body armor", "Headwear" => "Headwear", "FaceCover" => "Face cover", "Earpiece" => "Headset",
        "Eyewear" => "Eyewear", "Holster" => "Sidearm", "Scabbard" => "Melee weapon", "mod_magazine" => "Magazine",
        "cartridges" => "Ammunition", "hideout" or "main" => "Contents", _ => FriendlyName(slot)
    };
    public (Vector3 Position, float Yaw) PlayerLocation(string id)
    {
        var player = FindPlayer(id);
        return player ? (player.Position, player.Rotation.x) : (Vector3.zero, 0);
    }
    public IReadOnlyList<TargetInfo> NearbyDoors()
    {
        if (!InRaid) return Array.Empty<TargetInfo>();
        if (_doorWorld != World)
        {
            _doorWorld = World;
            _doors = LocationScene.GetAllObjectsAndWhenISayAllIActuallyMeanIt<Door>().ToArray();
        }
        var origin = FindPlayer(LocalProfileId)?.Position ?? Vector3.zero;
        var local = FindPlayer(LocalProfileId);
        return _doors.Where(d => d).OrderBy(d => Vector3.Distance(origin, d.transform.position)).Take(40)
            .Select(d =>
            {
                var direction = local ? local.Transform.Original.InverseTransformDirection(d.transform.position - origin) : d.transform.position - origin;
                var bearing = Mathf.Abs(direction.x) > Mathf.Abs(direction.z) ? direction.x > 0 ? "to your right" : "to your left" : direction.z > 0 ? "ahead" : "behind you";
                return new TargetInfo { Id = d.Id, Name = "Door · " + Vector3.Distance(origin, d.transform.position).ToString("0") + " m " + bearing + " · " + FriendlyName(d.DoorState.ToString()), Position = d.transform.position.ToString("F1") };
            }).ToArray();
    }
    public TargetInfo AimedDoor()
    {
        if (!InRaid || !Camera.main || !Physics.Raycast(Camera.main.transform.position, Camera.main.transform.forward, out var hit, 1500, ~0, QueryTriggerInteraction.Ignore)) return null;
        var door = hit.collider.GetComponentInParent<Door>();
        return door ? new TargetInfo { Id = door.Id, Name = "Door in view · " + FriendlyName(door.DoorState.ToString()), Position = hit.point.ToString("F1") } : null;
    }
    public IReadOnlyList<TargetInfo> BotRoles => Enum.GetNames(typeof(WildSpawnType)).Select(role => new TargetInfo
    {
        Id = role, Name = role switch
        {
            "assault" => "Scav", "marksman" => "Sniper scav", "pmcUSEC" or "pmcUsec" => "PMC · USEC", "pmcBEAR" or "pmcBear" => "PMC · BEAR",
            "pmcBot" => "Raider", "exUsec" => "Rogue", "bossBully" => "Reshala", "bossKilla" => "Killa", "bossKojaniy" => "Shturman",
            "bossGluhar" => "Glukhar", "bossSanitar" => "Sanitar", "bossTagilla" => "Tagilla", "bossKnight" => "Knight",
            "followerBigPipe" => "Big Pipe", "followerBirdEye" => "Birdeye", "sectantPriest" => "Cultist priest", "sectantWarrior" => "Cultist warrior",
            _ => FriendlyName(role)
        }
    }).OrderBy(r => r.Name).ToArray();
}
