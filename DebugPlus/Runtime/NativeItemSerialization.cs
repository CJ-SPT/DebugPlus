using System.Collections.Generic;
using System;
using System.Linq;
using EFT;
using EFT.InventoryLogic;
using JsonType;
using Newtonsoft.Json;

namespace DebugPlus.Runtime;

internal static class NativeItemSerialization
{
    internal static FlatItem[] FlattenAttached(ItemFactory factory, IEnumerable<Item> roots)
    {
        var rootArray = roots.ToArray();
        var byId = rootArray.ToDictionary(i => i.Id.ToString());
        var flat = factory.TreeToFlatItems(rootArray);
        // EFT drops input-root addresses. Descendant addresses are already serialized.
        foreach (var record in flat)
        {
            if (!byId.TryGetValue(record._id, out var root) || root.CurrentAddress?.Container is not { } container) continue;
            record.parentId = container.ParentItem?.Id;
            record.slotId = container.ID;
            // Same serializer used by ItemDeserializer.SaveItemLocation.
            record.location = container is Grid grid ? grid.GetItemLocation(root).ToUnparsedData(Array.Empty<JsonConverter>())
                : container is StackSlot stack ? stack.GetItemPosition(root).ToUnparsedData(Array.Empty<JsonConverter>()) : null;
        }
        return flat;
    }
}
