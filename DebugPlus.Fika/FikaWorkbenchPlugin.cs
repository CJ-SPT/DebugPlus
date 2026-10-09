using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BepInEx;
using Comfort.Common;
using DebugPlus.Common;
using DebugPlus.Runtime;
using Fika.Core.Main.Components;
using Fika.Core.Main.Players;
using Fika.Core.Main.Utils;
using Fika.Core.Modding;
using Fika.Core.Modding.Events;
using Fika.Core.Networking;
using Fika.Core.Networking.LiteNetLib;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DebugPlus.Fika;

[BepInPlugin("com.dirtbikercj.debugplus.fika", "DebugPlus Fika", Protocol.ModVersion)]
[BepInDependency("com.dirtbikercj.debugplus", Protocol.ModVersion)]
[BepInDependency("com.fika.core", "2.4.3")]
public sealed class FikaWorkbenchPlugin : BaseUnityPlugin
{
    private static FikaWorkbenchPlugin? _instance;
    private Harmony? _harmony;
    private const double RequestTimeoutSeconds = 30;
    private const double LeaseSeconds = 12;
    private readonly ConcurrentQueue<Action> _incoming = new();
    private readonly Dictionary<NetPeer, Companion> _companions = new();
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly RequestLedger _hostLedger = new();
    private readonly RequestLedger _ownerLedger = new();
    private readonly Dictionary<string, CommandRequest> _authorized = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CommandRequest> _activeOwners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ReplicaWait> _replicaWaits = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task<bool>> _replicaResults = new(StringComparer.Ordinal);
    private readonly HashSet<string> _committedRequests = new(StringComparer.Ordinal);
    private readonly Dictionary<CommandKind, CommandRequest> _environment = new();
    private int _environmentRevision;
    private IFikaNetworkManager? _manager;
    private string _session = "";
    private string _clientNonce = "";
    private double _heartbeat;
    private double _hostSeen;
    private bool _raidStarted;
    private int _generation;
    private WorkbenchRuntime? Runtime => WorkbenchRuntime.Instance;
    private FikaServer? Server => _manager as FikaServer;
    private FikaClient? Client => _manager as FikaClient;
    private static double Now => Time.realtimeSinceStartup;

    private void Awake()
    {
        _instance = this;
        DontDestroyOnLoad(gameObject);
        _harmony = new Harmony("com.dirtbikercj.debugplus.fika.lifecycle");
        _harmony.Patch(AccessTools.Method(typeof(CoopHandler), nameof(CoopHandler.CleanUpForTransit)), postfix: new HarmonyMethod(typeof(FikaWorkbenchPlugin), nameof(OnTransit)));
        FikaEventDispatcher.SubscribeEvent<FikaNetworkManagerCreatedEvent>(OnManagerCreated);
        FikaEventDispatcher.SubscribeEvent<FikaNetworkManagerDestroyedEvent>(OnManagerDestroyed);
        FikaEventDispatcher.SubscribeEvent<FikaRaidStartedEvent>(OnRaidStarted);
        FikaEventDispatcher.SubscribeEvent<FikaGameEndedEvent>(OnGameEnded);
        FikaEventDispatcher.SubscribeEvent<PeerDisconnectedEvent>(OnDisconnected);
        Logger.LogInfo("DebugPlus Fika transport loaded; authorization is owned by the raid host.");
    }

    private void OnDestroy()
    {
        _harmony?.UnpatchSelf();
        if (ReferenceEquals(_instance, this)) _instance = null;
        FikaEventDispatcher.UnsubscribeEvent<FikaNetworkManagerCreatedEvent>(OnManagerCreated);
        FikaEventDispatcher.UnsubscribeEvent<FikaNetworkManagerDestroyedEvent>(OnManagerDestroyed);
        FikaEventDispatcher.UnsubscribeEvent<FikaRaidStartedEvent>(OnRaidStarted);
        FikaEventDispatcher.UnsubscribeEvent<FikaGameEndedEvent>(OnGameEnded);
        FikaEventDispatcher.UnsubscribeEvent<PeerDisconnectedEvent>(OnDisconnected);
        Detach("DebugPlus Fika companion unloaded.");
    }

    private void OnManagerCreated(FikaNetworkManagerCreatedEvent ev) => _incoming.Enqueue(() => Attach(ev.Manager));
    private void OnManagerDestroyed(FikaNetworkManagerDestroyedEvent ev) => _incoming.Enqueue(() =>
    {
        if (ReferenceEquals(ev.Manager, _manager)) Detach("The co-op network session ended.");
    });

    private void Attach(IFikaNetworkManager manager)
    {
        if (ReferenceEquals(manager, _manager)) return;
        Detach("The co-op network session changed.");
        _manager = manager;
        _clientNonce = Guid.NewGuid().ToString("N");
        manager.RegisterPacket<WorkbenchPacket, NetPeer>((packet, peer) => _incoming.Enqueue(() =>
        {
            if (ReferenceEquals(manager, _manager)) Receive(packet, peer);
        }));
        SetUnavailable("Waiting for the raid and compatible DebugPlus companions.");
        BindRuntime();
    }

    private void Detach(string reason)
    {
        EndSession(reason);
        if (_manager != null)
        {
            try { _manager.UnregisterPacket<WorkbenchPacket>(); }
            catch (Exception ex) { Logger.LogDebug(ex.Message); }
        }
        _manager = null;
        if (Runtime != null) { Runtime.CoopSubmit = null; Runtime.ReplicateInventory = null; }
    }

    private void OnRaidStarted(FikaRaidStartedEvent ev) => _incoming.Enqueue(StartRaid);
    private void StartRaid()
    {
        if (_manager == null) return;
        EndSession("The previous raid session expired.");
        _raidStarted = true;
        _clientNonce = Guid.NewGuid().ToString("N");
        if (Server != null) BeginSession(Guid.NewGuid().ToString("N"));
        BindRuntime();
        _heartbeat = 0;
    }

    private void OnGameEnded(FikaGameEndedEvent ev) => _incoming.Enqueue(() => EndSession("The raid ended or entered transit."));
    private static void OnTransit() => _instance?._incoming.Enqueue(() => _instance?.EndSession("The raid entered transit."));

    private void BeginSession(string session)
    {
        _session = session;
        _generation++;
        _hostLedger.Begin(session);
        _ownerLedger.Begin(session);
        Runtime?.SetCoopSession(session);
    }

    private void EndSession(string reason)
    {
        _generation++;
        _raidStarted = false;
        _session = "";
        _companions.Clear();
        _authorized.Clear();
        _activeOwners.Clear();
        _replicaResults.Clear();
        _committedRequests.Clear();
        _environment.Clear();
        _environmentRevision = 0;
        foreach (var wait in _replicaWaits.Values) wait.Completion.TrySetResult(false);
        _replicaWaits.Clear();
        _hostLedger.Begin("");
        _ownerLedger.Begin("");
        foreach (var pending in _pending.Values.ToArray())
            pending.Completion.TrySetResult(Uncertain(pending.Request, reason + " The command may have executed; inspect state before submitting another request."));
        _pending.Clear();
        SetUnavailable(reason);
        Runtime?.EndCoopSession();
    }

    private void OnDisconnected(PeerDisconnectedEvent ev) => _incoming.Enqueue(() => Disconnect(ev));
    private void Disconnect(PeerDisconnectedEvent ev)
    {
        if (!ReferenceEquals(ev.NetworkManager, _manager)) return;
        _companions.Remove(ev.Peer);
        foreach (var entry in _replicaWaits.Where(p => p.Value.Peers.Contains(ev.Peer)).ToArray())
        {
            _replicaWaits.Remove(entry.Key);
            entry.Value.Completion.TrySetResult(false);
        }
        foreach (var entry in _pending.Where(p => ReferenceEquals(p.Value.Owner, ev.Peer)).ToArray())
        {
            _pending.Remove(entry.Key);
            entry.Value.Completion.TrySetResult(Uncertain(entry.Value.Request, "The target disconnected. Inspect its state before issuing another command."));
        }
        SetUnavailable("The peer roster changed; verifying companions.");
        if (Client != null && ReferenceEquals(ev.Peer, Client.ServerConnection)) EndSession("The authoritative host disconnected.");
        _heartbeat = 0;
    }

    private void BindRuntime()
    {
        if (Runtime == null || _manager == null) return;
        Runtime.CoopSubmit = Submit;
        Runtime.ReplicateInventory = ReplicateInventory;
        if (_session.Length > 0 && Runtime.SessionId != _session) Runtime.SetCoopSession(_session);
    }

    private void Update()
    {
        while (_incoming.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception ex) { Logger.LogWarning("Rejected malformed DebugPlus packet: " + ex.Message); }
        }
        if (_manager == null && Singleton<IFikaNetworkManager>.Instantiated)
            Attach(Singleton<IFikaNetworkManager>.Instance);
        if (_manager == null) return;
        BindRuntime();
        foreach (var entry in _pending.Where(p => Now >= p.Value.Deadline).ToArray())
        {
            _pending.Remove(entry.Key);
            entry.Value.Completion.TrySetResult(Uncertain(entry.Value.Request, "The command timed out. It may have executed; inspect state before issuing another request."));
        }
        foreach (var entry in _replicaWaits.Where(p => Now >= p.Value.Deadline).ToArray())
        {
            _replicaWaits.Remove(entry.Key);
            entry.Value.Completion.TrySetResult(false);
        }
        if (!_raidStarted) return;
        if (Client != null && Runtime?.CoopReady == true && Now - _hostSeen > LeaseSeconds)
            SetUnavailable("The host's DebugPlus heartbeat expired. Gameplay overrides are suspended.");
        if (Now < _heartbeat) return;
        _heartbeat = Now + 2;
        if (Server != null)
        {
            if (HostReadiness(false).Length == 0)
                foreach (var companion in _companions.ToArray())
                    if (Connected(companion.Key) && companion.Value.EnvironmentRevision != _environmentRevision && !companion.Value.SynchronizingEnvironment && !companion.Value.EnvironmentFailed)
                        _ = SynchronizeEnvironment(companion.Key, companion.Value);
            var reason = HostReadiness();
            SetReady(reason.Length == 0, reason.Length == 0 ? "Host authority verified; all peers compatible." : reason);
            foreach (var companion in _companions.ToArray())
                if (Connected(companion.Key)) Send(companion.Key, new WorkbenchPacket { Kind = MessageKind.State, Session = _session, Nonce = companion.Value.Nonce, Ready = reason.Length == 0, Message = Runtime?.CoopStatus ?? reason });
        }
        else if (Client?.ServerConnection is { } host && Connected(host))
            Send(host, new WorkbenchPacket { Kind = MessageKind.Hello, Session = _session, Nonce = _clientNonce });
    }

    private string HostReadiness(bool requireEnvironment = true)
    {
        if (!_raidStarted || Server == null || Runtime == null || !Runtime.InRaid || _session.Length == 0) return "The host raid is not ready.";
        var policy = Runtime.Policy;
        if (policy == null || policy.Protocol != Protocol.Version || policy.Version != Protocol.ModVersion || policy.AdminProfileIds == null || !Guid.TryParseExact(policy.SessionId, "N", out _))
            return "The host has no compatible server permission policy.";
        if (Server.CoopHandler == null) return "Waiting for Fika's player roster.";
        var peers = ConnectedPeers();
        foreach (var peer in peers)
        {
            if (!TryActor(peer, out _)) return "Waiting for Fika to bind a connected player.";
            if (!_companions.TryGetValue(peer, out var companion) || !companion.Compatible || Now - companion.LastSeen > LeaseSeconds)
                return "Every connected peer requires a matching DebugPlus companion.";
            if (requireEnvironment && companion.EnvironmentFailed) return "A peer could not synchronize the host environment. Reconnect that peer before further debugging.";
            if (requireEnvironment && companion.EnvironmentRevision != _environmentRevision) return "Synchronizing the host's time and weather with connected peers.";
        }
        return "";
    }

    private List<NetPeer> ConnectedPeers()
    {
        var result = new List<NetPeer>();
        if (Server?.NetServer == null) return result;
        foreach (var peer in Server.NetServer)
            if (peer is NetPeer connected && Connected(connected)) result.Add(connected);
        return result;
    }

    private static bool Connected(NetPeer? peer) => peer != null && peer.ConnectionState == ConnectionState.Connected;

    private bool TryActor(NetPeer peer, out string profileId)
    {
        profileId = "";
        // This is Fika's actual connection/player binding, never an identity supplied in our payload.
        var player = peer.Player;
        var handler = Server?.CoopHandler;
        if (!Connected(peer) || player == null || player.IsAI || handler == null || !handler.HumanPlayers.Contains(player) || handler.ExtractedPlayers.Contains(player.NetId)) return false;
        profileId = player.ProfileId;
        return !string.IsNullOrWhiteSpace(profileId);
    }

    private FikaPlayer? Player(string profileId) => _manager?.CoopHandler?.HumanPlayers.FirstOrDefault(p => p != null && p.ProfileId == profileId && !_manager.CoopHandler.ExtractedPlayers.Contains(p.NetId));
    private bool IsAdmin(string profileId) => Runtime?.Policy?.AdminProfileIds?.Contains(profileId, StringComparer.Ordinal) == true;
    private static string Key(CommandRequest request) => request.ActorId + ":" + request.RequestId;

    private Task<CommandResult> Submit(CommandRequest source)
    {
        if (!_raidStarted || Runtime == null || _session.Length == 0 || !Runtime.CoopReady)
            return Task.FromResult(CommandResult.Reject(source, Runtime?.CoopStatus ?? "Co-op debugging is unavailable."));
        // Clone so UI edits cannot mutate a command already authorized or in flight.
        var request = Newtonsoft.Json.JsonConvert.DeserializeObject<CommandRequest>(WorkbenchPacket.Encode(source), WorkbenchPacket.JsonSettings)!;
        request.ActorId = Runtime.LocalProfileId;
        request.SessionId = _session;
        if (Server != null) return ExecuteAsHost(request, null);
        if (Client?.ServerConnection is not { } host || !Connected(host)) return Task.FromResult(CommandResult.Reject(request, "The authoritative host is disconnected."));
        var key = Key(request);
        if (_pending.TryGetValue(key, out var existing)) return existing.Completion.Task;
        var pending = new Pending(request, host, Now + RequestTimeoutSeconds);
        _pending.Add(key, pending);
        Send(host, new WorkbenchPacket { Kind = MessageKind.Request, Session = _session, Payload = WorkbenchPacket.Encode(request) });
        return pending.Completion.Task;
    }

    private void Receive(WorkbenchPacket packet, NetPeer peer)
    {
        if (!_raidStarted || _manager == null) return;
        if (Server != null)
        {
            if (packet.Kind == MessageKind.Hello)
            {
                if (!Connected(peer) || !Guid.TryParseExact(packet.Nonce, "N", out _)) return;
                if (!_companions.TryGetValue(peer, out var companion) || companion.Nonce != packet.Nonce)
                    _companions[peer] = companion = new Companion { Nonce = packet.Nonce };
                companion.Compatible = packet.Compatible;
                companion.LastSeen = Now;
                Send(peer, new WorkbenchPacket { Kind = MessageKind.Welcome, Session = _session, Nonce = packet.Nonce, Message = packet.Compatible ? "Host companion verified." : "DebugPlus versions do not match." });
                _heartbeat = 0;
                return;
            }
            if (!packet.Compatible || packet.Session != _session || !TryActor(peer, out var actor)) return;
            if (packet.Kind == MessageKind.ReplicaAck)
            {
                AcceptReplicaAck(packet, peer);
                return;
            }
            if (packet.Kind == MessageKind.InventoryCommit)
            {
                var replica = packet.Decode<ReplicationMessage>();
                if (replica == null || replica.World || !Guid.TryParseExact(replica.Id, "N", out _)) return;
                var key = Key(replica.Request);
                if (!_authorized.TryGetValue(key, out var approved) || !UsesInventoryReplica(approved.Kind) || approved.TargetId != actor || replica.Request.SessionId != _session) return;
                if (!WorkbenchPacket.DeltaMatches(replica.Delta, approved)) { SendReplicaAck(peer, replica.Id, false); return; }
                // All execution fields come from the host's authorized request, never from the owner.
                replica.Request = approved;
                _ = RelayInventoryCommit(replica, peer);
                return;
            }
            if (packet.Kind == MessageKind.Request)
            {
                var request = packet.Decode<CommandRequest>();
                if (request == null) return;
                request.ActorId = actor;
                _ = ReplyToRequester(request, peer);
            }
            else if (packet.Kind == MessageKind.OwnerResult)
            {
                var result = packet.Decode<CommandResult>();
                if (result == null || !Enum.IsDefined(typeof(CommandStatus), result.Status) || result.Status == CommandStatus.Pending) return;
                var pending = _pending.Values.FirstOrDefault(p => p.Request.RequestId == result.RequestId && ReferenceEquals(p.Owner, peer));
                if (pending == null || pending.Request.TargetId != actor) return;
                _pending.Remove(Key(pending.Request));
                pending.Completion.TrySetResult(result);
            }
            return;
        }
        // Client handlers only accept instructions/results from the current server connection.
        if (Client == null || !ReferenceEquals(Client.ServerConnection, peer) || !Connected(peer)) return;
        if (!packet.Compatible)
        {
            SetUnavailable("The host uses an incompatible DebugPlus version. Install matching components on every participant.");
            return;
        }
        if (packet.Kind == MessageKind.Welcome)
        {
            if (packet.Nonce != _clientNonce || !Guid.TryParseExact(packet.Session, "N", out _)) return;
            if (_session.Length == 0) BeginSession(packet.Session);
            else if (packet.Session != _session)
            {
                EndSession("The host raid session changed.");
                // A new raid-start event is required before accepting another session.
                return;
            }
            _hostSeen = Now;
            return;
        }
        if (packet.Session != _session || _session.Length == 0) return;
        _hostSeen = Now;
        if (packet.Kind == MessageKind.ReplicaAck) AcceptReplicaAck(packet, peer);
        else if (packet.Kind == MessageKind.Replica)
        {
            var replica = packet.Decode<ReplicationMessage>();
            if (replica != null && Guid.TryParseExact(replica.Id, "N", out _) && replica.Request.SessionId == _session)
                _ = ApplyReplicaAndReply(replica, peer);
        }
        else if (packet.Kind == MessageKind.State && packet.Nonce == _clientNonce) SetReady(packet.Ready, packet.Message);
        else if (packet.Kind == MessageKind.Execute)
        {
            var request = packet.Decode<CommandRequest>();
            if (request != null) _ = ExecuteAsOwner(request, peer);
        }
        else if (packet.Kind == MessageKind.Result)
        {
            var result = packet.Decode<CommandResult>();
            if (result == null || result.Status == CommandStatus.Pending || !Enum.IsDefined(typeof(CommandStatus), result.Status)) return;
            var key = Runtime!.LocalProfileId + ":" + result.RequestId;
            if (_pending.TryGetValue(key, out var pending))
            {
                _pending.Remove(key);
                pending.Completion.TrySetResult(result);
            }
        }
    }

    private async Task ReplyToRequester(CommandRequest request, NetPeer peer)
    {
        var generation = _generation;
        var result = await ExecuteAsHost(request, peer);
        if (_generation == generation && Connected(peer))
            Send(peer, new WorkbenchPacket { Kind = MessageKind.Result, Session = _session, Payload = WorkbenchPacket.Encode(result) });
    }

    private async Task<CommandResult> ExecuteAsHost(CommandRequest request, NetPeer? requester)
    {
        var error = Commands.Validate(request);
        if (error.Length == 0 && (request.SessionId != _session || !_raidStarted)) error = "The raid session expired.";
        if (error.Length == 0) error = HostReadiness();
        if (error.Length == 0 && !IsAdmin(request.ActorId)) error = "The server has not authorized this profile as a DebugPlus administrator.";
        if (error.Length == 0 && Commands.Scope(request.Kind) == CommandScope.Menu) error = "Menu commands are unavailable in a raid.";
        if (error.Length > 0) return CommandResult.Reject(request, error);
        if (!_hostLedger.TryBegin(request, out var duplicate)) return duplicate;
        _authorized.Add(Key(request), request);
        var generation = _generation;
        CommandResult result;
        NetPeer? targetOwner = null;
        try
        {
            if (Commands.Scope(request.Kind) == CommandScope.World)
            {
                result = await Runtime!.ExecuteAuthorized(request);
                if (generation == _generation && result.Status == CommandStatus.Completed && IsReplicatedWorld(request.Kind))
                {
                    var recipients = ConnectedPeers();
                    var replica = new ReplicationMessage { Id = Guid.NewGuid().ToString("N"), Request = request, World = true };
                    var synchronized = await BroadcastReplica(replica, null);
                    if (generation == _generation && IsEnvironment(request.Kind)) RememberEnvironment(request, synchronized ? recipients : Array.Empty<NetPeer>());
                    if (!synchronized) result = Uncertain(request, "The host applied the world command, but a peer did not acknowledge it. Inspect world state before retrying.");
                }
            }
            else
            {
                var target = Player(request.TargetId);
                if (target == null || !target.HealthController.IsAlive)
                    result = CommandResult.Reject(request, "The target player is not alive and connected in this raid.");
                else if (target.IsYourPlayer && !FikaBackendUtils.IsHeadless)
                    result = await Runtime!.ExecuteAuthorized(request);
                else
                {
                    var owner = ConnectedPeers().FirstOrDefault(p => ReferenceEquals(p.Player, target));
                    targetOwner = owner;
                    if (owner == null || !_companions.TryGetValue(owner, out var companion) || !companion.Compatible)
                        result = CommandResult.Reject(request, "The target has no compatible, connected DebugPlus companion.");
                    else
                    {
                        var pending = new Pending(request, owner, Now + RequestTimeoutSeconds);
                        _pending.Add(Key(request), pending);
                        Send(owner, new WorkbenchPacket { Kind = MessageKind.Execute, Session = _session, Payload = WorkbenchPacket.Encode(request) });
                        result = await pending.Completion.Task;
                    }
                }
            }
            if (generation == _generation && result.Status == CommandStatus.Completed && IsComponentUpdate(request.Kind))
            {
                if (targetOwner != null)
                {
                    var observedResult = await Runtime!.ExecuteWorldReplica(request);
                    if (observedResult.Status != CommandStatus.Completed) result = Uncertain(request, "The owner applied the equipment update, but the host could not confirm its observed state.");
                }
                if (result.Status == CommandStatus.Completed)
                {
                    var replica = new ReplicationMessage { Id = Guid.NewGuid().ToString("N"), Request = request, World = true };
                    if (!await BroadcastReplica(replica, targetOwner)) result = Uncertain(request, "The equipment update completed on its owner, but a peer did not acknowledge it.");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
            result = Uncertain(request, "Execution failed after authorization. Inspect state before retrying. " + ex.Message);
        }
        if (generation == _generation) { _hostLedger.Finish(request, result); _authorized.Remove(Key(request)); }
        return result;
    }

    private async Task ExecuteAsOwner(CommandRequest request, NetPeer host)
    {
        var generation = _generation;
        var error = Commands.Validate(request);
        if (error.Length == 0 && (Runtime == null || !Runtime.InRaid || !Runtime.CoopReady || request.SessionId != _session)) error = "The target raid session or companion readiness expired.";
        if (error.Length == 0 && (request.TargetId != Runtime!.LocalProfileId || Commands.Scope(request.Kind) != CommandScope.Player)) error = "The host instruction does not target this player's controller.";
        CommandResult result;
        if (error.Length > 0) result = CommandResult.Reject(request, error);
        else if (!_ownerLedger.TryBegin(request, out result!)) { }
        else
        {
            _activeOwners[Key(request)] = request;
            try { result = await Runtime!.ExecuteAuthorized(request); }
            catch (Exception ex) { Logger.LogError(ex); result = Uncertain(request, "The owning controller could not confirm completion. " + ex.Message); }
            if (generation == _generation) _activeOwners.Remove(Key(request));
            if (generation == _generation) _ownerLedger.Finish(request, result);
        }
        if (generation == _generation && Connected(host))
            Send(host, new WorkbenchPacket { Kind = MessageKind.OwnerResult, Session = _session, Payload = WorkbenchPacket.Encode(result) });
    }

    private static bool IsReplicatedWorld(CommandKind kind) => IsEnvironment(kind) || kind == CommandKind.Door || IsComponentUpdate(kind);
    private static bool IsEnvironment(CommandKind kind) => kind is CommandKind.SetTime or CommandKind.SetWeather or CommandKind.RestoreEnvironment;
    private static bool IsComponentUpdate(CommandKind kind) => kind is CommandKind.RepairGear or CommandKind.ClearMalfunction;
    private static bool UsesInventoryReplica(CommandKind kind) => Commands.IsInventory(kind);

    private void RememberEnvironment(CommandRequest request, IEnumerable<NetPeer> synchronizedPeers)
    {
        if (request.Kind == CommandKind.RestoreEnvironment) _environment.Clear();
        _environment[request.Kind] = Clone(request);
        var previousRevision = _environmentRevision;
        _environmentRevision++;
        foreach (var peer in synchronizedPeers)
            if (_companions.TryGetValue(peer, out var companion) && companion.EnvironmentRevision == previousRevision) companion.EnvironmentRevision = _environmentRevision;
        _heartbeat = 0;
    }

    private static CommandRequest Clone(CommandRequest request) => Newtonsoft.Json.JsonConvert.DeserializeObject<CommandRequest>(WorkbenchPacket.Encode(request), WorkbenchPacket.JsonSettings)!;

    // Reconnects receive current idempotent environment state; command mutations are never retried.
    private async Task SynchronizeEnvironment(NetPeer peer, Companion companion)
    {
        companion.SynchronizingEnvironment = true;
        var generation = _generation;
        var revision = _environmentRevision;
        var success = true;
        try
        {
            var snapshots = _environment.OrderBy(p => p.Key == CommandKind.RestoreEnvironment ? 0 : p.Key == CommandKind.SetTime ? 1 : 2).Select(p => Clone(p.Value)).ToArray();
            foreach (var request in snapshots)
            {
                request.RequestId = Guid.NewGuid().ToString("N");
                if (request.Kind == CommandKind.SetTime && TODSkyProvider.IsAvailable) request.Args.Value = TODSkyProvider.Instance.Cycle.Hour;
                var replica = new ReplicationMessage { Id = Guid.NewGuid().ToString("N"), Request = request, World = true };
                var wait = new ReplicaWait(new[] { peer }, Now + RequestTimeoutSeconds - 4);
                _replicaWaits.Add(replica.Id, wait);
                Send(peer, new WorkbenchPacket { Kind = MessageKind.Replica, Session = _session, Payload = WorkbenchPacket.Encode(replica) });
                if (!await wait.Completion.Task || generation != _generation) { success = false; break; }
            }
        }
        catch (Exception ex) { Logger.LogWarning("Environment synchronization failed: " + ex.Message); success = false; }
        if (generation != _generation || !_companions.TryGetValue(peer, out var current) || !ReferenceEquals(current, companion)) return;
        companion.SynchronizingEnvironment = false;
        companion.EnvironmentFailed = !success;
        if (success) companion.EnvironmentRevision = revision;
        _heartbeat = 0;
    }

    private Task<bool> ReplicateInventory(CommandRequest request, string nativeDelta)
    {
        if (!_raidStarted || request.SessionId != _session || !UsesInventoryReplica(request.Kind) || Runtime?.CoopReady != true || !WorkbenchPacket.DeltaMatches(nativeDelta, request)) return Task.FromResult(false);
        var key = Key(request);
        if (Server != null)
        {
            if (!_authorized.ContainsKey(key) || request.TargetId != Runtime.LocalProfileId || !_committedRequests.Add(key)) return Task.FromResult(false);
            return BroadcastReplica(new ReplicationMessage { Id = Guid.NewGuid().ToString("N"), Request = request, Delta = nativeDelta }, null);
        }
        if (!_activeOwners.ContainsKey(key) || request.TargetId != Runtime.LocalProfileId || Client?.ServerConnection is not { } host || !Connected(host) || !_committedRequests.Add(key)) return Task.FromResult(false);
        var replica = new ReplicationMessage { Id = Guid.NewGuid().ToString("N"), Request = request, Delta = nativeDelta };
        var wait = new ReplicaWait(new[] { host }, Now + RequestTimeoutSeconds - 2);
        _replicaWaits.Add(replica.Id, wait);
        Send(host, new WorkbenchPacket { Kind = MessageKind.InventoryCommit, Session = _session, Payload = WorkbenchPacket.Encode(replica) });
        return wait.Completion.Task;
    }

    private async Task RelayInventoryCommit(ReplicationMessage replica, NetPeer owner)
    {
        var generation = _generation;
        if (!_replicaResults.TryGetValue(replica.Id, out var task))
        {
            if (_replicaResults.Count >= 10000) { SendReplicaAck(owner, replica.Id, false); return; }
            task = CommitOnHost(replica, owner);
            _replicaResults[replica.Id] = task;
        }
        var success = await task;
        if (generation == _generation) SendReplicaAck(owner, replica.Id, success);
    }

    private async Task<bool> CommitOnHost(ReplicationMessage replica, NetPeer owner)
    {
        if (Runtime == null || !_committedRequests.Add(Key(replica.Request))) return false;
        try
        {
            if (!AllowedRemovals(replica.Request, replica.Delta) || !Runtime.ValidateInventoryReplica(replica.Request, replica.Delta) || !await Runtime.ApplyInventoryReplica(replica.Delta)) return false;
            return await BroadcastReplica(replica, owner);
        }
        catch (Exception ex) { Logger.LogError(ex); return false; }
    }

    private Task<bool> BroadcastReplica(ReplicationMessage replica, NetPeer? except)
    {
        if (Server == null || replica.Request.SessionId != _session || HostReadiness().Length > 0) return Task.FromResult(false);
        var peers = ConnectedPeers().Where(p => !ReferenceEquals(p, except)).ToArray();
        if (peers.Length == 0) return Task.FromResult(true);
        var wait = new ReplicaWait(peers, Now + RequestTimeoutSeconds - 4);
        _replicaWaits.Add(replica.Id, wait);
        foreach (var peer in peers)
            Send(peer, new WorkbenchPacket { Kind = MessageKind.Replica, Session = _session, Payload = WorkbenchPacket.Encode(replica) });
        return wait.Completion.Task;
    }

    private async Task ApplyReplicaAndReply(ReplicationMessage replica, NetPeer host)
    {
        var generation = _generation;
        if (!_replicaResults.TryGetValue(replica.Id, out var task))
        {
            if (_replicaResults.Count >= 10000) { SendReplicaAck(host, replica.Id, false); return; }
            task = ApplyReplica(replica);
            _replicaResults[replica.Id] = task;
        }
        var success = await task;
        if (generation == _generation) SendReplicaAck(host, replica.Id, success);
    }

    private async Task<bool> ApplyReplica(ReplicationMessage replica)
    {
        if (Runtime == null || !Runtime.InRaid || replica.Request.SessionId != _session || Commands.Validate(replica.Request).Length > 0) return false;
        try
        {
            if (replica.World)
            {
                if (!IsReplicatedWorld(replica.Request.Kind)) return false;
                return (await Runtime.ExecuteWorldReplica(replica.Request)).Status == CommandStatus.Completed;
            }
            if (!UsesInventoryReplica(replica.Request.Kind) || replica.Request.TargetId == Runtime.LocalProfileId || !WorkbenchPacket.DeltaMatches(replica.Delta, replica.Request)) return false;
            return AllowedRemovals(replica.Request, replica.Delta) && Runtime.ValidateInventoryReplica(replica.Request, replica.Delta) && await Runtime.ApplyInventoryReplica(replica.Delta);
        }
        catch (Exception ex) { Logger.LogError(ex); return false; }
    }

    private bool AllowedRemovals(CommandRequest request, string delta)
    {
        try
        {
            var data = JObject.Parse(delta);
            if (data["RemovedRootIds"] is not JArray removed || removed.Count > Protocol.MaxItems || removed.Any(t => t.Type != JTokenType.String)) return false;
            var ids = removed.Values<string>().ToArray();
            if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) return false;
            if (request.Kind is CommandKind.SpawnItem or CommandKind.SpawnKit) return ids.Length == 0;
            if (request.Kind == CommandKind.EquipKit)
            {
                var player = Runtime?.FindPlayer(request.TargetId);
                if (player == null) return false;
                var included = ItemTrees.ReplacementSlots(request.Args);
                var allowed = new HashSet<string>(player.Equipment.Slots.Where(s => included.Contains(s.ID) && s.ContainedItem != null).Select(s => s.ContainedItem.Id.ToString()), StringComparer.Ordinal);
                return request.Args.ClearEquipment ? ids.All(id => id != null) && allowed.SetEquals(ids.Select(id => id!)) : ids.All(id => id != null && allowed.Contains(id));
            }
            return false;
        }
        catch (Exception ex) { Logger.LogDebug("Inventory removal validation rejected: " + ex.Message); return false; }
    }

    private void AcceptReplicaAck(WorkbenchPacket packet, NetPeer peer)
    {
        var ack = packet.Decode<ReplicaAcknowledgement>();
        if (ack == null || !_replicaWaits.TryGetValue(ack.Id, out var wait) || !wait.Peers.Remove(peer)) return;
        if (!ack.Success || wait.Peers.Count == 0)
        {
            _replicaWaits.Remove(ack.Id);
            wait.Completion.TrySetResult(ack.Success);
        }
    }

    private void SendReplicaAck(NetPeer peer, string id, bool success) => Send(peer, new WorkbenchPacket { Kind = MessageKind.ReplicaAck, Session = _session, Payload = WorkbenchPacket.Encode(new ReplicaAcknowledgement { Id = id, Success = success }) });

    private void Send(NetPeer peer, WorkbenchPacket packet)
    {
        if (!Connected(peer) || _manager == null) return;
        try { _manager.SendDataToPeer(ref packet, DeliveryMethod.ReliableOrdered, peer); }
        catch (Exception ex) { Logger.LogWarning("DebugPlus send failed: " + ex.Message); SetUnavailable("DebugPlus transport failed; inspect command state before retrying."); }
    }

    private void SetReady(bool ready, string message)
    {
        if (Runtime == null) return;
        Runtime.CoopReady = ready;
        Runtime.CoopStatus = message;
        if (!ready) Runtime.ResetOverrides();
    }
    private void SetUnavailable(string reason) => SetReady(false, reason);
    private static CommandResult Uncertain(CommandRequest request, string message) => new() { RequestId = request.RequestId, Status = CommandStatus.Uncertain, Message = message };

    private sealed class Companion
    {
        public string Nonce = "";
        public bool Compatible;
        public double LastSeen;
        public int EnvironmentRevision = -1;
        public bool SynchronizingEnvironment;
        public bool EnvironmentFailed;
    }
    private sealed class Pending
    {
        public readonly CommandRequest Request;
        public readonly NetPeer Owner;
        public readonly double Deadline;
        public readonly TaskCompletionSource<CommandResult> Completion = new();
        public Pending(CommandRequest request, NetPeer owner, double deadline) { Request = request; Owner = owner; Deadline = deadline; }
    }
    private sealed class ReplicaWait
    {
        public readonly HashSet<NetPeer> Peers;
        public readonly double Deadline;
        public readonly TaskCompletionSource<bool> Completion = new();
        public ReplicaWait(IEnumerable<NetPeer> peers, double deadline) { Peers = new HashSet<NetPeer>(peers); Deadline = deadline; }
    }
}
