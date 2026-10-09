using System;
using System.Collections.Generic;
using System.Linq;

namespace DebugPlus.Common;

// Keep uncertain operations in the ledger: retrying a spawn must never create a second item.
public sealed class RequestLedger
{
    private readonly Dictionary<string, CommandResult> _results = new();
    private readonly Queue<string> _order = new();
    public string SessionId { get; private set; } = "";
    public void Begin(string session) { SessionId = session; _results.Clear(); _order.Clear(); }
    public bool TryBegin(CommandRequest request, out CommandResult result)
    {
        if (request.SessionId != SessionId) { result = CommandResult.Reject(request, "This request belongs to an expired session."); return false; }
        var key = request.ActorId + ":" + request.RequestId;
        if (_results.TryGetValue(key, out result!)) return false;
        // Do not evict IDs inside a session: capacity exhaustion rejects new commands.
        if (_results.Count >= 10000) { result = CommandResult.Reject(request, "Session command limit reached; start a new session."); return false; }
        result = new CommandResult { RequestId = request.RequestId, Status = CommandStatus.Pending, Message = "Queued" };
        _results.Add(key, result);
        return true;
    }
    public void Finish(CommandRequest request, CommandResult result) => _results[request.ActorId + ":" + request.RequestId] = result;
}

public static class ItemTrees
{
    public static readonly string[] ExcludedSlots = { "SecuredContainer", "Pockets", "SpecialSlot1", "SpecialSlot2", "SpecialSlot3", "Dogtag" };
    public static readonly string[] LoadoutSlots = { "FirstPrimaryWeapon", "SecondPrimaryWeapon", "Holster", "Scabbard", "Headwear", "Earpiece", "FaceCover", "Eyewear", "ArmorVest", "TacticalVest", "Backpack", "ArmBand" };
    public static HashSet<string> ReplacementSlots(CommandArguments args) => new(args.ClearEquipment
        ? LoadoutSlots : args.Items.Where(i => string.IsNullOrEmpty(i.ParentId)).Select(i => i.SlotId), StringComparer.Ordinal);

    public static List<DebugItemRecord> SelectEquipment(IReadOnlyList<DebugItemRecord> records, ISet<string>? includedRoots = null)
    {
        var ids = new HashSet<string>(records.Where(i => string.IsNullOrEmpty(i.ParentId)
            && !ExcludedSlots.Contains(i.SlotId) && (includedRoots == null || includedRoots.Contains(i.Id))).Select(i => i.Id));
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var item in records)
                if (!string.IsNullOrEmpty(item.ParentId) && ids.Contains(item.ParentId) && ids.Add(item.Id)) changed = true;
        }
        return records.Where(i => ids.Contains(i.Id)).ToList();
    }

    public static bool RepairRootSlots(IReadOnlyList<DebugItemRecord> records, Func<DebugItemRecord, IEnumerable<string>> candidates)
    {
        var occupied = new HashSet<string>(records.Where(r => string.IsNullOrEmpty(r.ParentId) && !string.IsNullOrEmpty(r.SlotId)).Select(r => r.SlotId), StringComparer.Ordinal);
        var changed = false;
        foreach (var root in records.Where(r => string.IsNullOrEmpty(r.ParentId) && string.IsNullOrEmpty(r.SlotId)))
        {
            var slots = candidates(root).Where(s => !string.IsNullOrWhiteSpace(s) && !occupied.Contains(s)).Distinct().ToArray();
            // Primary weapon slots have the same filter. Preserve kit root order
            // when recovering an old capture with no remaining original item.
            var slot = slots.Length == 1 ? slots[0]
                : slots.Length == 2 && slots.Contains("FirstPrimaryWeapon") && slots.Contains("SecondPrimaryWeapon") ? "FirstPrimaryWeapon" : null;
            if (slot == null) continue;
            root.SlotId = slot;
            occupied.Add(slot);
            changed = true;
        }
        return changed;
    }
    public static string Validate(IReadOnlyList<DebugItemRecord> items)
    {
        if (items.Count == 0 || items.Count > Protocol.MaxItems) return "Kit must contain 1–2048 items.";
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
            if (item == null || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.TemplateId) || !ids.Add(item.Id)) return "Kit has a missing template or duplicate item ID.";
        var byId = items.ToDictionary(i => i.Id);
        var roots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (string.IsNullOrEmpty(item.ParentId))
            {
                if (!string.IsNullOrEmpty(item.SlotId) && !roots.Add(item.SlotId)) return "Kit has conflicting root equipment slots.";
                continue;
            }
            var visited = new HashSet<string> { item.Id };
            var parent = item.ParentId;
            while (!string.IsNullOrEmpty(parent))
            {
                if (!byId.TryGetValue(parent, out var next)) return "Kit contains a missing parent.";
                if (!visited.Add(parent)) return "Kit contains an item cycle.";
                parent = next.ParentId;
            }
        }
        return "";
    }
    public static List<DebugItemRecord> Remap(IReadOnlyList<DebugItemRecord> items, Func<string> newId)
    {
        var error = Validate(items);
        if (error.Length > 0) throw new ArgumentException(error);
        var map = items.ToDictionary(i => i.Id, _ => newId());
        if (map.Values.Distinct().Count() != map.Count) throw new ArgumentException("ID factory produced duplicates.");
        return items.Select(i => new DebugItemRecord { Id = map[i.Id], TemplateId = i.TemplateId, ParentId = string.IsNullOrEmpty(i.ParentId) ? "" : map[i.ParentId], SlotId = i.SlotId, StateJson = i.StateJson, LocationJson = i.LocationJson }).ToList();
    }
}
