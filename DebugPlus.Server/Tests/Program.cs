using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using DebugPlus.Common;
using DebugPlus.Server;
using HarmonyLib;
using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Models.Eft.Inventory;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services.Items;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;
using SPTarkov.Server.Core.Utils.Json;
using Path = System.IO.Path;

// Reads shipped database fixtures. It neither loads live profiles nor starts SPT.
var serverPath = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../../SPT_Runtime"));
var database = Path.Combine(serverPath, "SPT_Data/database/templates");
var json = new JsonUtil([new SptJsonConverterRegistrator()]);
var templates = json.Deserialize<Dictionary<MongoId, TemplateItem>>(File.ReadAllText(Path.Combine(database, "items.json")))!;
var table = (TemplateTable)RuntimeHelpers.GetUninitializedObject(typeof(TemplateTable));
typeof(TemplateTable).GetProperty(nameof(TemplateTable.Items))!.SetValue(table, templates);
var cloner = new SPTarkov.Server.Core.Utils.Cloners.FastCloner();
var bases = new ItemBaseClassService(null!, table, null!);
var itemHelper = new ItemHelper(null!, table, null!, null!, bases, null!, null!, null!, cloner);
var saves = Construct<SaveServer>(json, Array.Empty<SPTarkov.Server.Core.DI.SaveLoadRouter>());
var profiles = Construct<ProfileHelper>(saves, json, cloner);
var nativeInventory = Construct<InventoryHelper>(itemHelper, profiles, cloner);
var construction = new InventoryConstruction(itemHelper, nativeInventory, json);
var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(database, "profiles.json")))!["Standard"]!["usec"]!["character"]!;
var pmc = json.Deserialize<PmcData>(fixture.ToJsonString())!;
// Starter templates reuse placeholder IDs; real profile creation remaps them.
// This fixture keeps one copy of each unrelated stash placeholder before mutation checks.
pmc.Inventory!.Items = pmc.Inventory.Items!.DistinctBy(x => x.Id).ToList();
pmc.Id = new MongoId();
var session = new MongoId();
saves.AddProfile(new SptProfile { ProfileInfo = new SPTarkov.Server.Core.Models.Eft.Profile.Info { ProfileId = session },
    CharacterData = new Characters { PmcData = pmc, ScavData = new PmcData { Id = new MongoId(), Inventory = cloner.Clone(pmc.Inventory) } } });
var policy = new ServerPolicy();
policy.Initialize();
File.WriteAllText(Path.Combine(policy.DirectoryPath, "config.json"), json.Serialize(new ServerConfiguration()));
var service = new DebugService(saves, json, cloner, nativeInventory, construction, policy);
var serverAssembly = typeof(DebugService).Assembly;
var gateType = serverAssembly.GetType("DebugPlus.Server.ProfileGate")!;
gateType.GetProperty("Ready")!.SetValue(null, true);
var gate = gateType.GetMethod("For")!.Invoke(null, [session])!;
var configPath = Path.Combine(policy.DirectoryPath, "config.json");
var cases = 0;

Check(!service.Access(session).Authorized, "empty server allowlist denies access");
File.WriteAllText(configPath, json.Serialize(new ServerConfiguration { AdminProfileIds = [pmc.Id.ToString()!] }));
var access = service.Access(session);
Check(access.Authorized && access.ProfileId == pmc.Id.ToString() && access.SessionId.Length == 32, "authenticated PMC and server-owned menu session");
var headlessSession = new MongoId();
saves.AddProfile(new SptProfile { ProfileInfo = new SPTarkov.Server.Core.Models.Eft.Profile.Info { ProfileId = headlessSession } });
var headlessPolicy = service.Access(headlessSession);
Check(!headlessPolicy.Authorized && headlessPolicy.ProfileId == "" && headlessPolicy.SessionId.Length == 32
    && headlessPolicy.SessionId == service.Access(headlessSession).SessionId && headlessPolicy.AdminProfileIds.Contains(pmc.Id.ToString()!), "authenticated headless session receives read-only policy without mutation authority");
Check(service.Access(new MongoId()).AdminProfileIds.Length == 0, "unknown server session cannot read administrator policy");
var kit = pmc.Inventory!.Items!.Where(x => x.ParentId == pmc.Inventory.Equipment.ToString() && !ItemTrees.ExcludedSlots.Contains(x.SlotId!))
    .SelectMany(x => InventoryConstruction.Tree(x, pmc.Inventory.Items!)).Select(x => new DebugItemRecord
    {
        Id = x.Id.ToString(), TemplateId = x.Template.ToString(), ParentId = x.ParentId == pmc.Inventory.Equipment.ToString() ? "" : x.ParentId ?? "", SlotId = x.SlotId ?? "",
        LocationJson = x.Location == null ? "" : json.Serialize(x.Location)!, StateJson = x.Upd == null ? "" : json.Serialize(x.Upd)!
    }).ToList();
CommandRequest Request(CommandKind kind = CommandKind.EquipKit) => new()
{
    SessionId = access.SessionId, ActorId = "spoofed-actor", TargetId = pmc.Id.ToString()!, Kind = kind,
    Args = new CommandArguments { Items = cloner.Clone(kit)!, ConfirmReplacement = true }
};
var valid = Request();
var nativeTrees = construction.Build(valid);
construction.ValidateEquipment(pmc, nativeTrees);
Check(nativeTrees.SelectMany(x => x).All(x => kit.All(y => y.Id != x.Id.ToString())), "complete shipped equipment kit remaps all native IDs");
if (args.Length > 1)
{
    // Read-only replay of a reported saved kit; never write the user's kit/profile.
    var reportedKits = json.Deserialize<List<KitDocument>>(File.ReadAllText(args[1]))!;
    var equipment = pmc.Inventory.Items!.Single(i => i.Id == pmc.Inventory.Equipment);
    var slotNames = JsonNode.Parse(json.Serialize(templates[equipment.Template])!)!["_props"]!["Slots"]!.AsArray()
        .Select(s => s!["_name"]!.GetValue<string>()).ToArray();
    var validateChild = typeof(InventoryConstruction).GetMethod("ValidateChild", BindingFlags.Instance | BindingFlags.NonPublic)!;
    foreach (var reported in reportedKits)
    {
        ItemTrees.RepairRootSlots(reported.Items, root => slotNames.Where(slot =>
        {
            try { var child = new Item { Id = new MongoId(root.Id), Template = new MongoId(root.TemplateId), SlotId = slot };
                validateChild.Invoke(construction, [child, equipment, new List<Item> { child }]); return true; }
            catch (TargetInvocationException) { return false; }
        }).ToArray());
        Check(reported.Items.Where(i => i.ParentId == "").All(i => i.SlotId.Length > 0), "reported saved kit recovers all missing equipment slots");
        var excluded = reported.Items.Where(i => i.ParentId == "" && ItemTrees.ExcludedSlots.Contains(i.SlotId)).Select(i => i.Id).ToHashSet();
        var remaining = reported.Items.Where(i => !excluded.Contains(i.Id) && !excluded.Contains(i.ParentId)).ToList();
        if (remaining.Count == 0) continue;
        var repairedRequest = Request(); repairedRequest.Args.Items = remaining;
        var repairedTrees = construction.Build(repairedRequest);
        construction.ValidateEquipment(pmc, repairedTrees);
        Check(repairedTrees.Sum(t => t.Count) == remaining.Count, "reported kit passes native equipment validation with all attachments and ammunition retained");
    }
}
var preview = await service.Preview(session, valid);
Check(preview.Hash.Length == 64 && preview.Summary.Contains("nested contents"), "replacement preview includes displaced subtree and authoritative hash");
var original = json.Serialize(pmc.Inventory);
var stale = await service.Execute(session, valid);
Check(stale.Status == CommandStatus.Rejected && json.Serialize(pmc.Inventory) == original, "stale preview leaves inventory untouched");
var wrongTarget = Request(); wrongTarget.TargetId = new MongoId().ToString();
Check((await service.Execute(session, wrongTarget)).Status == CommandStatus.Rejected, "menu cannot mutate another target");
var expired = Request(); expired.SessionId = "expired";
Check((await service.Execute(session, expired)).Status == CommandStatus.Rejected, "stale sessions are rejected");
gateType.GetProperty("InRaid")!.SetValue(gate, true);
Check((await service.Execute(session, Request())).Status == CommandStatus.Rejected, "native raid state rejects menu inventory mutations");
gateType.GetProperty("InRaid")!.SetValue(gate, false);

var invalid = Request(); invalid.Args.Items[0].TemplateId = "invalid";
Rejects(() => construction.Build(invalid), "unknown templates rejected before inventory mutation");
var badSlot = Request(); badSlot.Args.Items.First(x => x.ParentId != "").SlotId = "not-a-native-slot";
Rejects(() => construction.Build(badSlot), "native slot restrictions preserved");
var cyclic = Request(); cyclic.Args.Items[0].ParentId = cyclic.Args.Items[0].Id;
Rejects(() => construction.Build(cyclic), "cyclic item trees rejected");
var stack = Request(CommandKind.SpawnItem); stack.Args.TemplateId = "54527a984bdc2d4e668b4567"; stack.Args.Count = 100;
var split = construction.Build(stack);
Check(split.Sum(x => x[0].Upd!.StackObjectsCount) == 100 && split.Count > 1 && split.All(x => x[0].Upd!.SpawnedInSession == false), "stack quantities split at native capacity and FIR defaults false");

var harmony = new Harmony("debugplus.tests.storage");
harmony.Patch(typeof(SaveServer).GetMethod(nameof(SaveServer.SaveProfileAsync))!, prefix: new HarmonyMethod(typeof(SaveFault).GetMethod(nameof(SaveFault.Prefix))!));
var replace = Request(); replace.Args.ExpectedInventoryHash = (await service.Preview(session, replace)).Hash;
SaveFault.FailuresRemaining = 1;
var rolledBack = await service.Execute(session, replace);
Console.WriteLine($"Storage fixture: {rolledBack.Status}; save calls {SaveFault.Calls}; {rolledBack.Message}");
Check(rolledBack.Status == CommandStatus.Rejected && json.Serialize(pmc.Inventory) == original && SaveFault.Calls == 2, "failed publication restores full inventory before successful recovery save");
var success = Request(); success.Args.ExpectedInventoryHash = (await service.Preview(session, success)).Hash;
var completed = await service.Execute(session, success);
Check(completed.Status == CommandStatus.Completed && json.Serialize(pmc.Inventory) != original, "equipment replacement commits after native validation");
var committedInventory = json.Serialize(pmc.Inventory);
var repeat = await service.Execute(session, success);
Check(repeat.Status == CommandStatus.Completed && json.Serialize(pmc.Inventory) == committedInventory, "same request ID does not replace or duplicate items again");
var stashRequest = Request(CommandKind.SpawnItem); stashRequest.Args.TemplateId = "54527a984bdc2d4e668b4567"; stashRequest.Args.Count = 100;
var itemCount = pmc.Inventory.Items!.Count;
var stashResult = await service.Execute(session, stashRequest);
Check(stashResult.Status == CommandStatus.Completed && pmc.Inventory.Items!.Count == itemCount + 2, "menu spawning uses native stash placement with split stacks");
var stashPayload = System.Text.Json.Nodes.JsonNode.Parse(stashResult.DataJson)!;
var deltaProperty = stashPayload.AsObject().Single(x => x.Key.Equals("ItemChanges", StringComparison.OrdinalIgnoreCase)).Value!;
Check(deltaProperty["new"]!.AsArray().Count == 2 &&
    !stashPayload.AsObject().Single(x => x.Key.Equals("RefreshBackend", StringComparison.OrdinalIgnoreCase)).Value!.GetValue<bool>(),
    "stash add returns native item changes without requesting a backend reload");
Check((await service.Execute(session, stashRequest)).DataJson == stashResult.DataJson,
    "duplicate stash request returns the same delta without duplicating saved items");

MenuInventorySync ClientBatch(CommandKind kind)
{
    var request = Request(kind);
    request.Args.TemplateId = "54527a984bdc2d4e668b4567";
    request.Args.Count = 100;
    var trees = construction.Build(request);
    var staged = cloner.Clone(pmc)!;
    var output = new ItemEventRouterResponse { Warnings = [], ProfileChanges = new() { [session] = new ProfileChange {
        Id = session, Items = new ItemChanges { NewItems = [], ChangedItems = [], DeletedItems = [] } } } };
    var sync = new MenuInventorySync { SessionId = access.SessionId, ActorId = pmc.Id.ToString()!, TargetId = pmc.Id.ToString()!, Kind = kind, Args = request.Args };
    if (kind == CommandKind.EquipKit)
    {
        var slots = trees.Select(t => t[0].SlotId).ToHashSet();
        sync.RemovedIds = pmc.Inventory.Items!.Where(i => i.ParentId == pmc.Inventory.Equipment.ToString() && slots.Contains(i.SlotId))
            .SelectMany(i => InventoryConstruction.Tree(i, pmc.Inventory.Items!)).Select(i => i.Id.ToString()).ToList();
        foreach (var tree in trees) { tree[0].ParentId = pmc.Inventory.Equipment.ToString(); tree[0].Location = null; }
    }
    else foreach (var tree in trees) nativeInventory.AddItemToStash(session,
        new AddItemDirectRequest { ItemWithModsToAdd = tree, FoundInRaid = request.Args.FoundInRaid, UseSortingTable = false }, staged, output);
    var built = trees.SelectMany(t => t).ToList();
    var attached = kind == CommandKind.EquipKit ? built : staged.Inventory!.Items!.Where(i => built.Any(b => b.Id == i.Id)).ToList();
    sync.Added = attached.Select(i => new DebugItemRecord { Id = i.Id.ToString(), TemplateId = i.Template.ToString(),
        ParentId = i.ParentId ?? "", SlotId = i.SlotId ?? "", LocationJson = i.Location == null ? "" : json.Serialize(i.Location)!, StateJson = json.Serialize(i.Upd)! }).ToList();
    return sync;
}
var clientAmmo = ClientBatch(CommandKind.SpawnItem);
var synchronizedAmmo = await service.Synchronize(session, clientAmmo);
Check(synchronizedAmmo.Status == CommandStatus.Completed, "client-created ammo synchronizes: " + synchronizedAmmo.Message);
Check(clientAmmo.Added.All(r => pmc.Inventory.Items!.Any(i => i.Id.ToString() == r.Id && json.Serialize(i.Location) == r.LocationJson)),
    "client IDs and stash positions are preserved for subsequent native moves and splits");
var afterSync = json.Serialize(pmc.Inventory);
Check((await service.Synchronize(session, clientAmmo)).Status == CommandStatus.Completed && json.Serialize(pmc.Inventory) == afterSync,
    "lost acknowledgement retry does not duplicate a client-created batch");
var nativeController = Construct<SPTarkov.Server.Core.Controllers.InventoryController>(table, nativeInventory, profiles, itemHelper, cloner, Construct<HttpResponseUtil>(json));
ItemLocation FreeStashLocation()
{
    var map = (int[,])typeof(InventoryHelper).GetMethod("GetStashSlotMap", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(nativeInventory, [pmc])!;
    for (var y = 0; y < map.GetLength(0); y++) for (var x = 0; x < map.GetLength(1); x++)
        if (map[y, x] == 0) return new ItemLocation { X = x, Y = y };
    throw new Exception("No free fixture cell.");
}
ItemEventRouterResponse NativeOutput() => new() { Warnings = [], ProfileChanges = new() { [session] = new ProfileChange {
    Id = session, Items = new ItemChanges { NewItems = [], ChangedItems = [], DeletedItems = [] } } } };
var nativeSplitId = new MongoId();
var splitOutput = NativeOutput();
nativeController.SplitItem(pmc, new InventorySplitRequestData { Action = "Split", SplitItem = new MongoId(clientAmmo.Added[0].Id), NewItem = nativeSplitId, Count = 1,
    Container = new Container { Id = pmc.Inventory.Stash.ToString(), ContainerName = "hideout", Location = FreeStashLocation() } }, session, splitOutput);
Check(splitOutput.Warnings!.Count == 0 && pmc.Inventory.Items!.Any(i => i.Id == nativeSplitId && i.Upd!.StackObjectsCount == 1),
    "native backend splits synchronized client ammo without the trader-item error");
var moveOutput = NativeOutput();
var moveLocation = FreeStashLocation();
nativeController.MoveItem(pmc, new InventoryMoveRequestData { Action = "Move", Item = nativeSplitId,
    To = new SPTarkov.Server.Core.Models.Eft.Inventory.To { Id = pmc.Inventory.Stash.ToString(), Container = "hideout", Location = moveLocation } }, session, moveOutput);
Check(moveOutput.Warnings!.Count == 0 && json.Serialize(pmc.Inventory.Items!.Single(i => i.Id == nativeSplitId).Location) == json.Serialize(moveLocation),
    "native backend moves the split client-created item without the trader-item error");
afterSync = json.Serialize(pmc.Inventory);
var overlappingBatch = ClientBatch(CommandKind.SpawnItem);
overlappingBatch.Added[1].LocationJson = overlappingBatch.Added[0].LocationJson;
Check((await service.Synchronize(session, overlappingBatch)).Status == CommandStatus.Rejected && json.Serialize(pmc.Inventory) == afterSync,
    "overlapping client positions reject the entire batch without publishing partial items");
var positionedBatch = ClientBatch(CommandKind.SpawnItem);
var positionMap = (int[,])typeof(InventoryHelper).GetMethod("GetStashSlotMap", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(nativeInventory, [pmc])!;
var positions = new List<ItemLocation>();
for (var y = positionMap.GetLength(0) - 1; y >= 0 && positions.Count < positionedBatch.Added.Count; y--)
    for (var x = positionMap.GetLength(1) - 1; x >= 0 && positions.Count < positionedBatch.Added.Count; x--)
        if (positionMap[y, x] == 0) positions.Add(new ItemLocation { X = x, Y = y });
for (var i = 0; i < positions.Count; i++) positionedBatch.Added[i].LocationJson = json.Serialize(positions[i])!;
Check((await service.Synchronize(session, positionedBatch)).Status == CommandStatus.Completed
    && positionedBatch.Added.All(r => json.Serialize(pmc.Inventory.Items!.Single(i => i.Id.ToString() == r.Id).Location) == r.LocationJson),
    "valid client positions are retained even when different from server automatic placement");
afterSync = json.Serialize(pmc.Inventory);
var invalidSync = ClientBatch(CommandKind.SpawnItem); invalidSync.Added[0].ParentId = pmc.Inventory.Equipment.ToString()!;
Check((await service.Synchronize(session, invalidSync)).Status == CommandStatus.Rejected && json.Serialize(pmc.Inventory) == afterSync,
    "invalid client destination leaves the saved inventory unchanged");
var stashKit = ClientBatch(CommandKind.SpawnKit);
var stashKitResult = await service.Synchronize(session, stashKit);
Check(stashKitResult.Status == CommandStatus.Completed && stashKit.Added.All(r => pmc.Inventory.Items!.Any(i => i.Id.ToString() == r.Id && i.ParentId == r.ParentId)),
    "client kit spawning retains stash roots and nested parents: " + stashKitResult.Message);
var clientKit = ClientBatch(CommandKind.EquipKit);
var synchronizedKit = await service.Synchronize(session, clientKit);
Check(synchronizedKit.Status == CommandStatus.Completed && clientKit.Added.All(r => pmc.Inventory.Items!.Any(i => i.Id.ToString() == r.Id))
    && clientKit.RemovedIds.All(id => pmc.Inventory.Items!.All(i => i.Id.ToString() != id)),
    "client equipment replacement preserves every nested ID and removes displaced trees: " + synchronizedKit.Message);
var beforeSyncFailure = json.Serialize(pmc.Inventory);
// Empty loadouts are explicit removal-only transactions, not missing item data.
var beforeClear = cloner.Clone(pmc.Inventory)!;
var clear = Request(); clear.Args.Items.Clear(); clear.Args.ClearEquipment = true;
var clearPreview = await service.Preview(session, clear);
Check(clearPreview.Hash.Length > 0 && clearPreview.Summary.Contains("DELETE", StringComparison.OrdinalIgnoreCase), "empty loadout previews the actual displaced equipment");
var clearedIds = pmc.Inventory.Items!.Where(i => i.ParentId == pmc.Inventory.Equipment.ToString() && ItemTrees.LoadoutSlots.Contains(i.SlotId!))
    .SelectMany(i => InventoryConstruction.Tree(i, pmc.Inventory.Items!)).Select(i => i.Id.ToString()).ToHashSet();
var keptItems = json.Serialize(pmc.Inventory.Items!.Where(i => !clearedIds.Contains(i.Id.ToString())).ToList());
var clearBatch = new MenuInventorySync { SessionId = access.SessionId, ActorId = pmc.Id.ToString()!, TargetId = pmc.Id.ToString()!, Kind = CommandKind.EquipKit,
    Args = clear.Args, RemovedIds = clearedIds.ToList() };
clearBatch.Args.ExpectedInventoryHash = clearPreview.Hash;
var invalidClear = json.Deserialize<MenuInventorySync>(json.Serialize(clearBatch)!)!;
invalidClear.RequestId = Guid.NewGuid().ToString("N");
invalidClear.RemovedIds.Add(pmc.Inventory.Items!.First(i => i.ParentId == pmc.Inventory.Equipment.ToString() && ItemTrees.ExcludedSlots.Contains(i.SlotId!)).Id.ToString());
Check((await service.Synchronize(session, invalidClear)).Status == CommandStatus.Rejected && json.Serialize(pmc.Inventory) == json.Serialize(beforeClear), "empty loadout cannot remove protected slots");
SaveFault.FailuresRemaining = 1;
var failedClear = await service.Synchronize(session, clearBatch);
Check(failedClear.Status == CommandStatus.Rejected && json.Serialize(pmc.Inventory) == json.Serialize(beforeClear), "empty loadout save failure restores complete original gear");
clearBatch.RequestId = Guid.NewGuid().ToString("N");
var clearResult = await service.Synchronize(session, clearBatch);
Check(clearResult.Status == CommandStatus.Completed && json.Serialize(pmc.Inventory.Items) == keptItems, "empty loadout removes worn gear and contents while preserving every other item");
Check((await service.Synchronize(session, clearBatch)).Status == CommandStatus.Completed && json.Serialize(pmc.Inventory.Items) == keptItems, "empty loadout retry is idempotent");
pmc.Inventory = beforeClear;
clear.Args.ExpectedInventoryHash = "stale";
Check((await service.Execute(session, clear)).Status == CommandStatus.Rejected, "empty loadout rejects stale preview before removal");
clear.RequestId = Guid.NewGuid().ToString("N"); clear.Args.ExpectedInventoryHash = (await service.Preview(session, clear)).Hash;
Check((await service.Execute(session, clear)).Status == CommandStatus.Completed && json.Serialize(pmc.Inventory.Items) == keptItems, "empty loadout preview-to-confirmation removes only listed gear");
pmc.Inventory = cloner.Clone(beforeClear)!;
var failedClientBatch = ClientBatch(CommandKind.SpawnItem);
SaveFault.FailuresRemaining = 1;
Check((await service.Synchronize(session, failedClientBatch)).Status == CommandStatus.Rejected && json.Serialize(pmc.Inventory) == beforeSyncFailure,
    "client synchronization save failure restores the original profile");
var inventoryBeforeFullFixture = cloner.Clone(pmc.Inventory)!;
var stashMap = (int[,])typeof(InventoryHelper).GetMethod("GetStashSlotMap", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(nativeInventory, [pmc])!;
var stashIds = pmc.Inventory.Items!.Where(x => x.ParentId == pmc.Inventory.Stash.ToString())
    .SelectMany(x => InventoryConstruction.Tree(x, pmc.Inventory.Items!)).Select(x => x.Id).ToHashSet();
pmc.Inventory.Items!.RemoveAll(x => stashIds.Contains(x.Id));
for (var y = 0; y < stashMap.GetLength(0); y++) for (var x = 0; x < stashMap.GetLength(1); x++)
    pmc.Inventory.Items.Add(new Item { Id = new MongoId(), Template = "54527a984bdc2d4e668b4567", ParentId = pmc.Inventory.Stash.ToString(), SlotId = "hideout", Location = new ItemLocation { X = x, Y = y }, Upd = new Upd { StackObjectsCount = 1 } });
var fullBefore = json.Serialize(pmc.Inventory);
var noSpace = Request(CommandKind.SpawnItem); noSpace.Args.TemplateId = "54527a984bdc2d4e668b4567";
var fullResult = await service.Execute(session, noSpace);
Check(fullResult.Status == CommandStatus.Rejected && fullResult.Message.Contains("free space") && fullBefore == json.Serialize(pmc.Inventory), "full stash rejects complete operation without partial insertion");
pmc.Inventory = inventoryBeforeFullFixture;
var uncertain = Request(); uncertain.Args.ExpectedInventoryHash = (await service.Preview(session, uncertain)).Hash;
SaveFault.FailuresRemaining = 2;
var uncertainResult = await service.Execute(session, uncertain);
Check(uncertainResult.Status == CommandStatus.Uncertain && File.Exists(Path.Combine(policy.DirectoryPath, "recovery", session + ".json")), "failed save and rollback retain durable recovery journal and uncertain outcome");
Check((await service.Execute(session, Request())).Status == CommandStatus.Rejected, "unresolved recovery blocks further inventory mutation");
gateType.GetProperty("NeedsRecovery")!.SetValue(gate, false);
var recoveredPreview = await service.Preview(session, Request());
Check(recoveredPreview.Hash.Length == 64 && !File.Exists(Path.Combine(policy.DirectoryPath, "recovery", session + ".json")), "reconciled before-state clears journal without replaying items");
File.WriteAllText(configPath, json.Serialize(new ServerConfiguration()));
Check(!service.Access(session).Authorized && (await service.Execute(session, Request())).Status == CommandStatus.Rejected, "server allowlist revocation takes immediate effect");

// Applying the actual patches catches incompatible native parameter/return signatures offline.
new Harmony("debugplus.tests.lifecycle").PatchAll(serverAssembly);
Check(true, $"native raid and inventory Harmony signatures bind on SPT {typeof(SaveServer).Assembly.GetName().Version}");
Console.WriteLine($"PASS: {cases} focused server checks against shipped SPT templates; no live profiles loaded or written.");

void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL: " + name); cases++; Console.WriteLine("PASS: " + name); }
void Rejects(Action action, string name) { try { action(); } catch (InvalidOperationException) { Check(true, name); return; } throw new Exception("FAIL: " + name); }
static T Construct<T>(params object[] services)
{
    var constructor = typeof(T).GetConstructors().Single();
    return (T)constructor.Invoke(constructor.GetParameters().Select(p => services.FirstOrDefault(s => p.ParameterType.IsInstanceOfType(s))
        ?? (p.ParameterType.IsInterface && p.ParameterType.Name.StartsWith("ISptLogger") ? DispatchProxy.Create(p.ParameterType, typeof(NullLogger)) : p.HasDefaultValue ? p.DefaultValue : null)).ToArray());
}
public static class SaveFault
{
    public static int Calls;
    public static int FailuresRemaining;
    public static bool Prefix(ref Task<long> __result)
    {
        Calls++;
        __result = FailuresRemaining-- > 0 ? Task.FromException<long>(new IOException("Injected storage failure")) : Task.FromResult(0L);
        return false;
    }
}
public class NullLogger : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.ReturnType == typeof(bool) ? false : null;
}
