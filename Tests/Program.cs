using DebugPlus.Common;

LoggingChecks.Run();

var checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
var request = new CommandRequest { SessionId = "raid-a", ActorId = "admin", TargetId = "player", Kind = CommandKind.SpawnItem, Args = new() { TemplateId = "item" } };
Check(Commands.Validate(request) == "", "valid request");
var ledger = new RequestLedger(); ledger.Begin("raid-a");
Check(ledger.TryBegin(request, out _), "first execution");
Check(!ledger.TryBegin(request, out var pending) && pending.Status == CommandStatus.Pending, "in-flight duplicate");
var uncertain = new CommandResult { RequestId = request.RequestId, Status = CommandStatus.Uncertain, Message = "lost acknowledgement" };
ledger.Finish(request, uncertain);
Check(!ledger.TryBegin(request, out var repeat) && ReferenceEquals(uncertain, repeat), "uncertain requests never replay");
ledger.Begin("raid-b");
Check(!ledger.TryBegin(request, out var expired) && expired.Status == CommandStatus.Rejected, "transit expires requests");
request.SessionId = "raid-b"; request.Args.X = float.NaN;
Check(Commands.Validate(request).Length > 0, "NaN rejected"); request.Args.X = 0;
request.Args.Count = int.MaxValue; Check(Commands.Validate(request).Length > 0, "unbounded item count rejected"); request.Args.Count = 1;
request.Kind = CommandKind.EquipKit; Check(Commands.Validate(request).Length > 0, "replacement needs explicit confirmation");
var clearRequest = new CommandRequest { SessionId = "menu", ActorId = "admin", TargetId = "pmc", Kind = CommandKind.EquipKit,
    Args = new() { ClearEquipment = true, ConfirmReplacement = true } };
Check(Commands.Validate(clearRequest) == "" && !ItemTrees.ReplacementSlots(clearRequest.Args).Overlaps(ItemTrees.ExcludedSlots), "explicit empty loadout clears only ordinary equipment slots");
clearRequest.Kind = CommandKind.SpawnKit;
Check(Commands.Validate(clearRequest).Length > 0, "spawning cannot clear equipment");
clearRequest.Kind = CommandKind.EquipKit; clearRequest.Args.Items.Add(new() { Id = "item", TemplateId = "template" });
Check(Commands.Validate(clearRequest).Length > 0, "nonempty kit cannot request full equipment clearing");
var items = new List<DebugItemRecord> { new() { Id="root", TemplateId="rifle", SlotId="FirstPrimaryWeapon" }, new() { Id="mag", TemplateId="magazine", ParentId="root", SlotId="mod_magazine", StateJson="{\"StackObjectsCount\":1}" }, new() { Id="ammo",TemplateId="round",ParentId="mag",SlotId="cartridges",LocationJson="0",StateJson="{\"StackObjectsCount\":30}" } };
Check(ItemTrees.Validate(items) == "", "nested weapon tree");
var next = 0; var remapped = ItemTrees.Remap(items, () => "new" + next++);
Check(remapped[2].ParentId == remapped[1].Id && remapped[1].ParentId == remapped[0].Id, "parent remapping");
Check(remapped[2].StateJson == items[2].StateJson && remapped[2].LocationJson == "0", "ammunition state retained");
Check(items[0].Id == "root", "source kit not mutated");
items[0].ParentId="ammo"; Check(ItemTrees.Validate(items).Length > 0, "cycles rejected"); items[0].ParentId="";
items[2].ParentId="missing"; Check(ItemTrees.Validate(items).Length > 0, "dangling parent rejected"); items[2].ParentId="mag";
items.Add(new() { Id="root",TemplateId="other" }); Check(ItemTrees.Validate(items).Length > 0, "duplicate identity rejected"); items.RemoveAt(3);
items.Add(new() { Id="second",TemplateId="other",SlotId="FirstPrimaryWeapon" }); Check(ItemTrees.Validate(items).Length > 0, "root slot conflicts rejected"); items.RemoveAt(3);
try { ItemTrees.Remap(items, () => "same"); throw new Exception("duplicate generated IDs accepted"); } catch (ArgumentException) { checks++; }
Check(ItemTrees.ExcludedSlots.Contains("SecuredContainer") && ItemTrees.ExcludedSlots.Contains("Pockets"), "default protected slots");
var legacyKit = new List<DebugItemRecord> { new() { Id="rifle1", TemplateId="rifle" }, new() { Id="rifle2", TemplateId="rifle" },
    new() { Id="mag", TemplateId="magazine", ParentId="rifle1", SlotId="mod_magazine", StateJson="ammo", LocationJson="0" } };
Check(ItemTrees.RepairRootSlots(legacyKit, _ => new[] { "FirstPrimaryWeapon", "SecondPrimaryWeapon" })
    && legacyKit[0].SlotId == "FirstPrimaryWeapon" && legacyKit[1].SlotId == "SecondPrimaryWeapon", "legacy roots recover distinct compatible weapon slots");
Check(legacyKit[2].ParentId == "rifle1" && legacyKit[2].SlotId == "mod_magazine" && legacyKit[2].StateJson == "ammo" && legacyKit[2].LocationJson == "0"
    && !ItemTrees.RepairRootSlots(legacyKit, _ => throw new Exception("Already repaired roots should not be re-resolved")), "repair preserves children and is idempotent");
Check(ItemTrees.ExcludedSlots.Contains("Dogtag"), "personal dogtag is excluded from equipment capture and replacement");
var bookmarks = new List<TeleportBookmark>();
BookmarkStore.Save(bookmarks, new() { Name = "Entrance", MapId = "sandbox", X = 1 });
BookmarkStore.Save(bookmarks, new() { Name = "Entrance", MapId = "sandbox_high", X = 2 });
BookmarkStore.Save(bookmarks, new() { Name = "Entrance", MapId = "sandbox", X = 3 });
Check(bookmarks.Count == 2 && BookmarkStore.ForMap(bookmarks, "sandbox_high").Single().X == 2, "same-name bookmarks do not replace other maps");
Check(BookmarkStore.ForMap(bookmarks, "sandbox").Single().X == 3 && !BookmarkStore.ForMap(bookmarks, "").Any(), "bookmarks require an exact active map");
try { BookmarkStore.Save(bookmarks, new() { Name = "Invalid", MapId = "sandbox", X = float.PositiveInfinity }); throw new Exception("nonfinite bookmark accepted"); } catch (ArgumentException) { checks++; }
Check(bookmarks.Count == 2, "invalid bookmark does not mutate saved entries");
Console.WriteLine($"{checks} command/session/item-tree/bookmark checks passed.");

var availabilityChecks = 0;
void Available(bool value, string name) { if (!value) throw new Exception(name); availabilityChecks++; }
var availability = new AvailabilityContext { ProfileReady = true, Authorized = true, OwnTarget = true };
foreach (var page in new[] { "Player", "Health", "Teleport", "Bots", "World" })
    Available(WorkbenchAvailability.Page(page, availability).Contains("raid"), page + " requires a raid");
foreach (var page in new[] { "Overview", "Items", "Kits", "Diagnostics", "Settings" })
    Available(WorkbenchAvailability.Page(page, availability) == "", page + " works at the menu");
Available(WorkbenchAvailability.Inventory(availability) == "", "own menu inventory available");
availability.OwnTarget = false;
Available(WorkbenchAvailability.Inventory(availability).Length > 0, "remote menu target unavailable");
availability.OwnTarget = true; availability.InWorld = true;
Available(WorkbenchAvailability.Inventory(availability).Length > 0 && WorkbenchAvailability.Menu(availability).Length > 0, "hideout/loading world blocks menu changes");
availability.InRaid = true; availability.TargetAlive = true;
Available(WorkbenchAvailability.Page("Health", availability) == "", "health becomes available when raid begins");
availability.Authorized = false;
Available(WorkbenchAvailability.Page("World", availability).Contains("Administrator") && WorkbenchAvailability.Inventory(availability).Length > 0, "permission loss disables mutations");
Available(WorkbenchAvailability.Page("Items", availability) == "" && WorkbenchAvailability.Page("Settings", availability) == "", "browsing and settings remain available without authority");
availability.Authorized = true; availability.IsCoop = true;
Available(WorkbenchAvailability.Page("Bots", availability).Contains("synchronization"), "companion loss disables raid tools");
availability.CoopReady = true;
Available(WorkbenchAvailability.Page("Teleport", availability) == "", "restored synchronization re-enables tools");
availability.TargetAlive = false;
Available(WorkbenchAvailability.Page("Player", availability).Contains("live player") && WorkbenchAvailability.Inventory(availability).Length > 0, "death/disconnect disables target actions");
Available(WorkbenchAvailability.Page("Bots", availability) == "" && WorkbenchAvailability.Page("World", availability) == "", "world tools do not require a playable target");
availability.InRaid = availability.InWorld = availability.ProfileReady = false;
Available(WorkbenchAvailability.Page("Items", availability).Length > 0 && WorkbenchAvailability.Inventory(availability).Length > 0, "profile unloading disables item browsing and inventory changes");
Console.WriteLine($"{availabilityChecks} availability and transition checks passed.");
