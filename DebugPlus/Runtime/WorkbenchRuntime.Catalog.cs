using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Comfort.Common;
using DebugPlus.Common;
using EFT;
using EFT.InventoryLogic;
using EFT.Interactive;
using Newtonsoft.Json;
using SPT.Common.Http;
using UnityEngine;

namespace DebugPlus.Runtime;

public sealed partial class WorkbenchRuntime
{
    private readonly Dictionary<string, Task<Sprite>> _icons = new();
    private readonly System.Threading.SemaphoreSlim _iconGate = new(2, 2);
    private IReadOnlyList<TargetInfo> _spawnPoints = Array.Empty<TargetInfo>();
    private IReadOnlyList<TargetInfo> _botZones = Array.Empty<TargetInfo>();
    public IReadOnlyList<TargetInfo> SpawnPoints => _spawnPoints;
    public IReadOnlyList<TargetInfo> BotZones => _botZones;
    private void CacheLocations()
    {
        var zones = LocationScene.GetAllObjectsAndWhenISayAllIActuallyMeanIt<BotZone>().ToArray();
        _botZones = zones.Select(z => new TargetInfo { Id = z.name, Name = FriendlyName(z.ShortName), Position = z.transform.position.ToString("F2") }).ToArray();
        _spawnPoints = zones.SelectMany(z => z.SpawnPoints.Select((p, index) => new TargetInfo { Id = p.Name, Name = FriendlyName(z.ShortName) + " · Spawn " + (index + 1), Position = p.Position.ToString("F2") })).GroupBy(p => p.Id).Select(g => g.First()).ToArray();
    }
    public Task<Sprite> LoadItemIcon(string templateId)
    {
        if (_icons.TryGetValue(templateId, out var cached)) return cached;
        return _icons[templateId] = LoadIcon(templateId);
    }
    private async Task<Sprite> LoadIcon(string id)
    {
        await _iconGate.WaitAsync();
        try
        {
            if (IsHeadless || !Singleton<ItemFactory>.Instantiated || !Singleton<ItemIconCreator>.Instantiated) return null;
            var item = Singleton<ItemFactory>.Instance.CreateItem(MongoID.Generate(), id, null);
            if (item == null) return null;
            var sprite = await Singleton<ItemIconCreator>.Instance.GetItemSpriteAsync(item, new IntVec2(128, 128));
            var info = _catalog.FirstOrDefault(i => i.Id == id); if (info != null) info.Icon = sprite;
            return sprite;
        }
        catch { return null; }
        finally { _iconGate.Release(); }
    }
    private Task _catalogRefresh;
    public Task RefreshCatalog() => _catalogRefresh != null && !_catalogRefresh.IsCompleted ? _catalogRefresh : _catalogRefresh = RefreshCatalogCore();
    private async Task RefreshCatalogCore()
    {
        if (!Singleton<ItemFactory>.Instantiated) { Report("Item database is still loading.", Common.CommandStatus.Rejected); return; }
        _catalog.Clear();
        var generation = _generation;
        var count = 0;
        foreach (var template in Singleton<ItemFactory>.Instance.ItemTemplates.Values)
        {
            if (template == null || template.Width <= 0) continue;
            _catalog.Add(new ItemInfo { Id = template._id, Name = TemplateName(template), Category = template.Parent == null ? "Other" : TemplateName(template.Parent) });
            if (++count % 250 == 0) { await Task.Yield(); if (generation != _generation) return; }
        }
        _catalog.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
    }
    public List<DebugItemRecord> CaptureKit(string targetId)
    {
        var equipment = InRaid ? FindPlayer(targetId)?.Equipment : targetId == LocalProfileId ? Session?.Profile?.Inventory?.Equipment : null;
        if (equipment == null) throw new InvalidOperationException("Target inventory is unavailable.");
        var roots = equipment.Slots.Where(s => !ItemTrees.ExcludedSlots.Contains(s.ID) && s.ContainedItem != null).Select(s => s.ContainedItem);
        return InventoryActions.Capture(roots);
    }
    public void RepairSavedKitSlots()
    {
        var changed = false;
        foreach (var kit in _kits) changed |= RepairKitSlots(kit);
        if (changed) Write("kits.json", _kits);
    }
    private bool RepairKitSlots(KitDocument kit)
    {
        var equipment = Session?.Profile?.Inventory?.Equipment;
        if (equipment == null || !Singleton<ItemFactory>.Instantiated || kit?.Items == null) return false;
        return ItemTrees.RepairRootSlots(kit.Items, root =>
        {
            var original = equipment.Slots.FirstOrDefault(s => s.ContainedItem?.Id == root.Id && s.ContainedItem.TemplateId == root.TemplateId);
            if (original != null) return new[] { original.ID };
            try
            {
                var item = InventoryActions.Factory.CreateItem(MongoID.Generate(), root.TemplateId, null);
                return equipment.Slots.Where(s => s.CheckCompatibility(item)).Select(s => s.ID).ToArray();
            }
            catch (Exception error) { Plugin.Log.LogWarning("Could not recover kit slot: " + error.Message); return Array.Empty<string>(); }
        });
    }
    public List<KitDocument> NativeBuilds()
    {
        var result = new List<KitDocument>();
        if (Session?.EquipmentBuildsStorage?.EquipmentBuilds != null)
            foreach (var build in Session.EquipmentBuildsStorage.EquipmentBuilds.Values)
                result.Add(new KitDocument { Id = "equipment-" + build.Id, Name = build.Name, Items = InventoryActions.Capture(build.Equipment.Slots.Where(s => !ItemTrees.ExcludedSlots.Contains(s.ID) && s.ContainedItem != null).Select(s => s.ContainedItem)) });
        if (Session?.WeaponBuildsStorage?.WeaponBuilds != null)
            foreach (var build in Session.WeaponBuildsStorage.WeaponBuilds.Values)
            {
                var items = InventoryActions.Capture(new[] { build.Item });
                if (items.Count == 0) continue;
                items.First(i => i.ParentId.Length == 0).SlotId = build.Item is Weapon weapon && weapon.WeapClass == "pistol" ? "Holster" : "FirstPrimaryWeapon";
                result.Add(new KitDocument { Id = "weapon-" + build.Id, Name = build.HandbookName ?? build.Item.Name.Localized(), Items = items });
            }
        return result;
    }
    public string PreviewReplacement(string targetId, List<DebugItemRecord> items, out string hash, bool foundInRaid = false, bool clearEquipment = false)
    {
        hash = "";
        var error = clearEquipment && items.Count == 0 ? "" : ItemTrees.Validate(items); if (error.Length > 0) throw new InvalidDataException(error);
        if (!InRaid && (!Policy.Authorized || targetId != LocalProfileId || World))
            throw new InvalidOperationException("Menu preview requires your authorized profile at the main menu.");
        var profile = InRaid ? FindPlayer(targetId)?.Profile : Session?.Profile;
        var equipment = profile?.Inventory?.Equipment ?? throw new InvalidOperationException("Target inventory is unavailable.");
        InventoryActions.ValidateReplacement(equipment, targetId, items, foundInRaid, clearEquipment);
        hash = InventoryActions.ReplacementFingerprint(equipment, targetId, items, foundInRaid, clearEquipment);
        var text = new StringBuilder("Target: ").AppendLine(profile.Info.Nickname).AppendLine("Included slots will be replaced. Old items and all their contents will be deleted.");
        if (clearEquipment) text.AppendLine("Empty kit: remove worn gear. Pockets, secure container, special slots and personal dogtag are kept.");
        foreach (var slotId in ItemTrees.ReplacementSlots(new CommandArguments { Items = items, ClearEquipment = clearEquipment }))
        {
            var slot = equipment.Slots.FirstOrDefault(s => s.ID == slotId) ?? throw new InvalidDataException("Unknown slot: " + slotId);
            var incoming = items.FirstOrDefault(i => i.ParentId.Length == 0 && i.SlotId == slotId);
            text.AppendLine().Append(slotId).Append(" ← ").AppendLine(incoming == null ? "Empty" : (incoming.TemplateId + " Name").Localized());
            if (slot.ContainedItem == null) text.AppendLine("  Currently empty");
            else foreach (var item in slot.ContainedItem.GetAllItems()) text.Append("  DELETE ").Append(item.Name.Localized()).Append(" × ").Append(item.StackObjectsCount).Append(" [").Append(item.Id).AppendLine("]");
        }
        return text.ToString();
    }
    public async Task<InventoryPreview> PreviewReplacementAsync(string targetId, List<DebugItemRecord> items, bool foundInRaid = false, bool clearEquipment = false)
    {
        // Empty previews are informational and never authorize a replacement.
        // Do this before accessing a player, inventory or multiplayer connection.
        if (!clearEquipment && (items == null || items.Count == 0))
            return new InventoryPreview { Summary = WorkbenchAvailability.EmptyKitPreview };
        if (IsCoop)
        {
            var result = await Submit(CommandKind.PreviewKit, new CommandArguments { Items = items, FoundInRaid = foundInRaid, ClearEquipment = clearEquipment }, targetId);
            if (result.Status != Common.CommandStatus.Completed) throw new InvalidOperationException(result.Message);
            return JsonConvert.DeserializeObject<InventoryPreview>(result.DataJson);
        }
        var summary = PreviewReplacement(targetId, items, out var hash, foundInRaid, clearEquipment);
        return new InventoryPreview { Hash = hash, Summary = summary };
    }
    public string InspectPlayer(string id, bool technical = true)
    {
        if (!InRaid) return (technical ? "Profile: " + LocalProfileId : Session?.Profile?.Info?.Nickname ?? "Your PMC") + "\nMain menu\n" + Policy.Message;
        var player = FindPlayer(id);
        if (!player) return "Player is unavailable.";
        var text = new StringBuilder().AppendLine(player.Profile.Info.Nickname);
        if (technical) text.AppendLine(player.ProfileId);
        text.AppendLine("Position " + player.Position.ToString("F3")).AppendLine("Yaw " + player.Rotation.x.ToString("F1"));
        if (player.HealthController != null)
            foreach (var part in PlayerActions.Parts)
            {
                var h = player.HealthController.GetBodyPartHealth(part);
                text.Append(FriendlyName(part.ToString())).Append(": ").Append(h.Current.ToString("F1")).Append(" / ").Append(h.Maximum.ToString("F1")).AppendLine();
                foreach (var effect in player.HealthController.GetAllEffects(part)) text.Append("  ").Append(effect.GetType().Name).Append(" ").Append(effect.CurrentStrength.ToString("F2")).AppendLine();
            }
        text.AppendLine("Weapon: " + (player.HandsController?.Item?.Name.Localized() ?? "Empty hands"));
        if (player.Physical?.Stamina != null) text.AppendLine("Stamina: " + player.Physical.Stamina.Current.ToString("F1"));
        if (player.ActiveHealthController is { } health) text.AppendLine("Energy: " + health.Energy.Current.ToString("F1")).AppendLine("Hydration: " + health.Hydration.Current.ToString("F1"));
        return text.ToString();
    }
    public string InspectWorld(bool technical = true)
    {
        var text = new StringBuilder().AppendLine("Map: " + (InRaid ? MapName : "Main menu"));
        if (technical) text.AppendLine("Session: " + SessionId);
        text.AppendLine("Authority: " + Status).AppendLine("Frame time: " + (Time.unscaledDeltaTime * 1000).ToString("F1") + " ms");
        if (Camera.main && Physics.Raycast(Camera.main.transform.position, Camera.main.transform.forward, out var hit, 1500, ~0, QueryTriggerInteraction.Ignore))
        {
            text.AppendLine().AppendLine("Aimed object: " + hit.collider.name).AppendLine("Position: " + hit.point.ToString("F3"));
            var door = hit.collider.GetComponentInParent<WorldInteractiveObject>();
            if (door) { if (technical) text.AppendLine("Door ID: " + door.Id).AppendLine("Key ID: " + door.KeyId); text.AppendLine("State: " + door.DoorState); }
        }
        return text.ToString();
    }
}
