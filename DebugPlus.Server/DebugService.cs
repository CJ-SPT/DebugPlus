using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using DebugPlus.Common;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Inventory;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils;
using SPTarkov.Server.Core.Utils.Cloners;
using Path = System.IO.Path;

namespace DebugPlus.Server;

[Injectable(InjectionType.Singleton)]
public sealed class DebugService(SaveServer saves, JsonUtil json, ICloner cloner, InventoryHelper inventory, InventoryConstruction construction, ServerPolicy policy)
{
    private readonly ConcurrentDictionary<string, RequestLedger> _sessions = new(StringComparer.Ordinal);
    private RequestLedger Session(MongoId id) => _sessions.GetOrAdd(id.ToString(), _ =>
    {
        var ledger = new RequestLedger();
        ledger.Begin(Guid.NewGuid().ToString("N"));
        return ledger;
    });

    public AccessPolicy Access(MongoId id)
    {
        try
        {
            // A real server account may be a headless host with no playable PMC. It still needs
            // the server policy to authorize raid commands, but receives no mutation authority.
            var profile = saves.GetProfile(id);
            var pmcId = profile.CharacterData?.PmcData?.Id?.ToString() ?? "";
            var admins = policy.Read();
            var authorized = pmcId.Length > 0 && admins.Contains(pmcId, StringComparer.Ordinal);
            return new AccessPolicy
            {
                ProfileId = pmcId,
                SessionId = Session(id).SessionId,
                AdminProfileIds = admins,
                Authorized = authorized && ProfileGate.Ready,
                Message = pmcId.Length == 0 ? "Authenticated server session; read-only policy access without a playable PMC."
                    : authorized ? "Authorized by server policy." : "This PMC profile is absent from DebugPlus.Server/config.json AdminProfileIds."
            };
        }
        catch (Exception error) { return new AccessPolicy { Authorized = false, Message = error.Message }; }
    }

    public async Task<InventoryPreview> Preview(MongoId id, CommandRequest request)
    {
        var gate = ProfileGate.For(id);
        await gate.Semaphore.WaitAsync();
        try
        {
            var pmc = Authorize(id, request);
            var trees = construction.Build(request);
            construction.ValidateEquipment(pmc, trees);
            var slots = ItemTrees.ReplacementSlots(request.Args);
            var displaced = pmc.Inventory!.Items!.Where(x => x.ParentId == pmc.Inventory.Equipment.ToString() && slots.Contains(x.SlotId!))
                .SelectMany(x => InventoryConstruction.Tree(x, pmc.Inventory.Items!)).ToList();
            var message = new StringBuilder($"Target: {pmc.Info?.Nickname ?? pmc.Id.ToString()}\nIncoming equipment ({trees.Sum(x => x.Count)} items):\n");
            foreach (var tree in trees)
                foreach (var item in tree) message.AppendLine($"  {item.SlotId}: {item.Template} ×{item.Upd?.StackObjectsCount ?? 1}");
            message.AppendLine($"Replace and delete removes {displaced.Count} existing items, including nested contents:");
            foreach (var item in displaced) message.AppendLine($"  {item.SlotId}: {item.Template} ×{item.Upd?.StackObjectsCount ?? 1} [{item.Id}]");
            return new InventoryPreview { Hash = PreviewHash(pmc, request), Summary = message.ToString() };
        }
        catch (Exception error) { return new InventoryPreview { Summary = error.Message }; }
        finally { gate.Semaphore.Release(); }
    }

    public async Task<CommandResult> Execute(MongoId id, CommandRequest request)
    {
        var gate = ProfileGate.For(id);
        await gate.Semaphore.WaitAsync();
        RequestLedger? ledger = null;
        var started = false;
        try
        {
            var pmc = Authorize(id, request);
            var validation = Commands.Validate(request);
            if (validation.Length > 0) return CommandResult.Reject(request, validation);
            ledger = Session(id);
            if (!ledger.TryBegin(request, out var existing)) return existing;
            started = true;
            var result = await ExecuteStaged(id, pmc, request);
            ledger.Finish(request, result);
            return result;
        }
        catch (Exception error)
        {
            var result = CommandResult.Reject(request, error.Message);
            if (started) ledger!.Finish(request, result);
            return result;
        }
        finally { gate.Semaphore.Release(); }
    }

    private PmcData Authorize(MongoId id, CommandRequest request)
    {
        if (!ProfileGate.Ready) throw new InvalidOperationException("DebugPlus server lifecycle guards are unavailable.");
        var pmc = saves.GetProfile(id).CharacterData?.PmcData ?? throw new InvalidOperationException("PMC profile is unavailable.");
        if (!policy.Read().Contains(pmc.Id.ToString(), StringComparer.Ordinal)) throw new InvalidOperationException("This profile is not a DebugPlus administrator.");
        if (request.Protocol != Protocol.Version) throw new InvalidOperationException("DebugPlus protocol versions do not match.");
        if (request.SessionId != Session(id).SessionId) throw new InvalidOperationException("Menu session expired. Refresh DebugPlus access.");
        request.ActorId = pmc.Id.ToString() ?? throw new InvalidOperationException("PMC ID is unavailable.");
        if (request.TargetId != request.ActorId) throw new InvalidOperationException("Menu commands can only affect your authenticated PMC profile.");
        if (!Commands.IsInventory(request.Kind)) throw new InvalidOperationException("This command requires an active raid.");
        if (ProfileGate.For(id).InRaid) throw new InvalidOperationException("Menu inventory operations are blocked while this profile is in a raid or transit.");
        if (request.Args == null || Encoding.UTF8.GetByteCount(json.Serialize(request)!) > Protocol.MaxPayloadBytes) throw new InvalidOperationException("Invalid or oversized request.");
        if (request.Args.Destination is not ("Inventory" or "Stash")) throw new InvalidOperationException("Menu spawning only supports your stash.");
        if (request.Args.Count < 1 || request.Args.Count > Protocol.MaxCount) throw new InvalidOperationException("Invalid item count.");
        if (pmc.Inventory?.Items == null) throw new InvalidOperationException("PMC inventory is unavailable.");
        ReconcileJournal(id, pmc);
        return pmc;
    }

    public async Task<CommandResult> Synchronize(MongoId id, MenuInventorySync request)
    {
        var gate = ProfileGate.For(id);
        await gate.Semaphore.WaitAsync();
        RequestLedger? ledger = null;
        var started = false;
        try
        {
            var pmc = Authorize(id, request);
            var validation = Commands.Validate(request);
            if (validation.Length > 0) return CommandResult.Reject(request, validation);
            ledger = Session(id);
            if (!ledger.TryBegin(request, out var existing)) return existing;
            started = true;
            var clearEquipment = request.Kind == CommandKind.EquipKit && request.Args.ClearEquipment && request.Args.Items.Count == 0;
            if (request.Added == null || request.RemovedIds == null || (request.Added.Count == 0 && !clearEquipment) || (clearEquipment && request.Added.Count != 0) || request.Added.Count > Protocol.MaxItems || request.RemovedIds.Count > Protocol.MaxItems
                || Encoding.UTF8.GetByteCount(json.Serialize(request)!) > Protocol.MaxPayloadBytes)
                throw new InvalidOperationException("Invalid client inventory batch.");
            var beforeHash = Hash(pmc);
            var staged = cloner.Clone(pmc)!;
            staged.InsuredItems ??= [];
            var existingIds = staged.Inventory!.Items!.Select(i => i.Id.ToString()).ToHashSet(StringComparer.Ordinal);
            if (request.Added.Any(i => i == null || existingIds.Contains(i.Id)))
                throw new InvalidOperationException("A client item ID already exists in the profile.");
            var addedIds = request.Added.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
            var roots = request.Added.Where(i => !addedIds.Contains(i.ParentId)).ToList();
            var replacement = request.Kind == CommandKind.EquipKit;
            if ((roots.Count == 0 && !clearEquipment) || roots.Any(i => i.ParentId != (replacement ? staged.Inventory.Equipment : staged.Inventory.Stash).ToString()
                || (!replacement && i.SlotId != "hideout")))
                throw new InvalidOperationException("Invalid client inventory destination.");
            var records = request.Added.Select(i => new DebugItemRecord { Id = i.Id, TemplateId = i.TemplateId,
                ParentId = addedIds.Contains(i.ParentId) ? i.ParentId : "", SlotId = addedIds.Contains(i.ParentId) || replacement ? i.SlotId : "",
                LocationJson = addedIds.Contains(i.ParentId) ? i.LocationJson : "", StateJson = i.StateJson }).ToList();
            // Reuse native template, tree, condition, stack and equipment validation,
            // but retain the IDs already created by the client.
            var trees = construction.Build(new CommandRequest { Kind = clearEquipment ? CommandKind.EquipKit : CommandKind.SpawnKit,
                Args = new CommandArguments { Items = records, FoundInRaid = request.Args.FoundInRaid, ClearEquipment = clearEquipment } }, preserveIds: true);
            if (request.Kind == CommandKind.SpawnItem && (trees.Any(t => t[0].Template.ToString() != request.Args.TemplateId)
                || trees.Sum(t => t[0].Upd?.StackObjectsCount ?? 1) != request.Args.Count))
                throw new InvalidOperationException("Client spawn does not match the requested quantity or template.");
            var output = new ItemEventRouterResponse { Warnings = [], ProfileChanges = new() { [id] = new ProfileChange {
                Id = id, Items = new ItemChanges { NewItems = [], ChangedItems = [], DeletedItems = [] } } } };
            if (replacement)
            {
                construction.ValidateEquipment(staged, trees);
                var slots = clearEquipment ? ItemTrees.ReplacementSlots(request.Args) : roots.Select(i => i.SlotId).ToHashSet(StringComparer.Ordinal);
                var old = staged.Inventory.Items!.Where(i => i.ParentId == staged.Inventory.Equipment.ToString() && slots.Contains(i.SlotId!)).ToList();
                var removed = old.SelectMany(i => InventoryConstruction.Tree(i, staged.Inventory.Items!)).Select(i => i.Id.ToString()).ToHashSet(StringComparer.Ordinal);
                if (!removed.SetEquals(request.RemovedIds)) throw new InvalidOperationException("Equipment changed before the client replacement was saved.");
                foreach (var item in old) inventory.RemoveItem(staged, item.Id, id, output);
                if (staged.Inventory.FastPanel != null)
                    foreach (var key in staged.Inventory.FastPanel.Where(p => removed.Contains(p.Value.ToString())).Select(p => p.Key).ToList()) staged.Inventory.FastPanel.Remove(key);
                foreach (var tree in trees) { tree[0].ParentId = staged.Inventory.Equipment.ToString(); tree[0].Location = null; staged.Inventory.Items!.AddRange(tree); }
            }
            else
            {
                if (request.RemovedIds.Count != 0) throw new InvalidOperationException("Stash spawning cannot remove existing items.");
                construction.PlaceClientStashItems(staged, trees, roots);
            }
            if (output.Warnings?.Count > 0 || staged.Inventory.Items!.Count(i => addedIds.Contains(i.Id.ToString())) != addedIds.Count)
                throw new InvalidOperationException("Native inventory services rejected the client batch.");
            var result = await CommitInventory(id, pmc, staged, request, beforeHash, CommandResult.Complete(request, "Client inventory saved."));
            ledger.Finish(request, result);
            return result;
        }
        catch (Exception error)
        {
            var result = CommandResult.Reject(request, error.Message);
            if (started) ledger!.Finish(request, result);
            return result;
        }
        finally { gate.Semaphore.Release(); }
    }

    private async Task<CommandResult> ExecuteStaged(MongoId id, PmcData pmc, CommandRequest request)
    {
        var beforeHash = Hash(pmc);
        if (request.Kind == CommandKind.EquipKit && (!request.Args.ConfirmReplacement || request.Args.ExpectedInventoryHash != PreviewHash(pmc, request)))
            return CommandResult.Reject(request, "Inventory changed or replacement was not confirmed. Review a fresh replacement preview.");
        var trees = construction.Build(request);
        var staged = cloner.Clone(pmc) ?? throw new InvalidOperationException("Unable to stage the inventory.");
        staged.InsuredItems ??= [];
        var output = new ItemEventRouterResponse
        {
            Warnings = [],
            ProfileChanges = new() { [id] = new ProfileChange { Id = id, Items = new ItemChanges { NewItems = [], ChangedItems = [], DeletedItems = [] } } }
        };
        if (request.Kind == CommandKind.EquipKit)
        {
            construction.ValidateEquipment(staged, trees);
            var slots = ItemTrees.ReplacementSlots(request.Args);
            var old = staged.Inventory!.Items!.Where(x => x.ParentId == staged.Inventory.Equipment.ToString() && slots.Contains(x.SlotId!)).ToList();
            var deletedIds = old.SelectMany(x => InventoryConstruction.Tree(x, staged.Inventory.Items!)).Select(x => x.Id.ToString()).ToHashSet(StringComparer.Ordinal);
            foreach (var root in old) inventory.RemoveItem(staged, root.Id, id, output);
            if (staged.Inventory.FastPanel != null)
                foreach (var key in staged.Inventory.FastPanel.Where(x => deletedIds.Contains(x.Value.ToString())).Select(x => x.Key).ToList()) staged.Inventory.FastPanel.Remove(key);
            foreach (var tree in trees)
            {
                tree[0].ParentId = staged.Inventory.Equipment.ToString();
                tree[0].Location = null;
                staged.Inventory.Items!.AddRange(tree);
            }
        }
        else
        {
            // Preflight the entire set against one shared occupancy map, then use native placement on the clone.
            if (!inventory.CanPlaceItemsInInventory(id, trees)) return CommandResult.Reject(request, "The stash does not have enough free space for all requested items.");
            foreach (var tree in trees)
                inventory.AddItemToStash(id, new AddItemDirectRequest { ItemWithModsToAdd = tree, FoundInRaid = request.Args.FoundInRaid, UseSortingTable = false }, staged, output);
        }
        if (output.Warnings?.Count > 0) return CommandResult.Reject(request, "Native inventory services rejected the operation; the profile was not changed.");
        var createdIds = trees.SelectMany(x => x).Select(x => x.Id).ToHashSet();
        if (staged.Inventory!.Items!.Count(x => createdIds.Contains(x.Id)) != createdIds.Count || staged.Inventory.Items!.Select(x => x.Id).Distinct().Count() != staged.Inventory.Items!.Count)
            throw new InvalidOperationException("Native item construction failed validation; the profile was not changed.");
        var completed = CommandResult.Complete(request, request.Kind == CommandKind.EquipKit ? "Kit equipped and displaced items deleted." : "Items added to your stash.",
            json.Serialize(new { InventoryHash = Hash(staged), RefreshBackend = request.Kind == CommandKind.EquipKit,
                StashDeltaVersion = 1, ItemChanges = output.ProfileChanges[id].Items })!);
        return await CommitInventory(id, pmc, staged, request, beforeHash, completed);
    }

    private async Task<CommandResult> CommitInventory(MongoId id, PmcData pmc, PmcData staged, CommandRequest request, string beforeHash, CommandResult completed)
    {
        if (Hash(pmc) != beforeHash) return CommandResult.Reject(request, "Inventory changed while the operation was being prepared. Retry with a fresh preview.");
        var beforeInventory = pmc.Inventory;
        var beforeInsurance = pmc.InsuredItems;
        var journal = new RecoveryJournal { RequestId = request.RequestId, SessionId = request.SessionId, BeforeHash = beforeHash, AfterHash = Hash(staged), BeforeInventory = json.Serialize(beforeInventory)!, BeforeInsurance = json.Serialize(beforeInsurance)! };
        var path = JournalPath(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Durable recovery metadata precedes publication. A crash never causes an automatic replay.
        WriteJournal(path, journal);
        pmc.Inventory = staged.Inventory;
        pmc.InsuredItems = staged.InsuredItems;
        try
        {
            await saves.SaveProfileAsync(id);
            TryDeleteJournal(path);
            return completed;
        }
        catch (Exception saveError)
        {
            pmc.Inventory = beforeInventory;
            pmc.InsuredItems = beforeInsurance;
            try
            {
                InvalidateSaveHash(id);
                await saves.SaveProfileAsync(id);
                TryDeleteJournal(path);
                return CommandResult.Reject(request, "Saving failed; the original inventory was restored. " + saveError.Message);
            }
            catch
            {
                ProfileGate.For(id).NeedsRecovery = true;
                return new CommandResult { RequestId = request.RequestId, Status = CommandStatus.Uncertain, Message = "Profile saving and recovery failed. Original in-memory inventory was restored; further inventory operations are blocked. Preserve the recovery journal and restart after resolving the storage error. " + saveError.Message };
            }
        }
    }

    private string Hash(PmcData pmc) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json.Serialize(new { Inventory = pmc.Inventory!.Items!.OrderBy(x => x.Id.ToString(), StringComparer.Ordinal), Insurance = pmc.InsuredItems, QuickSlots = pmc.Inventory.FastPanel })!)));
    private string PreviewHash(PmcData pmc, CommandRequest request) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        Hash(pmc) + json.Serialize(new { request.Kind, request.TargetId, request.Args.Items, request.Args.FoundInRaid, request.Args.Count, request.Args.ClearEquipment }))));
    private string JournalPath(MongoId id) => Path.Combine(policy.DirectoryPath, "recovery", id + ".json");
    private void ReconcileJournal(MongoId id, PmcData pmc)
    {
        if (ProfileGate.For(id).NeedsRecovery) throw new InvalidOperationException("A failed profile save requires recovery before more inventory operations.");
        var path = JournalPath(id);
        if (!File.Exists(path)) return;
        var journal = json.Deserialize<RecoveryJournal>(File.ReadAllText(path)) ?? throw new InvalidOperationException("Recovery journal is invalid.");
        var current = Hash(pmc);
        if (current != journal.BeforeHash && current != journal.AfterHash) throw new InvalidOperationException("Unresolved inventory recovery journal. Preserve recovery files and reconcile the saved profile before using menu operations.");
        // The profile is already in a fully committed or fully rolled-back state. Do not apply anything.
        TryDeleteJournal(path);
    }
    private void InvalidateSaveHash(MongoId id)
    {
        // SPT 4.1.5 updates saveMd5 before the write; clear it after an I/O failure so rollback is really persisted.
        var field = typeof(SaveServer).GetField("saveMd5", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        if (field?.GetValue(saves) is ConcurrentDictionary<MongoId, string> cache) cache.TryRemove(id, out _);
        else throw new InvalidOperationException("Native save recovery is incompatible with this server version.");
    }
    private void WriteJournal(string path, RecoveryJournal journal)
    {
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            var bytes = Encoding.UTF8.GetBytes(json.Serialize(journal)!);
            stream.Write(bytes);
            stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }
    private static void TryDeleteJournal(string path) { try { File.Delete(path); } catch { /* Committed profile remains authoritative; reconcile on the next request. */ } }
}

public sealed class RecoveryJournal
{
    public string RequestId { get; set; } = "";
    public string SessionId { get; set; } = "";
    public string BeforeHash { get; set; } = "";
    public string AfterHash { get; set; } = "";
    public string BeforeInventory { get; set; } = "";
    public string BeforeInsurance { get; set; } = "";
}
