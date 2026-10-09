using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Bootstrap;
using Comfort.Common;
using DebugPlus.Common;
using EFT;
using EFT.InventoryLogic;
using Newtonsoft.Json;
using SPT.Common.Http;
using UnityEngine;
using CommandStatus = DebugPlus.Common.CommandStatus;
using DebugPlus.Config;
using BepInEx.Configuration;

namespace DebugPlus.Runtime;

public sealed partial class WorkbenchRuntime : MonoBehaviour
{
    public static WorkbenchRuntime Instance { get; private set; }
    private GameWorld _world;
    private readonly RequestLedger _ledger = new();
    private readonly Dictionary<string, SemaphoreSlim> _locks = new();
    private readonly List<ActivityEntry> _activity = new();
    private readonly List<TargetInfo> _targets = new();
    private readonly List<ItemInfo> _catalog = new();
    private List<KitDocument> _kits = new();
    private List<TeleportBookmark> _bookmarks = new();
    private string _account = "";
    private float _nextAccess, _nextTargets;
    private bool _fetchingAccess;
    private int _generation;
    private bool _syncingLegacy;
    private bool _autoRaidRequested;
    private readonly Queue<Action> _mainThread = new();
    private readonly Dictionary<string, PlayerOverrides> _overrides = new();
    public AccessPolicy Policy { get; private set; } = new();
    public string SessionId { get; private set; } = "";
    public bool InRaid => _world && _world == World && IsRaidWorld(_world);
    public bool IsHeadless => Chainloader.PluginInfos.ContainsKey("com.fika.headless");
    public bool IsCoop => Chainloader.PluginInfos.ContainsKey("com.fika.core") && InRaid;
    public bool IsAuthorized => Policy.Authorized;
    public string MapId => InRaid ? _world.LocationId ?? "" : "";
    public string LocalProfileId => InRaid && _world.MainPlayer ? _world.MainPlayer.ProfileId : Session?.Profile?.Id ?? Policy.ProfileId ?? "";
    public string Status => IsCoop ? CoopStatus : Policy.Message;
    public bool CoopReady { get; set; }
    public string CoopStatus { get; set; } = "Waiting for the DebugPlus Fika companion.";
    public Func<CommandRequest, Task<CommandResult>> CoopSubmit { get; set; }
    public Func<CommandRequest, string, Task<bool>> ReplicateInventory { get; set; }
    public IReadOnlyList<TargetInfo> Targets => _targets;
    public IReadOnlyList<ItemInfo> Catalog => _catalog;
    public IReadOnlyList<ActivityEntry> Activity => _activity;
    public IReadOnlyList<KitDocument> Kits => _kits;
    public IReadOnlyList<TeleportBookmark> Bookmarks => _bookmarks;
    public WorkbenchPreferences Preferences { get; private set; } = new();
    public static GameWorld World => Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance : null;
    public static IEftSession Session => TarkovApplication.Exist(out var app) ? app.Session : null;
    public string DataDirectory => Path.Combine(BepInEx.Paths.ConfigPath, "DebugPlus");
    public static bool IsRaidWorld(GameWorld world) => world && world.GetType().Name != "HideoutGameWorld" && !string.Equals(world.LocationId, "hideout", StringComparison.OrdinalIgnoreCase);
    public Player FindPlayer(string id) => World?.AllAlivePlayersList.FirstOrDefault(p => p && p.ProfileId == id);

    private void Awake()
    {
        Instance = this;
        Directory.CreateDirectory(DataDirectory);
        Preferences = Read("preferences.json", new WorkbenchPreferences());
        _kits = Read("kits.json", new List<KitDocument>());
        _bookmarks = Read("bookmarks.json", new List<TeleportBookmark>());
        _ledger.Begin("");
        SyncLegacy();
        DebugPlusConfig.GodMode.SettingChanged += LegacyGod;
        DebugPlusConfig.InfiniteStamina.SettingChanged += LegacyStamina;
    }
    private void Update()
    {
        lock (_mainThread) while (_mainThread.Count > 0) _mainThread.Dequeue()();
        if (!ReferenceEquals(_world, null) && (!_world || _world != World)) EndRaid();
        if (!IsCoop || CoopReady) TickOverrides(); else ResetOverrides();
        WorldActions.Tick();
        var account = Session?.GetPhpSessionId() ?? "";
        if (account != _account)
        {
            _account = account;
            Policy = new AccessPolicy();
            _nextAccess = 0;
            _catalog.Clear();
            _icons.Clear();
            _generation++;
        }
        if (!_fetchingAccess && account.Length > 0 && Time.unscaledTime >= _nextAccess) RefreshAccess();
        if (!_autoRaidRequested && Policy.Authorized && !World && DebugPlusConfig.LoadIntoFactoryOnStart.Value)
        {
            _autoRaidRequested = true;
            _ = Submit(CommandKind.StartRaid, new CommandArguments { MapId = "factory4_day", Enabled = true });
        }
        if (Time.unscaledTime >= _nextTargets)
        {
            _nextTargets = Time.unscaledTime + .5f;
            _targets.Clear();
            if (InRaid)
                foreach (var p in _world.AllAlivePlayersList.Where(p => p))
                    _targets.Add(new TargetInfo { Id = p.ProfileId, Name = p.Profile?.Info?.Nickname ?? p.ProfileId, IsBot = p.IsAI, Position = p.Position.ToString("F2") });
            else if (LocalProfileId.Length > 0) _targets.Add(new TargetInfo { Id = LocalProfileId, Name = Session?.Profile?.Info?.Nickname ?? "Your PMC", Position = "Main menu" });
        }
    }
    public void BeginRaid(GameWorld world)
    {
        if (!IsRaidWorld(world) || _world == world) return;
        EndRaid();
        _world = world;
        SessionId = Guid.NewGuid().ToString("N");
        _ledger.Begin(SessionId);
        _generation++;
        CacheLocations();
        Report("Raid context ready: " + world.LocationId);
    }
    private void EndRaid()
    {
        WorldActions.Reset();
        ResetOverrides();
        _world = null;
        SessionId = Policy.SessionId;
        _ledger.Begin(SessionId);
        _generation++;
        InventoryActions.Reset();
        PlayerActions.Reset();
        ResetDoorCache();
        _spawnPoints = Array.Empty<TargetInfo>(); _botZones = Array.Empty<TargetInfo>();
    }
    public void SetCoopSession(string sessionId)
    {
        if (SessionId == sessionId) return;
        ResetOverrides();
        SessionId = sessionId;
        _ledger.Begin(sessionId);
        _generation++;
    }
    public void EndCoopSession() { CoopReady = false; ResetOverrides(); _ledger.Begin(""); _generation++; }

    public async void RefreshAccess()
    {
        if (_fetchingAccess) return;
        _fetchingAccess = true;
        _nextAccess = Time.unscaledTime + 15;
        var account = _account;
        try
        {
            var json = await Task.Run(() => RequestHandler.GetJson("/debugplus/access"));
            if (account != _account) return;
            var policy = JsonConvert.DeserializeObject<AccessPolicy>(json) ?? throw new InvalidDataException("Empty access policy.");
            if (policy.Protocol != Protocol.Version || policy.Version != Protocol.ModVersion) throw new InvalidDataException("Install matching DebugPlus client and server components.");
            Policy = policy;
            if (!InRaid) { SessionId = policy.SessionId; if (_ledger.SessionId != SessionId) _ledger.Begin(SessionId); }
            if (!policy.Authorized && !IsCoop) ResetOverrides();
        }
        catch (Exception e) { if (account != _account) return; Policy = new AccessPolicy { Message = "DebugPlus server unavailable: " + e.Message }; if (!IsCoop) ResetOverrides(); }
        finally { _fetchingAccess = false; }
    }

    public async Task<CommandResult> Submit(CommandKind kind, CommandArguments args, string targetId = null)
    {
        if (kind == CommandKind.Door && string.IsNullOrWhiteSpace(args.ObjectId))
        {
            var door = AimedDoor();
            if (door == null) return CommandResult.Reject(new CommandRequest(), "Aim at a door or choose one from nearby doors.");
            args.ObjectId = door.Id;
        }
        if (kind == CommandKind.Teleport && args.Action == "surface")
        {
            if (!Camera.main || !Physics.Raycast(Camera.main.transform.position, Camera.main.transform.forward, out var hit, 1500, ~0, QueryTriggerInteraction.Ignore))
                return CommandResult.Reject(new CommandRequest(), "Aim at a solid surface first.");
            var point = hit.point + hit.normal * .15f;
            args.X = point.x; args.Y = point.y; args.Z = point.z; args.MapId = MapId; args.Action = "";
        }
        var request = new CommandRequest { SessionId = InRaid ? SessionId : Policy.SessionId, ActorId = LocalProfileId, TargetId = targetId ?? LocalProfileId, Kind = kind, Args = args };
        CommandResult result;
        var invalid = Commands.Validate(request);
        if (invalid.Length > 0) result = CommandResult.Reject(request, invalid);
        else if (!Policy.Authorized) result = CommandResult.Reject(request, "This profile is not on the server's DebugPlus administrator list. " + Policy.Message);
        else if (IsCoop)
            result = CoopReady && CoopSubmit != null ? await CoopSubmit(request) : CommandResult.Reject(request, CoopStatus);
        else result = await ExecuteAuthorized(request);
        Report(kind + ": " + result.Message, result.Status);
        SyncLegacy();
        return result;
    }
    // Called only by the local dispatcher or the Fika companion after authority validation.
    public async Task<CommandResult> ExecuteAuthorized(CommandRequest request)
    {
        var invalid = Commands.Validate(request);
        if (invalid.Length > 0) return CommandResult.Reject(request, invalid);
        if (!_ledger.TryBegin(request, out var existing)) return existing;
        var generation = _generation;
        var key = Commands.Scope(request.Kind) == CommandScope.World ? "world" : request.TargetId;
        if (!_locks.TryGetValue(key, out var gate)) _locks[key] = gate = new SemaphoreSlim(1, 1);
        await gate.WaitAsync();
        CommandResult result;
        try
        {
            if (generation != _generation) return CommandResult.Reject(request, "Session changed while waiting.");
            if (IsCoop && !CoopReady) { result = CommandResult.Reject(request, CoopStatus); _ledger.Finish(request, result); return result; }
            if (Commands.Scope(request.Kind) == CommandScope.Menu) result = await MenuCommand(request);
            else if (!InRaid && Commands.IsInventory(request.Kind)) result = await ExecuteMenuInventory(request);
            else if (!InRaid) result = CommandResult.Reject(request, "Enter a raid to use this action.");
            else if (Commands.Scope(request.Kind) == CommandScope.World) result = await WorldActions.Execute(this, request);
            else
            {
                var player = FindPlayer(request.TargetId);
                if (!player || !player.HealthController.IsAlive) result = CommandResult.Reject(request, "The selected player is not alive in this raid.");
                else if (IsCoop && !player.IsYourPlayer && !player.IsAI) result = CommandResult.Reject(request, "This action must execute on the owning player's client.");
                else if (Commands.IsInventory(request.Kind)) result = await InventoryActions.Execute(this, player, request);
                else result = await PlayerActions.Execute(this, player, request);
            }
        }
        catch (Exception e) { Plugin.Log.LogError(e); result = new CommandResult { RequestId = request.RequestId, Status = CommandStatus.Uncertain, Message = e.Message + " Check the target state before another action." }; }
        finally { gate.Release(); }
        if (generation == _generation) _ledger.Finish(request, result);
        return result;
    }
    public Task<bool> ApplyInventoryReplica(string payload) => InventoryActions.ApplyReplica(this, payload);
    public bool ValidateInventoryReplica(CommandRequest approved, string payload) => InventoryActions.ValidateReplica(this, approved, payload);
    public async Task<CommandResult> ExecuteWorldReplica(CommandRequest request)
    {
        if (request.Kind == CommandKind.RepairGear || request.Kind == CommandKind.ClearMalfunction)
        {
            if (request.SessionId != SessionId || !InRaid) return CommandResult.Reject(request, "Expired replica session.");
            var player = FindPlayer(request.TargetId);
            if (!player) return CommandResult.Reject(request, "Replica target is unavailable.");
            return await PlayerActions.Execute(this, player, request);
        }
        if (request.Kind != CommandKind.SetTime && request.Kind != CommandKind.SetWeather && request.Kind != CommandKind.RestoreEnvironment && request.Kind != CommandKind.Door) return CommandResult.Reject(request, "Unsupported world replication command.");
        if (request.SessionId != SessionId || !InRaid || Commands.Validate(request).Length > 0) return CommandResult.Reject(request, "Expired or invalid world replica.");
        return await WorldActions.Execute(this, request);
    }
    private async Task<CommandResult> ExecuteMenuInventory(CommandRequest request)
    {
        if (request.TargetId != LocalProfileId || World) return CommandResult.Reject(request, "Menu actions require your own profile at the main menu.");
        if (Session is not ClientBackendSession session || !session._profileUpdaters.TryGetValue(LocalProfileId, out var handler) || handler is not ProfileUpdatesHandler native)
            return CommandResult.Reject(request, "The stash update handler is not ready. Open your character inventory and retry.");
        var controller = native._inventoryController;
        if (controller.IsInventoryBlocked()) return CommandResult.Reject(request, "Wait for the current inventory action to finish.");
        // Finish already-queued native moves before capturing the client-created batch.
        // The controller lock prevents the next move/split from overtaking its save.
        controller.Locked = true;
        try
        {
            var flushed = await session.FlushOperationQueue();
            if (flushed.Failed || !ReferenceEquals(session, Session) || World || !session._profileUpdaters.TryGetValue(request.TargetId, out var current) || !ReferenceEquals(current, native))
                return CommandResult.Reject(request, "Inventory changed while finishing pending actions. Retry the spawn.");
            controller.Locked = false;
            var result = InventoryActions.PrepareMenuInventory(request, native._profile, controller, out var transaction);
            controller.Locked = true;
            if (transaction == null) return result;
            var body = JsonConvert.SerializeObject(transaction.Sync);
            var failures = 0;
            while (ReferenceEquals(session, Session) && !World && this)
            {
                try
                {
                    var json = await Task.Run(() => RequestHandler.PostJson("/debugplus/menu/sync", body));
                    var saved = JsonConvert.DeserializeObject<CommandResult>(json) ?? throw new InvalidDataException("No inventory save result.");
                    if (saved.RequestId != request.RequestId) throw new InvalidDataException("Inventory save response did not match the batch.");
                    if (saved.Status == CommandStatus.Completed)
                    {
                        transaction.Notify(true);
                        return result;
                    }
                    if (saved.Status == CommandStatus.Rejected)
                    {
                        if (!transaction.Rollback()) saved.Status = CommandStatus.Uncertain;
                        transaction.Notify(false);
                        return saved;
                    }
                    if (saved.Status == CommandStatus.Uncertain) return saved;
                }
                catch (Exception error)
                {
                    Plugin.Log.LogWarning("Retrying the same client inventory save: " + error.Message);
                    if (failures++ == 0) Report("Waiting for the inventory save connection; retrying automatically.", CommandStatus.Pending);
                }
                await Task.Delay(1500);
            }
            return new CommandResult { RequestId = request.RequestId, Status = CommandStatus.Uncertain, Message = "The profile changed before the inventory save was confirmed." };
        }
        finally { controller.Locked = false; }
    }
    private async Task<CommandResult> MenuCommand(CommandRequest request)
    {
        if (World || !TarkovApplication.Exist(out var app)) return CommandResult.Reject(request, "Return to the main menu first.");
        if (request.Kind == CommandKind.ReloadBackend) await app.RecreateCurrentBackend();
        else if (request.Kind == CommandKind.StartRaid)
        {
            if (string.IsNullOrWhiteSpace(request.Args.MapId)) return CommandResult.Reject(request, "Choose a map.");
            await app.InternalStartGame(request.Args.MapId, true, request.Args.Enabled);
        }
        return CommandResult.Complete(request, "Done.");
    }
    public PlayerOverrides Overrides(string id)
    {
        if (!_overrides.TryGetValue(id, out var value)) _overrides[id] = value = new PlayerOverrides();
        return value;
    }
    public bool GodMode(Player player) => InRaid && (!IsCoop || CoopReady) && player && _overrides.TryGetValue(player.ProfileId, out var value) && value.God;
    private void TickOverrides()
    {
        if (!InRaid) return;
        foreach (var pair in _overrides)
        {
            var p = FindPlayer(pair.Key);
            if (!p || !p.HealthController.IsAlive) continue;
            var o = pair.Value;
            if (p.Physical != null)
            {
                if (o.Stamina) { if (p.Physical.Stamina != null) p.Physical.Stamina.Current = p.Physical.Stamina.TotalCapacity.Value; if (p.Physical.HandsStamina != null) p.Physical.HandsStamina.Current = p.Physical.HandsStamina.TotalCapacity.Value; }
                if (o.Oxygen && p.Physical.Oxygen != null) p.Physical.Oxygen.Current = p.Physical.Oxygen.TotalCapacity.Value;
            }
            var health = p.ActiveHealthController;
            if (health == null) continue;
            if (o.Energy && health.Energy.Current < health.Energy.Maximum) health.ChangeEnergy(health.Energy.Maximum - health.Energy.Current);
            if (o.Hydration && health.Hydration.Current < health.Hydration.Maximum) health.ChangeHydration(health.Hydration.Maximum - health.Hydration.Current);
        }
    }
    public void ResetOverrides() { _overrides.Clear(); SyncLegacy(); }
    private void LegacyGod(object sender, EventArgs args) => LegacyToggle(CommandKind.GodMode, DebugPlusConfig.GodMode);
    private void LegacyStamina(object sender, EventArgs args) => LegacyToggle(CommandKind.Stamina, DebugPlusConfig.InfiniteStamina);
    private async void LegacyToggle(CommandKind kind, ConfigEntry<bool> entry)
    {
        if (_syncingLegacy) return;
        try { await Submit(kind, new CommandArguments { Enabled = entry.Value }); }
        catch (Exception e) { Report(e.Message, CommandStatus.Uncertain); }
        finally { SyncLegacy(); }
    }
    private void SyncLegacy()
    {
        _syncingLegacy = true;
        try
        {
            _overrides.TryGetValue(LocalProfileId, out var value);
            DebugPlusConfig.GodMode.Value = value?.God == true;
            DebugPlusConfig.InfiniteStamina.Value = value?.Stamina == true;
        }
        finally { _syncingLegacy = false; }
    }
    public void Report(string message, CommandStatus status = CommandStatus.Completed)
    {
        _activity.Add(new ActivityEntry { Message = message, Status = status });
        if (_activity.Count > 500) _activity.RemoveRange(0, _activity.Count - 500);
    }
    public void SavePreferences() => Write("preferences.json", Preferences);
    public void SaveKit(KitDocument kit)
    {
        RepairKitSlots(kit);
        var error = kit.Items?.Count == 0 ? "" : ItemTrees.Validate(kit.Items); if (error.Length > 0) throw new InvalidDataException(error);
        if (kit.Schema != 1 || string.IsNullOrWhiteSpace(kit.Name)) throw new InvalidDataException("Kit name/schema is invalid.");
        _kits.RemoveAll(k => k.Id == kit.Id); _kits.Add(kit); Write("kits.json", _kits);
    }
    public void DeleteKit(string id) { _kits.RemoveAll(k => k.Id == id); Write("kits.json", _kits); }
    public void SaveBookmark(TeleportBookmark b)
    {
        BookmarkStore.Save(_bookmarks, b); Write("bookmarks.json", _bookmarks);
    }
    public void DeleteBookmark(TeleportBookmark b) { _bookmarks.Remove(b); Write("bookmarks.json", _bookmarks); }
    public string ExportKit(KitDocument kit) { var name = "kit-" + Guid.NewGuid().ToString("N") + ".json"; Write(name, kit); return Path.Combine(DataDirectory, name); }
    public KitDocument ImportKit(string path)
    {
        if (new FileInfo(path).Length > Protocol.MaxPayloadBytes) throw new InvalidDataException("Kit file is too large.");
        var kit = JsonConvert.DeserializeObject<KitDocument>(File.ReadAllText(path)) ?? throw new InvalidDataException("Empty kit.");
        kit.Id = Guid.NewGuid().ToString("N"); SaveKit(kit); return kit;
    }
    private T Read<T>(string file, T fallback)
    {
        try { var path = Path.Combine(DataDirectory, file); if (!File.Exists(path)) return fallback; var result = JsonConvert.DeserializeObject<T>(File.ReadAllText(path)); return result == null ? fallback : result; }
        catch (Exception e) { Report("Could not load " + file + ": " + e.Message, CommandStatus.Rejected); return fallback; }
    }
    private void Write(string name, object value)
    {
        var path = Path.Combine(DataDirectory, name); var temp = path + ".tmp";
        File.WriteAllText(temp, JsonConvert.SerializeObject(value, Formatting.Indented));
        if (File.Exists(path)) File.Replace(temp, path, path + ".bak"); else File.Move(temp, path);
    }
    public string ExportDiagnostics()
    {
        var name = "diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json";
        Write(name, new { Version = Protocol.ModVersion, MapId, InRaid, IsCoop, IsHeadless, Status, Targets, Plugins = Chainloader.PluginInfos.Values.Select(p => p.Metadata.Name + " " + p.Metadata.Version), Activity, Logging = DebugPlus.Logging.ClientLogging.Instance?.Diagnostics() });
        return Path.Combine(DataDirectory, name);
    }
    private void OnDestroy() { DebugPlusConfig.GodMode.SettingChanged -= LegacyGod; DebugPlusConfig.InfiniteStamina.SettingChanged -= LegacyStamina; EndRaid(); if (Instance == this) Instance = null; }
}
public sealed class PlayerOverrides { public bool God, Stamina, Oxygen, Energy, Hydration; }
