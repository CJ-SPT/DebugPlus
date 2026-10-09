using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using DebugPlus.Runtime;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using DebugPlus.Common;
using Newtonsoft.Json;

var game = args.Length > 0 ? Path.GetFullPath(Path.Combine(args[0], "..")) : "F:/SPT 4.1.x";
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    foreach (var folder in new[] { "EscapeFromTarkov_Data/Managed", "BepInEx/core", "BepInEx/plugins/spt" })
    {
        var path = Path.Combine(game, folder, name.Name + ".dll");
        if (File.Exists(path)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    }
    return null;
};
Run();
CheckEmptyLoadoutPlan();
CheckAvailability(args.Length > 1 ? args[1] : null);

[MethodImpl(MethodImplOptions.NoInlining)]
static void CheckEmptyLoadoutPlan()
{
    var equipment = Bare<InventoryEquipment>();
    Field(equipment, "<Id>k__BackingField", "222222222222222222222222");
    var template = Bare<ItemTemplate>(); template._id = "5447a9cd4bdc2dbd208b4567";
    Field(equipment, "<Template>k__BackingField", template);
    var slots = new List<Slot>();
    foreach (var slotId in new[] { "FirstPrimaryWeapon", "Backpack", "Pockets", "SecuredContainer", "Dogtag", "SpecialSlot1" })
    {
        var item = Bare<Item>(); Field(item, "<Id>k__BackingField", MongoID.Generate().ToString()); Field(item, "<Template>k__BackingField", template);
        var slot = Bare<Slot>(); Field(slot, "<ID>k__BackingField", slotId); Field(slot, "<ParentItem>k__BackingField", equipment);
        Field(slot, "<ContainedItem>k__BackingField", item); item.CurrentAddress = slot.CreateItemAddress(); slots.Add(slot);
    }
    equipment.Slots = slots.ToArray(); equipment.Grids = Array.Empty<Grid>();
    // Examination state belongs to the live player; removal simulation itself is native.
    new Harmony("debugplus.empty.plan").Patch(AccessTools.Method(typeof(Slot), "Examined"), prefix: new HarmonyMethod(typeof(AvailabilityFixture), nameof(AvailabilityFixture.Examined)));
    var actions = typeof(WorkbenchRuntime).Assembly.GetType("DebugPlus.Runtime.InventoryActions");
    var plan = AccessTools.Method(actions, "Plan");
    var request = new CommandRequest { Kind = CommandKind.EquipKit, Args = new() { ClearEquipment = true, ConfirmReplacement = true } };
    var delta = new InventoryDelta();
    plan.Invoke(null, new object[] { equipment, request, new List<(Item, string)>(), delta });
    Check(delta.RemovedRootIds.ToHashSet().SetEquals(slots.Take(2).Select(s => s.ContainedItem.Id.ToString())) && slots.All(s => s.ContainedItem != null),
        "actual native empty-loadout preview plans worn gear removal without mutating or including protected slots");
    Field(slots[0], "<Locked>k__BackingField", true);
    try { plan.Invoke(null, new object[] { equipment, request, new List<(Item, string)>(), new InventoryDelta() }); throw new Exception("Locked equipment accepted"); }
    catch (TargetInvocationException e) when (e.InnerException is InvalidDataException) { Check(slots.All(s => s.ContainedItem != null), "locked empty-loadout preview rejects without mutation"); }
    Check(ItemTrees.LoadoutSlots.All(s => Enum.IsDefined(typeof(EquipmentSlot), s)), "all empty-loadout slot names exist in installed EFT");
}

[MethodImpl(MethodImplOptions.NoInlining)]
static void CheckAvailability(string kitPath)
{
    // Only replace the live Unity/session context; execute the production
    // command availability method, including its argument validation.
    var harmony = new Harmony("debugplus.availability.fixture");
    harmony.Patch(typeof(WorkbenchRuntime).GetMethod(nameof(WorkbenchRuntime.Availability)), prefix: new HarmonyMethod(typeof(AvailabilityFixture), nameof(AvailabilityFixture.Context)));
    harmony.Patch(AccessTools.PropertyGetter(typeof(WorkbenchRuntime), nameof(WorkbenchRuntime.InRaid)), prefix: new HarmonyMethod(typeof(AvailabilityFixture), nameof(AvailabilityFixture.Menu)));
    harmony.Patch(AccessTools.PropertyGetter(typeof(WorkbenchRuntime), nameof(WorkbenchRuntime.LocalProfileId)), prefix: new HarmonyMethod(typeof(AvailabilityFixture), nameof(AvailabilityFixture.Identity)));
    var runtime = Bare<WorkbenchRuntime>();
    var windowType = typeof(WorkbenchRuntime).Assembly.GetType("DebugPlus.UI.WorkbenchWindow");
    var window = RuntimeHelpers.GetUninitializedObject(windowType);
    var uiAvailability = AccessTools.Method(windowType, "ActionUnavailable");
    string ButtonReason(CommandKind kind, CommandArguments arguments) => (string)uiAvailability.Invoke(window, new object[] { kind, arguments });
    Field(runtime, "<Policy>k__BackingField", new AccessPolicy { Authorized = true, SessionId = "fixture", ProfileId = "pmc" });
    var kits = kitPath == null ? new List<KitDocument> { new() { Name = "Nested fixture", Items = new() {
        new() { Id = "rifle", TemplateId = "rifle", SlotId = "FirstPrimaryWeapon" },
        new() { Id = "mag", TemplateId = "mag", ParentId = "rifle", SlotId = "mod_magazine" } } },
        new() { Name = "Dogtag-only fixture", Items = new() { new() { Id = "tag", TemplateId = "tag", SlotId = "Dogtag" } } } }
        : JsonConvert.DeserializeObject<List<KitDocument>>(File.ReadAllText(kitPath));
    foreach (var kit in kits)
    {
        var selected = ItemTrees.SelectEquipment(kit.Items);
        if (selected.Count == 0)
        {
            Check(ButtonReason(CommandKind.SpawnKit, new() { Items = selected }).Contains("No equipment"), kit.Name + " explains its empty equipment selection");
            Check(ButtonReason(CommandKind.PreviewKit, new() { Items = selected }) == "", kit.Name + " actual UI permits preview of an empty selection");
            var emptyPreview = runtime.PreviewReplacementAsync("pmc", selected).GetAwaiter().GetResult();
            Check(string.IsNullOrEmpty(emptyPreview.Hash) && emptyPreview.Summary == WorkbenchAvailability.EmptyKitPreview,
                kit.Name + " actual preview reports no changes without inventory or backend access");
            Check(!WorkbenchAvailability.KitPreviewPrompt(false).Contains("Preview required")
                && ButtonReason(CommandKind.EquipKit, new() { Items = selected }).Contains("No equipment"), "empty kit never requests an impossible replacement preview");
            var clear = new CommandArguments { ClearEquipment = true, Items = selected, ConfirmReplacement = true };
            Check(ButtonReason(CommandKind.PreviewKit, clear) == "" && ButtonReason(CommandKind.EquipKit, clear).Contains("Preview"), "empty loadout can preview but cannot clear without confirmation hash");
            clear.ExpectedInventoryHash = "preview";
            Check(ButtonReason(CommandKind.EquipKit, clear) == "" && runtime.CommandUnavailable(CommandKind.EquipKit, clear, "pmc") == "", "empty loadout enables actual confirmation and runtime execution after preview");
            continue;
        }
        Check(ItemTrees.SelectEquipment(kit.Items, new HashSet<string>()).Count == 0, "unchecked equipment excludes its complete descendant trees");
        foreach (var kind in new[] { CommandKind.SpawnKit, CommandKind.PreviewKit, CommandKind.EquipKit })
        {
            var arguments = new CommandArguments { Items = selected, ExpectedInventoryHash = kind == CommandKind.EquipKit ? "preview" : "", ConfirmReplacement = true };
            var reason = runtime.CommandUnavailable(kind, arguments, "pmc");
            Check(reason == "", kit.Name + " production " + kind + " availability: " + reason);
            Check(ButtonReason(kind, arguments) == "", kit.Name + " actual UI " + kind + " button permits execution without cached runtime availability");
        }
        Check(ButtonReason(CommandKind.EquipKit, new() { Items = selected }).Contains("Preview"), "actual UI still requires replacement preview");
        Field(window, "_busy", true);
        Check(ButtonReason(CommandKind.SpawnKit, new() { Items = selected }).Contains("Wait"), "actual UI prevents duplicate in-flight spawns");
        Field(window, "_busy", false);
        Check(ButtonReason(CommandKind.SpawnKit, new() { Items = selected }) == "", "actual UI re-enables spawning after busy state clears");
    }
}

[MethodImpl(MethodImplOptions.NoInlining)]
static void Run()
{
    // Stub unrelated component-state collection only. Execute the installed EFT
    // tree serializer and the production root-address adapter on native objects.
    new Harmony("debugplus.capture.fixture").Patch(typeof(ItemFactory).GetMethod(nameof(ItemFactory.GetDiff)),
        prefix: new HarmonyMethod(typeof(StateFixture).GetMethod(nameof(StateFixture.Prefix))));
    var factory = Bare<ItemFactory>();
    var root = Bare<Item>();
    var equipment = Bare<Item>();
    Field(root, "<Id>k__BackingField", "111111111111111111111111");
    Field(equipment, "<Id>k__BackingField", "222222222222222222222222");
    var template = Bare<ItemTemplate>(); template._id = "5447a9cd4bdc2dbd208b4567";
    Field(root, "<Template>k__BackingField", template);
    var slot = Bare<Slot>();
    Field(slot, "<ParentItem>k__BackingField", equipment);
    Field(slot, "<ID>k__BackingField", "FirstPrimaryWeapon");
    root.CurrentAddress = slot.CreateItemAddress();
    var raw = factory.TreeToFlatItems(new[] { root }).Single();
    Check(raw.parentId == null && raw.slotId == null, "native serializer reproduces missing root equipment slot");
    var repaired = NativeItemSerialization.FlattenAttached(factory, new[] { root }).Single();
    Check(repaired.parentId?.ToString() == equipment.Id && repaired.slotId == "FirstPrimaryWeapon" && repaired._id.ToString() == root.Id,
        "production adapter restores native equipment root parent and slot without changing ID");
    var stash = Bare<CompoundItem>(); Field(stash, "<Id>k__BackingField", "333333333333333333333333");
    Field(stash, "<Template>k__BackingField", template);
    var grid = Bare<Grid>(); grid._parentItem = stash; grid._iD = "hideout";
    var collection = new GridItemCollection();
    grid._itemCollection = collection;
    var location = new LocationInGrid(3, 7, ItemRotation.Vertical);
    root.CurrentAddress = null;
    collection.Add(root, grid, location);
    root.CurrentAddress = grid.CreateItemAddress(location);
    var placed = NativeItemSerialization.FlattenAttached(factory, new[] { root }).Single();
    var decoded = placed.location.JToken.ToObject<LocationInGrid>();
    Check(placed.parentId?.ToString() == stash.Id && placed.slotId == "hideout" && decoded.x == 3 && decoded.y == 7 && decoded.r == ItemRotation.Vertical,
        "production synchronization adapter preserves native stash position and rotation");
}
static T Bare<T>() => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
static void Field(object value, string name, object data) => AccessTools.Field(value.GetType(), name).SetValue(value, data);
static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS: " + message); }
public static class StateFixture
{
    public static bool Prefix(ref UnparsedData __result) { __result = null; return false; }
}
public static class AvailabilityFixture
{
    public static bool Examined(ref bool __result) { __result = true; return false; }
    public static bool Context(ref AvailabilityContext __result) { __result = new() { ProfileReady = true, Authorized = true, OwnTarget = true }; return false; }
    public static bool Menu(ref bool __result) { __result = false; return false; }
    public static bool Identity(ref string __result) { __result = "pmc"; return false; }
}
