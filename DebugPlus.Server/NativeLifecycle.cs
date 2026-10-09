using System.Collections.Concurrent;
using System.Reflection;
using HarmonyLib;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.DI.Routing;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Eft.Match;

namespace DebugPlus.Server;

internal sealed class ProfileGate
{
    private static readonly ConcurrentDictionary<string, ProfileGate> Gates = new(StringComparer.Ordinal);
    public static ProfileGate For(MongoId id) => Gates.GetOrAdd(id.ToString(), _ => new());
    public SemaphoreSlim Semaphore { get; } = new(1, 1);
    public bool InRaid { get; set; }
    public bool NeedsRecovery { get; set; }
    public static bool Ready { get; set; }
    public IDisposable Enter()
    {
        Semaphore.Wait();
        if (NeedsRecovery)
        {
            Semaphore.Release();
            throw new InvalidOperationException("DebugPlus blocked inventory/raid changes after a failed profile save. Resolve storage failure and recover the profile before continuing.");
        }
        return new Lease(Semaphore);
    }
    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) semaphore.Release(); }
    }
}

[Injectable(InjectionType.Singleton, OnLoadOrder.PostLoad + 9000)]
public sealed class DebugStartup(ServerPolicy policy) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        policy.Initialize();
        new Harmony("com.CJ.DebugPlus.Server").PatchAll(Assembly.GetExecutingAssembly());
        ProfileGate.Ready = true;
        return Task.CompletedTask;
    }
}

// Gate at MatchController, outside the lifecycle service replaced by Fika.
// Start stays marked in raid on failure or disconnect until the native end succeeds.
[HarmonyPatch(typeof(MatchController), nameof(MatchController.StartLocalRaidAsync))]
internal static class RaidStartPatch
{
    [HarmonyPrefix]
    private static void Prefix(MongoId sessionId, out IDisposable __state)
    {
        var gate = ProfileGate.For(sessionId);
        __state = gate.Enter();
        gate.InRaid = true;
    }
    [HarmonyPostfix]
    private static void Postfix(ref Task<StartLocalRaidResponseData> __result, IDisposable __state) => __result = Finish(__result, __state);
    private static async Task<StartLocalRaidResponseData> Finish(Task<StartLocalRaidResponseData> task, IDisposable lease)
    {
        try { return await task; } finally { lease.Dispose(); }
    }
    [HarmonyFinalizer]
    private static void Finalizer(Exception? __exception, IDisposable? __state) { if (__exception != null) __state?.Dispose(); }
}

[HarmonyPatch(typeof(MatchController), nameof(MatchController.EndLocalRaidAsync))]
internal static class RaidEndPatch
{
    [HarmonyPrefix]
    private static void Prefix(MongoId sessionId, out IDisposable __state) => __state = ProfileGate.For(sessionId).Enter();
    [HarmonyPostfix]
    private static void Postfix(MongoId sessionId, EndLocalRaidRequestData request, ref Task __result, IDisposable __state) =>
        __result = Finish(__result, sessionId, request, __state);
    private static async Task Finish(Task task, MongoId id, EndLocalRaidRequestData request, IDisposable lease)
    {
        try
        {
            await task;
            ProfileGate.For(id).InRaid = request.Results?.IsMapToMapTransfer() == true;
        }
        finally { lease.Dispose(); }
    }
    [HarmonyFinalizer]
    private static void Finalizer(Exception? __exception, IDisposable? __state) { if (__exception != null) __state?.Dispose(); }
}

[HarmonyPatch(typeof(ItemEventRouter), nameof(ItemEventRouter.HandleItemEvent))]
internal static class InventoryEventPatch
{
    [HarmonyPrefix]
    private static void Prefix(MongoId sessionID, out IDisposable __state) => __state = ProfileGate.For(sessionID).Enter();
    [HarmonyPostfix]
    private static void Postfix(ref ValueTask<ItemEventRouterResponse> __result, IDisposable __state) => __result = Finish(__result, __state);
    private static async ValueTask<ItemEventRouterResponse> Finish(ValueTask<ItemEventRouterResponse> task, IDisposable lease)
    {
        try { return await task; } finally { lease.Dispose(); }
    }
    [HarmonyFinalizer]
    private static void Finalizer(Exception? __exception, IDisposable? __state) { if (__exception != null) __state?.Dispose(); }
}
