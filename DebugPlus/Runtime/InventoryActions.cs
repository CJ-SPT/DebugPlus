using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Comfort.Common;
using DebugPlus.Common;
using EFT;
using EFT.BinarySerialization;
using EFT.InventoryLogic;
using JsonType;
using Mirror;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using CommandStatus = DebugPlus.Common.CommandStatus;

namespace DebugPlus.Runtime;

public sealed class InventoryDelta
{
    public string RequestId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string TargetId { get; set; } = "";
    public List<InventoryRoot> Roots { get; set; } = new();
    public List<string> RemovedRootIds { get; set; } = new();
    public bool Ground { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
}
public sealed class InventoryRoot
{
    public string Descriptor { get; set; } = "";
    public string ParentId { get; set; } = "";
    public string SlotId { get; set; } = "";
    public string LocationJson { get; set; } = "";
}

internal static class InventoryActions
{
    private static readonly HashSet<string> Applied = new();
    private static readonly HashSet<string> Applying = new();
    private static readonly HashSet<string> GroundAttempts = new();
    private static readonly HashSet<string> Uncertain = new();
    internal static bool IsUncertain(string targetId) => Uncertain.Contains(targetId);
    internal static void Reset() { Applied.Clear(); Applying.Clear(); GroundAttempts.Clear(); Uncertain.Clear(); }
    internal static ItemFactory Factory => Singleton<ItemFactory>.Instance;
    internal static JsonSerializer Serializer => JsonSerializer.Create(new JsonSerializerSettings { Converters = EftJsonConverters.Converters.ToList() });

    internal static List<DebugItemRecord> Capture(IEnumerable<Item> roots)
    {
        var rootArray = roots.ToArray();
        var rootIds = new HashSet<string>(rootArray.Select(x => x.Id.ToString()));
        var result = new List<DebugItemRecord>();
        foreach (var flat in NativeItemSerialization.FlattenAttached(Factory, rootArray))
        {
            var json = JObject.FromObject(flat, Serializer);
            result.Add(new DebugItemRecord
            {
                Id = flat._id, TemplateId = flat._tpl,
                ParentId = rootIds.Contains(flat._id) ? "" : flat.parentId?.ToString() ?? "",
                SlotId = flat.slotId ?? "",
                LocationJson = json["location"]?.Type == JTokenType.Null ? "" : json["location"]?.ToString(Formatting.None) ?? "",
                StateJson = json["upd"]?.Type == JTokenType.Null ? "" : json["upd"]?.ToString(Formatting.None) ?? ""
            });
        }
        return result;
    }
    internal static string Fingerprint(InventoryEquipment equipment)
    {
        var flat = Factory.TreeToFlatItems(new Item[] { equipment });
        var token = JToken.FromObject(flat, Serializer);
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Canonical(token).ToString(Formatting.None)))).Replace("-", "");
    }
    private static JToken Canonical(JToken token) => token is JObject o ? new JObject(o.Properties().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => new JProperty(p.Name, Canonical(p.Value)))) : token is JArray a ? new JArray(a.Select(Canonical)) : token.DeepClone();
    internal static string ReplacementFingerprint(InventoryEquipment equipment, string target, List<DebugItemRecord> items, bool foundInRaid, bool clearEquipment = false)
    {
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Fingerprint(equipment) + target + JsonConvert.SerializeObject(items) + foundInRaid + clearEquipment))).Replace("-", "");
    }

    private static List<(Item Item, string Slot)> Build(CommandRequest request)
    {
        var a = request.Args;
        var roots = new List<(Item, string)>();
        if (a.ClearEquipment && a.Items.Count == 0 && request.Kind is CommandKind.EquipKit or CommandKind.PreviewKit) return roots;
        if (request.Kind == CommandKind.SpawnItem)
        {
            if (string.IsNullOrWhiteSpace(a.TemplateId)) throw new InvalidDataException("Choose an item template.");
            var remaining = a.Count;
            while (remaining > 0)
            {
                var item = Factory.CreateItem(MongoID.Generate(), a.TemplateId, null) ?? throw new InvalidDataException("Unknown or non-spawnable template.");
                item.StackObjectsCount = Math.Min(remaining, Math.Max(1, item.StackMaxSize));
                remaining -= item.StackObjectsCount;
                roots.Add((item, ""));
            }
        }
        else
        {
            var remapped = ItemTrees.Remap(a.Items, () => MongoID.Generate());
            var records = new JArray(remapped.Select(r => new JObject
            {
                ["_id"] = r.Id, ["_tpl"] = r.TemplateId,
                ["parentId"] = string.IsNullOrEmpty(r.ParentId) ? null : r.ParentId,
                ["slotId"] = string.IsNullOrEmpty(r.ParentId) ? null : r.SlotId,
                ["location"] = string.IsNullOrEmpty(r.ParentId) || string.IsNullOrEmpty(r.LocationJson) ? null : JToken.Parse(r.LocationJson),
                ["upd"] = string.IsNullOrEmpty(r.StateJson) ? null : JToken.Parse(r.StateJson)
            }));
            var flat = records.ToObject<FlatItem[]>(Serializer);
            var tree = Factory.FlatItemsToTree(flat, false, null);
            if (tree.DeserializationErrors.Count > 0) throw new InvalidDataException("Kit could not be assembled: " + string.Join("; ", tree.DeserializationErrors));
            foreach (var root in remapped.Where(r => r.ParentId.Length == 0)) roots.Add((tree.Items[root.Id], root.SlotId));
        }
        foreach (var (root, _) in roots)
            foreach (var item in root.GetAllItems())
            {
                item.SpawnedInSession = a.FoundInRaid;
                if (request.Kind == CommandKind.SpawnItem && item.GetItemComponent<RepairableComponent>() is { } repair)
                    repair.Durability = repair.MaxDurability * Mathf.Clamp01(a.Value / 100);
            }
        return roots;
    }
    internal sealed class MenuTransaction
    {
        internal MenuInventorySync Sync;
        internal readonly List<IOperationResult> Operations = new();
        internal OfflineInventoryController Controller;

        internal void Notify(bool success)
        {
            if (success && Sync.RemovedIds.Count > 0)
            {
                var removed = Sync.RemovedIds.ToHashSet();
                var bindings = Controller.Inventory.FastAccess.BoundItems;
                foreach (var key in bindings.Where(p => p.Value != null && removed.Contains(p.Value.Id.ToString())).Select(p => p.Key).ToArray())
                    bindings.Remove(key);
            }
            foreach (var operation in Operations)
                foreach (var status in success
                    ? new[] { EFT.InventoryLogic.CommandStatus.Begin, EFT.InventoryLogic.CommandStatus.Succeed }
                    : new[] { EFT.InventoryLogic.CommandStatus.Failed })
                    try { operation.RaiseEvents(Controller, status); }
                    catch (Exception error) { Plugin.Log.LogError(error); }
            try { Controller.ReportProfileUpdate(); }
            catch (Exception error) { Plugin.Log.LogError(error); }
        }
        internal bool Rollback()
        {
            var succeeded = true;
            foreach (var operation in Operations.AsEnumerable().Reverse())
                try { ((IRollable)operation).RollBack(); }
                catch (Exception error) { succeeded = false; Plugin.Log.LogError(error); }
            return succeeded;
        }
    }
    internal static CommandResult PrepareMenuInventory(CommandRequest request, Profile profile, OfflineInventoryController controller, out MenuTransaction transaction)
    {
        transaction = null;
        if (!Commands.IsInventory(request.Kind) || request.Args.Destination is not ("Inventory" or "Stash"))
            return CommandResult.Reject(request, "Menu spawning only supports your stash.");
        var stash = profile?.Inventory?.Stash;
        if (stash == null || controller == null || profile.Id != request.TargetId || !ReferenceEquals(controller.Inventory, profile.Inventory))
            return CommandResult.Reject(request, "Your stash is not ready. Open your character inventory and retry.");
        if (controller.IsInventoryBlocked()) return CommandResult.Reject(request, "Wait for the current inventory action to finish.");

        var operations = new List<IOperationResult>();
        var sync = new MenuInventorySync { RequestId = request.RequestId, SessionId = request.SessionId,
            ActorId = request.ActorId, TargetId = request.TargetId, Kind = request.Kind, Args = request.Args };
        try
        {
            var roots = Build(request);
            var equipment = profile.Inventory.Equipment;
            if (request.Kind == CommandKind.EquipKit)
            {
                if (!request.Args.ConfirmReplacement || ReplacementFingerprint(equipment, profile.Id, request.Args.Items, request.Args.FoundInRaid, request.Args.ClearEquipment) != request.Args.ExpectedInventoryHash)
                    return CommandResult.Reject(request, "Equipment or kit changed since the preview. Preview the replacement again.");
                var plan = new InventoryDelta();
                Plan(equipment, request, roots, plan);
                foreach (var id in plan.RemovedRootIds)
                {
                    var item = equipment.Slots.Select(s => s.ContainedItem).Single(i => i != null && i.Id == id);
                    sync.RemovedIds.AddRange(item.GetAllItems().Select(i => i.Id.ToString()));
                    var removed = ItemManipulator.Discard(item, controller, false);
                    if (removed.Failed) throw new InvalidDataException(removed.Error.ToString());
                    operations.Add(removed.Value);
                }
            }
            // Synchronous on the game thread: each placement reserves space for the next
            // stack, and a full stash rolls the whole batch back before any UI events.
            foreach (var (item, slot) in roots)
            {
                ItemAddress address = request.Kind == CommandKind.EquipKit
                    ? equipment.Slots.Single(s => s.ID == slot).CreateItemAddress()
                    : stash.Grid.FindLocationForItem(item);
                if (address == null) throw new InvalidDataException("The stash does not have enough free space for all requested items.");
                var added = ItemManipulator.AddWithoutRestrictions(item, address, controller);
                if (added.Failed) throw new InvalidDataException(added.Error.ToString());
                operations.Add(added.Value);
            }
            // Capture the attached roots: unlike a reusable kit, this payload retains
            // the actual parent IDs, positions, child IDs, ammunition and condition.
            var flat = NativeItemSerialization.FlattenAttached(Factory, roots.Select(r => r.Item));
            foreach (var item in flat)
            {
                var data = JObject.FromObject(item, Serializer);
                sync.Added.Add(new DebugItemRecord { Id = item._id, TemplateId = item._tpl,
                    ParentId = item.parentId?.ToString() ?? "", SlotId = item.slotId ?? "",
                    LocationJson = data["location"]?.ToString(Formatting.None) ?? "",
                    StateJson = data["upd"]?.ToString(Formatting.None) ?? "" });
            }
            if (Encoding.UTF8.GetByteCount(JsonConvert.SerializeObject(sync)) > Protocol.MaxPayloadBytes)
                throw new InvalidDataException("Kit is too large to save.");
        }
        catch (Exception error)
        {
            var rollbackFailed = false;
            foreach (var operation in operations.AsEnumerable().Reverse())
                try { ((IRollable)operation).RollBack(); }
                catch (Exception rollback) { rollbackFailed = true; Plugin.Log.LogError(rollback); }
            Plugin.Log.LogError(error);
            return rollbackFailed
                ? new CommandResult { RequestId = request.RequestId, Status = CommandStatus.Uncertain, Message = "Inventory rollback was incomplete. Check your items before retrying. " + error.Message }
                : CommandResult.Reject(request, error.Message);
        }

        transaction = new MenuTransaction { Sync = sync, Controller = controller };
        transaction.Operations.AddRange(operations);
        return CommandResult.Complete(request, request.Kind == CommandKind.EquipKit ? "Kit equipped; displaced equipment deleted." : "Items added to your stash.");
    }

    private static async Task LoadResources(IEnumerable<Item> roots)
    {
        var resources = roots.SelectMany(r => r.GetAllItems()).SelectMany(i => i.Template.AllResources).ToHashSet();
        if (resources.Count == 0) return;
        await Singleton<ObjectsFactory>.Instance.LoadBundlesAndCreatePools(ObjectsFactory.PoolsCategory.Raid, ObjectsFactory.AssemblyType.Local, resources, Diz.Jobs.JobYieldPriority.Immediate, null, CancellationToken.None);
    }
    public static async Task<CommandResult> Execute(WorkbenchRuntime runtime, Player player, CommandRequest request)
    {
        if (Uncertain.Contains(player.ProfileId)) return CommandResult.Reject(request, "Inventory synchronization is uncertain. Leave the raid before further inventory debugging.");
        if (player.InventoryController.IsInventoryBlocked()) return CommandResult.Reject(request, "Finish the current inventory action first.");
        if (request.Kind == CommandKind.EquipKit && ReplacementFingerprint(player.Equipment, player.ProfileId, request.Args.Items, request.Args.FoundInRaid, request.Args.ClearEquipment) != request.Args.ExpectedInventoryHash) return CommandResult.Reject(request, "Equipment or kit changed since the preview. Preview the replacement again.");
        List<(Item Item, string Slot)> roots;
        try { roots = Build(request); }
        catch (Exception e) { return CommandResult.Reject(request, e.Message); }
        var delta = new InventoryDelta { RequestId = request.RequestId, SessionId = request.SessionId, TargetId = player.ProfileId };
        if (request.Args.Destination.Equals("Ground", StringComparison.OrdinalIgnoreCase))
        {
            if (request.Kind == CommandKind.EquipKit) return CommandResult.Reject(request, "Choose inventory when equipping a kit.");
            delta.Ground = true;
            var position = player.Position + player.Transform.Original.forward + Vector3.up;
            delta.X = position.x; delta.Y = position.y; delta.Z = position.z;
            delta.Roots = roots.Select(r => new InventoryRoot { Descriptor = Encode(r.Item) }).ToList();
        }
        else
        {
            try { Plan(player.Equipment, request, roots, delta); }
            catch (Exception e) { return CommandResult.Reject(request, e.Message); }
        }
        await LoadResources(roots.Select(r => r.Item));
        if (!runtime.InRaid || request.SessionId != runtime.SessionId || !player || !player.HealthController.IsAlive) return CommandResult.Reject(request, "Raid or target changed while loading items.");
        if (request.Kind == CommandKind.EquipKit && ReplacementFingerprint(player.Equipment, player.ProfileId, request.Args.Items, request.Args.FoundInRaid, request.Args.ClearEquipment) != request.Args.ExpectedInventoryHash) return CommandResult.Reject(request, "Equipment changed while loading. Preview again.");
        var payload = JsonConvert.SerializeObject(delta);
        if (Encoding.UTF8.GetByteCount(payload) > Protocol.MaxPayloadBytes) return CommandResult.Reject(request, "Kit is too large to synchronize.");
        if (runtime.IsCoop && Encoding.UTF8.GetByteCount(JsonConvert.SerializeObject(new { Id = new string('0', 32), Request = request, Delta = payload, World = false })) > Protocol.MaxPayloadBytes - 1024) return CommandResult.Reject(request, "Kit and command exceed the multiplayer packet limit.");
        var originalStatus = player.ProcessStatus;
        var acquired = false;
        try
        {
            if (!await ApplyReplica(runtime, payload, () =>
                {
                    if (!runtime.InRaid || runtime.SessionId != request.SessionId || !player.HealthController.IsAlive || (runtime.IsCoop && !runtime.CoopReady)) throw new InvalidOperationException("Raid, authority, or target changed while preparing items.");
                    if (player.InventoryController.IsInventoryBlocked()) throw new InvalidOperationException("Another inventory operation started while preparing items.");
                    if (request.Kind == CommandKind.EquipKit && ReplacementFingerprint(player.Equipment, player.ProfileId, request.Args.Items, request.Args.FoundInRaid, request.Args.ClearEquipment) != request.Args.ExpectedInventoryHash) throw new InvalidOperationException("Equipment changed after the preview. Preview the replacement again.");
                    originalStatus = player.ProcessStatus;
                    player.InventoryController.Locked = true;
                    acquired = true;
                })) return new CommandResult { RequestId = request.RequestId, Status = Uncertain.Contains(player.ProfileId) ? CommandStatus.Uncertain : CommandStatus.Rejected, Message = "Inventory validation or application failed; check the activity log." };
            if (runtime.IsCoop && (runtime.ReplicateInventory == null || !await runtime.ReplicateInventory(request, payload)))
            {
                Uncertain.Add(player.ProfileId);
                return new CommandResult { RequestId = request.RequestId, Status = CommandStatus.Uncertain, Message = "Applied locally, but a peer did not confirm the inventory update. Further inventory debugging is locked for this raid." };
            }
            return CommandResult.Complete(request, delta.Ground ? "Spawned on the ground." : request.Kind == CommandKind.EquipKit ? "Kit equipped; displaced equipment deleted." : "Items added to inventory.");
        }
        finally
        {
            if (acquired && player && (int)player.ProcessStatus == 2)
            {
                player.ProcessStatus = originalStatus;
                player.InventoryController.UpdateLockedStatus();
            }
        }
    }
    internal static void ValidateReplacement(InventoryEquipment equipment, string targetId, List<DebugItemRecord> items, bool foundInRaid, bool clearEquipment = false)
    {
        var request = new CommandRequest { TargetId = targetId, Kind = CommandKind.EquipKit,
            Args = new CommandArguments { Items = items, FoundInRaid = foundInRaid, ConfirmReplacement = true, ClearEquipment = clearEquipment } };
        Plan(equipment, request, Build(request), new InventoryDelta());
    }
    private static void Plan(InventoryEquipment equipment, CommandRequest request, List<(Item Item, string Slot)> roots, InventoryDelta delta)
    {
        if (request.Args.ClearEquipment)
        {
            foreach (var slot in equipment.Slots.Where(s => ItemTrees.LoadoutSlots.Contains(s.ID) && s.ContainedItem != null))
            {
                if (slot.Locked) throw new InvalidDataException("Equipment slot is locked: " + slot.ID);
                var removed = slot.RemoveItem(true);
                if (removed.Failed) throw new InvalidDataException(removed.Error.ToString());
                delta.RemovedRootIds.Add(slot.ContainedItem.Id);
            }
            return;
        }
        var clone = (InventoryEquipment)Decode(Encode(equipment));
        if (request.Kind == CommandKind.EquipKit)
        {
            foreach (var slotId in ItemTrees.ReplacementSlots(request.Args))
            {
                var slot = clone.Slots.FirstOrDefault(s => s.ID == slotId) ?? throw new InvalidDataException("Unknown equipment slot: " + slotId);
                if (slot.Locked || ItemTrees.ExcludedSlots.Contains(slot.ID)) throw new InvalidDataException("Protected equipment slot: " + slot.ID);
                if (slot.ContainedItem == null) continue;
                delta.RemovedRootIds.Add(slot.ContainedItem.Id);
                var removed = slot.RemoveItem(false);
                if (removed.Failed) throw new InvalidDataException(removed.Error.ToString());
            }
        }
        foreach (var entry in roots)
        {
            ItemAddress address;
            var test = Decode(Encode(entry.Item));
            if (request.Kind == CommandKind.EquipKit)
            {
                var slot = clone.Slots.FirstOrDefault(s => s.ID == entry.Slot) ?? throw new InvalidDataException("Unknown equipment slot: " + entry.Slot);
                if (slot.Locked) throw new InvalidDataException("Equipment slot is locked: " + slot.ID);
                address = slot.CreateItemAddress();
            }
            else address = FindFree(clone, test) ?? throw new InvalidDataException("Not enough inventory space. Choose ground spawning or clear space first.");
            var added = address.Add(test, false);
            if (added.Failed) throw new InvalidDataException(added.Error.ToString());
            var record = Capture(new[] { test }).Single(r => r.Id == test.Id);
            delta.Roots.Add(new InventoryRoot { Descriptor = Encode(entry.Item), ParentId = address.Container.ParentItem.Id, SlotId = address.Container.ID, LocationJson = record.LocationJson });
        }
    }
    private static ItemAddress FindFree(InventoryEquipment equipment, Item item)
    {
        foreach (var root in equipment.Slots.Where(s => s.ID != "SecuredContainer" && s.ContainedItem != null).Select(s => s.ContainedItem))
            foreach (var container in root.GetAllItems().OfType<CompoundItem>())
                foreach (var grid in container.Grids)
                {
                    var address = grid.FindLocationForItem(item);
                    if (address != null) return address;
                }
        return null;
    }
    internal static bool ValidateReplica(WorkbenchRuntime runtime, CommandRequest approved, string payload)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(payload) > Protocol.MaxPayloadBytes) return false;
            var delta = JsonConvert.DeserializeObject<InventoryDelta>(payload);
            if (delta == null || delta.RequestId != approved.RequestId || delta.SessionId != approved.SessionId || delta.TargetId != approved.TargetId || delta.Roots == null || delta.RemovedRootIds == null) return false;
            if (delta.Ground != approved.Args.Destination.Equals("Ground", StringComparison.OrdinalIgnoreCase)) return false;
            if (!Commands.IsInventory(approved.Kind)) return false;
            var expected = Build(approved);
            if (expected.Count != delta.Roots.Count || delta.RemovedRootIds.Distinct().Count() != delta.RemovedRootIds.Count) return false;
            if (approved.Kind != CommandKind.EquipKit && delta.RemovedRootIds.Count > 0) return false;
            var actual = delta.Roots.Select(r => (Item: Decode(r.Descriptor), Slot: approved.Kind == CommandKind.EquipKit ? r.SlotId : "")).ToList();
            if (actual.SelectMany(r => r.Item.GetAllItems()).Select(i => i.Id.ToString()).Distinct().Count() != actual.Sum(r => r.Item.GetAllItems().Count())) return false;
            if (actual.Sum(r => r.Item.GetAllItems().Count()) > Protocol.MaxItems) return false;
            string Shape(Item item)
            {
                var records = Capture(new[] { item });
                string Branch(DebugItemRecord node) => node.TemplateId + ":" + Canonical(string.IsNullOrEmpty(node.StateJson) ? new JObject() : JToken.Parse(node.StateJson)).ToString(Formatting.None) + "[" + string.Join("|", records.Where(r => r.ParentId == node.Id).Select(r => r.SlotId + ":" + r.LocationJson + ":" + Branch(r)).OrderBy(s => s, StringComparer.Ordinal)) + "]";
                return Branch(records.Single(r => r.ParentId.Length == 0));
            }
            var expectedShapes = expected.Select(r => (approved.Kind == CommandKind.EquipKit ? r.Slot : "") + Shape(r.Item)).OrderBy(s => s, StringComparer.Ordinal).ToArray();
            var actualShapes = actual.Select(r => r.Slot + Shape(r.Item)).OrderBy(s => s, StringComparer.Ordinal).ToArray();
            if (!expectedShapes.SequenceEqual(actualShapes)) return false;
            if (approved.Kind == CommandKind.EquipKit)
            {
                var player = runtime.FindPlayer(approved.TargetId); if (!player) return false;
                var slots = ItemTrees.ReplacementSlots(approved.Args);
                if (delta.Roots.Any(r => r.ParentId != player.Equipment.Id)) return false;
                if (approved.Args.ClearEquipment && !player.Equipment.Slots.Where(s => slots.Contains(s.ID) && s.ContainedItem != null)
                    .Select(s => s.ContainedItem.Id.ToString()).ToHashSet().SetEquals(delta.RemovedRootIds)) return false;
                foreach (var removed in delta.RemovedRootIds)
                    if (!player.Equipment.Slots.Any(s => slots.Contains(s.ID) && s.ContainedItem?.Id == removed)) return false;
            }
            return true;
        }
        catch (Exception e) { Plugin.Log.LogWarning("Rejected inventory replica: " + e.Message); return false; }
    }
    public static async Task<bool> ApplyReplica(WorkbenchRuntime runtime, string payload, Action beforeMutation = null)
    {
        if (payload == null || Encoding.UTF8.GetByteCount(payload) > Protocol.MaxPayloadBytes) return false;
        var delta = JsonConvert.DeserializeObject<InventoryDelta>(payload);
        if (delta == null || delta.SessionId != runtime.SessionId || !runtime.InRaid || delta.Roots == null || delta.Roots.Count > Protocol.MaxItems || delta.RemovedRootIds == null) return false;
        var key = delta.TargetId + ":" + delta.RequestId;
        if (Applied.Contains(key)) return true;
        var player = runtime.FindPlayer(delta.TargetId);
        if (!player) return false;
        if (GroundAttempts.Contains(key) || !Applying.Add(key)) return false;
        var operations = new List<IOperationResult>();
        try
        {
            var roots = delta.Roots.Select(r => (Item: Decode(r.Descriptor), Placement: r)).ToList();
            if (roots.SelectMany(r => r.Item.GetAllItems()).Count() > Protocol.MaxItems) return false;
            if (roots.SelectMany(r => r.Item.GetAllItems()).Any(i => player.Inventory.AllRealPlayerItems.Any(old => old.Id == i.Id))) return false;
            await LoadResources(roots.Select(r => r.Item));
            if (runtime.SessionId != delta.SessionId || !player) return false;
            if (delta.Ground)
            {
                if (!new[] { delta.X, delta.Y, delta.Z }.All(Commands.Finite)) return false;
                beforeMutation?.Invoke();
                GroundAttempts.Add(key); // A partially created batch must never be executed again.
                foreach (var root in roots)
                    if (!WorkbenchRuntime.World.ThrowItem(root.Item, player, Vector3.zero)) throw new InvalidDataException("The game could not create ground loot; inspect the existing batch before continuing.");
                Applied.Add(key);
                return true;
            }
            if (delta.RemovedRootIds.Count > 0 && player.HandsController?.Item != null && delta.RemovedRootIds.Any(id => player.HandsController.Item.GetRootItem().Id == id || player.HandsController.Item.Id == id))
            {
                var empty = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                player.SetEmptyHands(result => empty.TrySetResult(result.Succeed));
                if (await Task.WhenAny(empty.Task, Task.Delay(5000)) != empty.Task || !await empty.Task) return false;
            }
            if (runtime.SessionId != delta.SessionId || !player) return false;
            beforeMutation?.Invoke();
            foreach (var id in delta.RemovedRootIds)
            {
                var item = player.Equipment.Slots.Select(s => s.ContainedItem).FirstOrDefault(i => i != null && i.Id == id);
                if (item == null) throw new InvalidDataException("Replaced equipment no longer matches the preview.");
                var removed = ItemManipulator.Discard(item, player.InventoryController, false);
                if (removed.Failed) throw new InvalidDataException(removed.Error.ToString());
                operations.Add(removed.Value);
            }
            foreach (var (item, placement) in roots)
            {
                var parent = player.Equipment.GetAllItems().FirstOrDefault(i => i.Id == placement.ParentId) as ContainerCollection;
                var container = parent?.Containers.FirstOrDefault(c => c.ID == placement.SlotId) ?? throw new InvalidDataException("Destination container is missing.");
                ItemAddress address = container is Slot slot ? slot.CreateItemAddress() : container is Grid grid ? grid.CreateItemAddress(JsonConvert.DeserializeObject<LocationInGrid>(placement.LocationJson, EftJsonConverters.Converters)) : throw new InvalidDataException("Unsupported inventory destination.");
                var stash = Factory.CreateFakeStash(null);
                var staged = stash.Grid.AddAnywhere(item, (EErrorHandlingType)0);
                if (staged.Failed) throw new InvalidDataException(staged.Error.ToString());
                _ = new ItemController(stash, player.InventoryController.ID, "DebugPlus staging", false, EOwnerType.Profile);
                var moved = ItemManipulator.Move(item, address, player.InventoryController, false);
                if (moved.Failed) throw new InvalidDataException(moved.Error.ToString());
                operations.Add(moved.Value);
            }
            foreach (var operation in operations) operation.RaiseEvents(player.InventoryController, EFT.InventoryLogic.CommandStatus.Succeed);
            Applied.Add(key);
            return true;
        }
        catch (Exception e)
        {
            var rollbackFailed = false;
            foreach (var operation in operations.AsEnumerable().Reverse())
                try { ((IRollable)operation).RollBack(); operation.RaiseEvents(player.InventoryController, EFT.InventoryLogic.CommandStatus.Failed); }
                catch (Exception rollback) { rollbackFailed = true; Plugin.Log.LogError(rollback); }
            if (rollbackFailed || GroundAttempts.Contains(key)) Uncertain.Add(delta.TargetId);
            runtime.Report("Inventory update failed" + (delta.Ground ? "; partial ground loot may remain" : rollbackFailed ? "; rollback needs reconciliation" : " and was rolled back") + ": " + e.Message, CommandStatus.Uncertain);
            return false;
        }
        finally { Applying.Remove(key); }
    }
    internal static string Encode(Item item)
    {
        var writer = new NetworkWriter();
        writer.WriteEFTItemDescriptor(ItemBinarySerializer.SerializeItem(item, null));
        return Convert.ToBase64String(writer.ToArray());
    }
    internal static Item Decode(string encoded)
    {
        var reader = new NetworkReader(new ArraySegment<byte>(Convert.FromBase64String(encoded)));
        var descriptor = reader.ReadEFTItemDescriptor();
        if (reader.Remaining != 0) throw new InvalidDataException("Trailing native item data.");
        return descriptor.Deserialize(new Dictionary<MongoID, Item>());
    }
}
