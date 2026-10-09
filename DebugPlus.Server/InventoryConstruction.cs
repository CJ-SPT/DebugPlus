using System.Globalization;
using System.Text.Json.Nodes;
using DebugPlus.Common;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers.Items;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Utils;

namespace DebugPlus.Server;

[Injectable(InjectionType.Singleton)]
public sealed class InventoryConstruction(ItemHelper items, InventoryHelper inventory, JsonUtil json)
{
    private static readonly System.Reflection.MethodInfo StashMap = typeof(InventoryHelper).GetMethod("GetStashSlotMap",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Native stash occupancy is unavailable.");

    public void PlaceClientStashItems(PmcData pmc, List<List<Item>> trees, List<DebugItemRecord> roots)
    {
        var map = (int[,])StashMap.Invoke(inventory, [pmc])!;
        foreach (var tree in trees)
        {
            var root = tree[0];
            var record = roots.Single(r => r.Id == root.Id.ToString());
            var location = json.Deserialize<ItemLocation>(record.LocationJson) ?? throw new InvalidOperationException("Missing stash position.");
            var (width, height) = inventory.GetItemSize(root.Template, root.Id, tree);
            if ((int)location.R is < 0 or > 1) throw new InvalidOperationException("Invalid stash rotation.");
            if ((int)location.R == 1) (width, height) = (height, width);
            if (location.X is not int x || location.Y is not int y || x < 0 || y < 0 || width <= 0 || height <= 0
                || x > map.GetLength(1) - width || y > map.GetLength(0) - height)
                throw new InvalidOperationException("Client stash placement is outside the stash.");
            for (var row = y; row < y + height; row++) for (var col = x; col < x + width; col++)
            {
                if (map[row, col] != 0) throw new InvalidOperationException("Client stash placement overlaps another item.");
                map[row, col] = 1;
            }
            root.ParentId = pmc.Inventory!.Stash.ToString();
            root.SlotId = "hideout";
            root.Location = location;
            pmc.Inventory.Items!.AddRange(tree);
        }
    }

    public List<List<Item>> Build(CommandRequest request, bool preserveIds = false)
    {
        var a = request.Args;
        if (a.ClearEquipment && a.Items.Count == 0 && request.Kind is CommandKind.EquipKit or CommandKind.PreviewKit) return [];
        if (a.Value < 0 || a.Value > 100) throw new InvalidOperationException("Item condition must be between 0 and 100 percent.");
        var records = a.Items;
        if (request.Kind == CommandKind.SpawnItem)
        {
            var template = Template(a.TemplateId);
            var max = Math.Max(1, Number(Properties(template)["StackMaxSize"], 1));
            records = [];
            for (var remaining = a.Count; remaining > 0;)
            {
                var count = (int)Math.Min(remaining, max);
                var state = JsonNode.Parse(json.Serialize(items.GenerateUpdForItem(template))!)!.AsObject();
                state["StackObjectsCount"] = count;
                ApplyCondition(state, a.Value / 100d);
                records.Add(new DebugItemRecord { Id = new MongoId().ToString(), TemplateId = a.TemplateId, StateJson = state.ToJsonString() });
                remaining -= count;
            }
        }
        var error = ItemTrees.Validate(records);
        if (error.Length > 0) throw new InvalidOperationException(error);
        var mapped = preserveIds ? records : ItemTrees.Remap(records, () => new MongoId().ToString());
        if (mapped.Any(r => !MongoId.IsValidMongoId(r.Id))) throw new InvalidOperationException("Invalid item ID.");
        var result = new List<Item>(mapped.Count);
        foreach (var record in mapped)
        {
            var template = Template(record.TemplateId);
            var state = JsonNode.Parse(json.Serialize(items.GenerateUpdForItem(template))!)!.AsObject();
            if (!string.IsNullOrWhiteSpace(record.StateJson))
            {
                var supplied = JsonNode.Parse(record.StateJson) as JsonObject ?? throw new InvalidOperationException("Item state must be a JSON object.");
                CheckNumbers(supplied);
                foreach (var field in supplied) state[field.Key] = field.Value?.DeepClone();
            }
            state["SpawnedInSession"] = a.FoundInRaid;
            state.Remove("UnlimitedCount");
            state.Remove("BuyRestrictionCurrent");
            state.Remove("BuyRestrictionMax");
            var stack = Number(state["StackObjectsCount"], 1);
            if (stack < 1 || Math.Truncate(stack) != stack || stack > Math.Max(1, Number(Properties(template)["StackMaxSize"], 1)))
                throw new InvalidOperationException($"Invalid stack size for {record.TemplateId}.");
            state["StackObjectsCount"] = stack;
            ValidateCondition(state, Properties(template), record.TemplateId);
            var native = new JsonObject
            {
                ["_id"] = record.Id,
                ["_tpl"] = record.TemplateId,
                ["parentId"] = string.IsNullOrEmpty(record.ParentId) ? null : record.ParentId,
                ["slotId"] = string.IsNullOrEmpty(record.SlotId) ? null : record.SlotId,
                ["upd"] = state,
                ["location"] = string.IsNullOrWhiteSpace(record.LocationJson) ? null : JsonNode.Parse(record.LocationJson)
            };
            result.Add(json.Deserialize<Item>(native.ToJsonString()) ?? throw new InvalidOperationException("Unable to construct native item."));
        }
        var byId = result.ToDictionary(x => x.Id.ToString(), StringComparer.Ordinal);
        var roots = result.Where(x => string.IsNullOrEmpty(x.ParentId)).ToList();
        var trees = roots.Select(root => Tree(root, result)).ToList();
        foreach (var tree in trees)
        {
            foreach (var item in tree.Where(x => !string.IsNullOrEmpty(x.ParentId))) ValidateChild(item, byId[item.ParentId!], tree);
            foreach (var parent in tree) ValidateContents(parent, tree);
            ValidateConflicts(tree);
        }
        return trees;
    }

    public void ValidateEquipment(PmcData pmc, List<List<Item>> trees)
    {
        var equipment = pmc.Inventory!.Items!.Single(x => x.Id == pmc.Inventory.Equipment);
        var slots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tree in trees)
        {
            var root = tree[0];
            if (string.IsNullOrWhiteSpace(root.SlotId) || ItemTrees.ExcludedSlots.Contains(root.SlotId) || !slots.Add(root.SlotId))
                throw new InvalidOperationException("Kit equipment slots are missing, duplicated, or protected.");
            ValidateChild(root, equipment, tree);
        }
        var retained = pmc.Inventory.Items!.Where(x => x.ParentId == equipment.Id.ToString() && !slots.Contains(x.SlotId ?? "")).ToList();
        var candidateTrees = retained.Select(x => Tree(x, pmc.Inventory.Items!)).Concat(trees).ToList();
        ValidateConflicts(candidateTrees.Select(x => x[0]).ToList());
        var occupied = candidateTrees.Select(x => x[0].SlotId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var tree in candidateTrees)
            foreach (var item in tree)
                foreach (var flag in Properties(item).Where(x => x.Key.StartsWith("Blocks", StringComparison.Ordinal) && x.Value is JsonValue value && value.TryGetValue<bool>(out var enabled) && enabled))
                {
                    var blockedSlot = flag.Key[6..];
                    if (occupied.Contains(blockedSlot) && !string.Equals(tree[0].SlotId, blockedSlot, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"Equipment {item.Template} blocks the occupied {blockedSlot} slot.");
                }
    }

    public TemplateItem Template(string id)
    {
        if (!MongoId.IsValidMongoId(id)) throw new InvalidOperationException($"Invalid item template ID: {id}.");
        var found = items.GetItem(new MongoId(id));
        // Commerce validity excludes zero-price attachments and modded items. Debug inventory uses
        // native possession/type restrictions instead, so captured armor inserts remain valid.
        if (!found.Key || found.Value == null || !string.Equals(found.Value.Type, "Item", StringComparison.OrdinalIgnoreCase)
            || found.Value.Properties?.QuestItem == true
            || items.IsOfBaseclasses(new MongoId(id), [BaseClasses.LOOT_CONTAINER, BaseClasses.MOB_CONTAINER, BaseClasses.STASH, BaseClasses.SORTING_TABLE, BaseClasses.INVENTORY, BaseClasses.STATIONARY_CONTAINER, BaseClasses.POCKETS]))
            throw new InvalidOperationException($"Item template {id} is missing or cannot be spawned.");
        return found.Value;
    }

    private JsonObject Properties(TemplateItem template) =>
        JsonNode.Parse(json.Serialize(template)!)!["_props"]?.AsObject() ?? throw new InvalidOperationException("Item has no native properties.");
    private JsonObject Properties(Item item)
    {
        var found = items.GetItem(item.Template);
        if (!found.Key || found.Value == null) throw new InvalidOperationException("Inventory parent template is unavailable.");
        return Properties(found.Value);
    }
    public static List<Item> Tree(Item root, IReadOnlyList<Item> all)
    {
        var children = all.ToLookup(x => x.ParentId ?? "", StringComparer.Ordinal);
        var result = new List<Item>();
        var pending = new Stack<Item>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var next = pending.Pop();
            result.Add(next);
            foreach (var child in children[next.Id.ToString()]) pending.Push(child);
            if (result.Count > Protocol.MaxItems * 100) throw new InvalidOperationException("Invalid inventory item cycle.");
        }
        return result;
    }
    private void ValidateChild(Item child, Item parent, IReadOnlyList<Item> all)
    {
        var props = Properties(parent);
        var slots = Array(props, "Slots").Concat(Array(props, "Chambers")).Concat(Array(props, "Cartridges")).Concat(Array(props, "StackSlots")).Concat(Array(props, "Grids"));
        var slot = slots.FirstOrDefault(x => x["_name"]?.GetValue<string>() == child.SlotId)
            ?? throw new InvalidOperationException($"{parent.Template} has no slot {child.SlotId}.");
        var filters = slot["_props"]?["filters"] ?? slot["_props"]?["Filters"];
        if (filters is JsonArray rules && rules.Count > 0 && !rules.Any(rule => Allowed(child, rule)))
            throw new InvalidOperationException($"{child.Template} is incompatible with {child.SlotId} on {parent.Template}.");
        var maxStack = Number(slot["_max_count"] ?? slot["_props"]?["MaxStackCount"], double.MaxValue);
        if ((child.Upd?.StackObjectsCount ?? 1) > maxStack) throw new InvalidOperationException("Item exceeds native slot capacity.");
    }
    private bool Allowed(Item child, JsonNode? rule)
    {
        bool Matches(JsonNode? values) => values is JsonArray array && array.Any(x =>
        {
            var id = x?.GetValue<string>();
            return id != null && MongoId.IsValidMongoId(id) && (child.Template.ToString() == id || items.IsOfBaseclass(child.Template, new MongoId(id)));
        });
        var filter = rule?["Filter"];
        return (filter is not JsonArray { Count: > 0 } || Matches(filter)) && !Matches(rule?["ExcludedFilter"]);
    }
    private void ValidateContents(Item parent, IReadOnlyList<Item> all)
    {
        var props = Properties(parent);
        foreach (var children in all.Where(x => x.ParentId == parent.Id.ToString()).GroupBy(x => x.SlotId))
        {
            var grid = Array(props, "Grids").FirstOrDefault(x => x["_name"]?.GetValue<string>() == children.Key);
            var stackSlot = Array(props, "Cartridges").Concat(Array(props, "StackSlots")).FirstOrDefault(x => x["_name"]?.GetValue<string>() == children.Key);
            if (grid != null)
            {
                var w = (int)Number(grid["_props"]?["cellsH"]);
                var h = (int)Number(grid["_props"]?["cellsV"]);
                if (w <= 0 || h <= 0 || w > 1000 || h > 1000) throw new InvalidOperationException("Invalid native container dimensions.");
                var used = new HashSet<(int, int)>();
                foreach (var child in children)
                {
                    var location = JsonNode.Parse(json.Serialize(child.Location) ?? "null");
                    if (location is not JsonObject) throw new InvalidOperationException("Grid item is missing its location.");
                    var x = (int)Number(location["x"], -1);
                    var y = (int)Number(location["y"], -1);
                    if (Number(location["x"], -1) != x || Number(location["y"], -1) != y) throw new InvalidOperationException("Grid coordinates must be whole numbers.");
                    var (width, height) = inventory.GetItemSize(child.Template, child.Id, Tree(child, all));
                    var rotation = location["r"]?.ToString();
                    if (rotation is not (null or "0" or "1" or "Horizontal" or "Vertical")) throw new InvalidOperationException("Invalid item rotation.");
                    if (rotation is "1" or "Vertical") (width, height) = (height, width);
                    if (x < 0 || y < 0 || x + width > w || y + height > h) throw new InvalidOperationException("Kit item lies outside its container.");
                    for (var ix = x; ix < x + width; ix++) for (var iy = y; iy < y + height; iy++)
                        if (!used.Add((ix, iy))) throw new InvalidOperationException("Kit items overlap inside a container.");
                }
            }
            else if (stackSlot != null)
            {
                var total = children.Sum(x => x.Upd?.StackObjectsCount ?? 1);
                if (total > Number(stackSlot["_max_count"], 0)) throw new InvalidOperationException("Ammunition exceeds magazine capacity.");
                var positions = new HashSet<int>();
                foreach (var child in children)
                {
                    var raw = JsonNode.Parse(json.Serialize(child.Location) ?? "null");
                    // Native one-stack magazines can omit location; the sole stack is index zero.
                    var position = (int)Number(raw, children.Count() == 1 ? 0 : -1);
                    if (raw != null && Number(raw, -1) != position) throw new InvalidOperationException("Ammunition positions must be whole numbers.");
                    if (position < 0 || !positions.Add(position)) throw new InvalidOperationException("Invalid ammunition stack position.");
                }
            }
            else if (children.Count() > 1) throw new InvalidOperationException("Multiple items occupy a single native slot.");
        }
    }
    private void ValidateConflicts(IReadOnlyList<Item> all)
    {
        var present = all.Select(x => x.Template.ToString()).ToHashSet(StringComparer.Ordinal);
        foreach (var item in all)
            if (Properties(item)["ConflictingItems"] is JsonArray conflicts && conflicts.Any(x => present.Contains(x?.ToString() ?? "")))
                throw new InvalidOperationException($"Kit contains an item incompatible with {item.Template}.");
    }
    private static IEnumerable<JsonNode> Array(JsonObject props, string name) => (props[name] as JsonArray)?.OfType<JsonNode>() ?? [];
    private static double Number(JsonNode? value, double fallback = 0) => value != null && double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : fallback;
    private static void CheckNumbers(JsonNode? node)
    {
        if (node is JsonObject obj) { foreach (var pair in obj) CheckNumbers(pair.Value); }
        else if (node is JsonArray array) { foreach (var entry in array) CheckNumbers(entry); }
        else if (node is JsonValue value && value.TryGetValue<double>(out var n) && (!double.IsFinite(n) || Math.Abs(n) > 1_000_000_000))
            throw new InvalidOperationException("Item state has an invalid numeric value.");
    }
    private static void ApplyCondition(JsonObject state, double factor)
    {
        foreach (var (component, field) in new[] { ("Repairable", "Durability"), ("MedKit", "HpResource"), ("FoodDrink", "HpPercent"), ("Resource", "Value"), ("RepairKit", "Resource") })
            if (state[component] is JsonObject part && part[field] != null) part[field] = Number(part[field]) * factor;
    }
    private static void ValidateCondition(JsonObject state, JsonObject props, string template)
    {
        if (state["Repairable"] is JsonObject repair)
        {
            var durability = Number(repair["Durability"], -1);
            var maximum = Number(repair["MaxDurability"], -1);
            var nativeMax = Number(props["MaxDurability"], Number(props["Durability"], double.MaxValue));
            // Plate-carrier shells retain legacy repair metadata while their own maximum is zero.
            if (durability < 0 || maximum < 0 || durability > maximum || nativeMax > 0 && maximum > nativeMax)
                throw new InvalidOperationException($"Invalid durability for {template}.");
        }
        foreach (var (component, field, limit) in new[] { ("MedKit", "HpResource", "MaxHpResource"), ("FoodDrink", "HpPercent", "MaxResource"), ("Resource", "Value", "MaxResource"), ("RepairKit", "Resource", "MaxRepairResource") })
            if (state[component] is JsonObject part && part[field] != null && (Number(part[field], -1) < 0 || Number(part[field]) > Number(props[limit], double.MaxValue)))
                throw new InvalidOperationException($"Invalid resource state for {template}.");
    }
}
