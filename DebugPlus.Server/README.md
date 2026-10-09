# DebugPlus server component

This component supplies the authoritative administrator policy and persistent main-menu inventory operations. It targets SPT 4.1.5 / .NET 10 and also guards the native raid lifecycle used by Fika 2.4.3.

Install `DebugPlus.Server.dll` and the matching `DebugPlus.Common.dll` together under `SPT_Runtime/user/mods/DebugPlus/`. On the next user-controlled server startup, the component creates `config.json` beside its assembly if it does not exist:

```json
{
  "AdminProfileIds": []
}
```

Add the PMC character `_id` values of authorized administrators. The workbench access response reports your own PMC ID. An account/session ID is not interchangeable with its PMC ID. Access is denied by default, and the file is reread for every command, so revocation takes effect immediately. The client cannot modify this policy.

An authenticated headless account without a playable PMC can read the administrator policy and receives a stable menu session token, while `ProfileId` remains empty and `Authorized` remains false. This lets a headless host enforce raid permissions without granting it menu inventory access. Unknown server sessions receive no policy.

## API

These authenticated SPT static routes return raw JSON, with shared `DebugPlus.Common` wire types. Protocol 3 and matching component versions are required. Empty-loadout replacement uses an explicit ClearEquipment flag and accepts a removal-only client batch; all removed item IDs must match ordinary equipment slots and their contents. Protected personal slots cannot be removed.

| Route | Request | Response |
| --- | --- | --- |
| `/debugplus/access` | Empty native request | `AccessPolicy`: authenticated PMC `ProfileId`, administrator IDs, `Authorized`, and opaque menu `SessionId` |
| `/debugplus/menu/preview` | `CommandRequest` for `EquipKit` | `InventoryPreview`: incoming/displaced item summary including nested contents, and opaque `Hash`; an unavailable preview has an empty hash and explanatory summary |
| `/debugplus/menu/execute` | `CommandRequest` for `SpawnItem`, `SpawnKit`, or `EquipKit` | `CommandResult`; completed `DataJson` includes `RefreshBackend: true` and `InventoryHash` |

Menu requests must use the session issued by `/debugplus/access` and target that authenticated PMC. The server overwrites the supplied actor ID. Sessions expire when the server process restarts. Retrying a request ID returns its recorded result without repeating the operation. Request IDs are not evicted within a session; the 10,000-command limit fails closed.

Pass the preview's hash unchanged as `Args.ExpectedInventoryHash`, with `Args.ConfirmReplacement = true`. The hash binds both the current inventory and the incoming kit/target/FIR/count. Changing a kit or its options requires a new preview. These endpoints remain available for API callers. Workbench kit previews run locally. Workbench spawning and replacement create the items locally, then use `/debugplus/menu/sync` to save their exact IDs and placements before ordinary menu inventory operations resume. The synchronization endpoint validates native templates, item trees, equipment restrictions, stash occupancy and displaced item IDs; it stages and saves the batch under the same profile gate and request ledger. It does not return items to apply again or request a backend reload.

## Inventory and recovery behavior

The server constructs fresh native item identities, validates complete parent trees, template/slot filters, ammunition capacity, grid bounds/overlap, equipment conflicts, and item state. It stages all changes on a cloned PMC inventory. Native SPT helpers preflight and place stash items and remove replaced equipment. Only included equipment slots are replaced; protected secure-container, pocket, and special slots are rejected. Native inventory events and raid start/end are serialized with menu transactions for each authenticated profile. Raid transit remains in-raid; failed start/end and disconnect paths fail closed.

A flushed recovery journal is written before the staged inventory is published and saved through SPT. A save failure restores the original inventory and retries its save after invalidating SPT 4.1.5's premature save hash cache. If recovery also fails, the result is `Uncertain`, and further inventory/raid changes are blocked for that server lifetime. The journal contains the original inventory and insurance information under `recovery/<account-profile-id>.json` beside the server assembly.

Do not delete a recovery file to bypass an unresolved result. Preserve it, resolve the underlying storage failure, and inspect the saved profile before restarting the server. On the next command, an exactly matching before/after inventory is recognized without replaying any items; a divergent state remains blocked for manual reconciliation. Interrupted raids remain blocked until a successful native raid end or a user-controlled server restart. No client button overrides the raid guard.

## Validation

From the repository root:

```powershell
dotnet build DebugPlus.Server/DebugPlus.Server.csproj --configuration Release -p:DeployDebugPlus=false
dotnet run --project DebugPlus.Server/Tests/DebugPlus.Server.Tests.csproj --configuration Release -- 'F:/SPT 4.1.x/SPT_Runtime'
```

The focused runner reads shipped database templates and constructs an isolated fixture. It never reads or writes live player profiles or launches the server. It verifies authorization, expired sessions, remote-target rejection, raid exclusion, native item/slot validation, remapped IDs, stale previews, split stacks, full stash, replacement rollback, duplicate requests, unresolved recovery, allowlist revocation, and installed native Harmony signatures. Profile storage success/failure is simulated for the fault-injection checks; live save, menu refresh, Fika listen-host/headless, reconnect, transit, and multiplayer visibility acceptance remain user-controlled.
