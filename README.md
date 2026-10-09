# DebugPlus 2.0 — Debugging Workbench

An original UI Toolkit workbench for EFT build 40743 / SPT 4.1.5, with an optional Fika 2.4.3 companion and headless-host support.

Press **F10** to open it. The dark graphite interface uses unnumbered top tabs, a visual item grid, named player/bot choices, nearby-door selection, equipment cards, favorites, and an activity log. Search by item name, select a card, then add it to your inventory or stash. Technical IDs are tucked into optional details; ordinary actions do not require them. Change the key, scale, and layout in Settings. Opening the workbench captures input but does not pause the raid or enable protection.

## Tools

- Player: god mode, stamina/oxygen, energy/hydration, equipment repair, weapon malfunction clearing.
- Health: body-part health, damage/healing, limb restoration, recovery, and six supported native health effects.
- Items: localized search, template IDs, categories, icons, favorites, quantity, condition and found-in-raid state; inventory, ground, and main-menu stash spawning.
- Kits: native builds and captured loadouts, nested contents/ammunition, import/export, included-slot selection, and replacement previews. **Replace and delete removes displaced equipment and its contents.**
- Teleport: coordinates/yaw, aimed surface, map spawn points, other players, previous position, and map-specific bookmarks.
- Bots: role/difficulty/count and spawn zone, inspection, pause/resume, heal, kill, and despawn.
- World: door interactions and identifiers, daylight/time, weather controls, restore normal environment.
- Diagnostics: native weapon tools, door/spawn overlays, aimed-object details, errors, recent commands and diagnostic exports.
- Logging: saved Unity severity controls (including assertions), native EFT categories and severity thresholds, native/AI logging gates, a searchable live viewer, and text/JSON export. This tab works in menus and raids without administrator access.

Gameplay overrides reset at raid boundaries. Spawned items follow ordinary save/extraction rules and default to not found in raid. Native template, slot, stack and grid restrictions still apply. Saved settings/kits/bookmarks live under `BepInEx/config/DebugPlus`.

### Revealing client logs

Open **Logging → Capture controls**. Unity info, warning, error, and exception toggles retain their existing config keys and BepInEx forwarding behavior. Assertions are optional. For native EFT logs, enable a named category and choose its minimum severity; if the native gate is disabled, enable **Enable native gate**. **AI diagnostics** enables runtime AI messages; select Unity info to capture those messages. F12 and workbench settings share the same BepInEx config entries and persist across restarts. New sources are disabled by default.

Native categories cover AI, animation, inventory, backend requests, assets, audio, spawning, networking, health, quests, and other installed-client systems. The client’s no-op audio debug factory and editor-only AI file logger are identified as unavailable. This does not disable ordinary native audio or AI categories. Compiled-out log calls cannot be recovered. BepInEx, SPT, and Fika logger controls are outside this feature.

Additional native capture uses separate in-memory NLog rules and preserves the game’s existing rules and configuration file. Turning a category off stops DebugPlus capture, not ordinary game logging. **Restore defaults** disables additional capture and restores gates changed by DebugPlus. Native log initialization and configuration reloads reapply saved selections. Logging is local to each process; headless instances use their own saved config.

The viewer retains at most 5,000 entries / 16 MiB, with individual entries capped at 64 KiB. Eviction, truncation, and file-drop counters are visible. **Pause display** freezes the view while capture continues. Export writes the currently filtered retained entries to `BepInEx/config/DebugPlus/exports`. Captured messages also write asynchronously to `BepInEx/LogOutput/DebugPlus/client.log`, rotating at 10 MiB with five archives. Disk errors disable file output and appear in the UI while memory capture continues. Only enable trace-heavy categories when needed; logging can affect performance.

### Item browser and stash updates

The item grid grows with the workbench viewport and highlights individual cards without a full-row hover background. Item and kit spawning creates items on the client. The exact item IDs, nested contents, condition, ammunition and positions are automatically saved before normal inventory actions resume, so moves and ammo splits recognize the new items. The existing inventory UI updates in place; no backend reload or manual reconciliation is required. Kit previews validate a copy of the current local equipment, and confirmed replacements use the same synchronization flow. Quantities split at native stack limits, and rejected batches roll back. Interrupted connections retry the same request without spawning duplicate items.

Install matching protocol-2 client, server and Fika companion components, then restart the game and server yourself to load them. Offline checks exercise native SPT split/move operations on synchronized client IDs, complete kit replacement, save rollback, placement and request deduplication; live game acceptance remains separate.

Kit capture preserves each root's native equipment slot; spawn synchronization preserves its parent, grid position and rotation. Older captures with missing slots are repaired from their original equipped items or compatible native slots when the kit library opens. The personal dogtag is excluded from kit operations alongside pockets, secure containers and special slots. A regression fixture executes EFT's installed tree serializer and the production address adapter to cover the native root-address omission.

Unavailable tabs and controls are disabled with explanatory tooltips. Raid-only tabs require an active raid, administrator access, compatible multiplayer companions, and a live target where needed. Item browsing and local kit management remain available without mutation permission. Controls update when targets, selections, map capabilities, or connection state change; inaccessible open pages return to Overview. Final command validation still runs before execution.

Forms use aligned label columns and compact inputs. Related fields wrap into rows as space permits. Scrollbars use visible dark tracks and contrasting thumbs; mouse-wheel scrolling moves three times the native default step, including item lists, logging, and dropdown menus. Dropdowns use the workbench theme and bounded menu height. Changing tabs returns to the top, and closing the workbench also dismisses any open dropdown.

## Installation and access

Extract `release/DebugPlus-2.0.0-Release.zip` into the SPT game root, merging both `BepInEx` and `SPT_Runtime`. When upgrading from 1.x, remove the old `BepInEx/plugins/DebugPlus.dll` so BepInEx does not discover two copies.

The archive contains:

```text
BepInEx/plugins/DebugPlus/
  DebugPlus.dll
  DebugPlus.Common.dll
  debugplus.workbench
SPT_Runtime/user/mods/DebugPlus/
  DebugPlus.Server.dll
  DebugPlus.Common.dll
```

For Fika, also extract `release/DebugPlus.Fika-2.0.0-Release.zip` into the game root on every participant and host. This optional addon contains only `BepInEx/plugins/DebugPlus/DebugPlus.Fika.dll` and requires the matching main release. Both archives contain runtime files only; documentation and build manifests are excluded.

Release builds of `DebugPlus.sln` build all components and refresh both versioned ZIPs in the solution-root `release/` folder through the `DebugPlus.Release` packaging project. Completed archives replace the previous files atomically. If another process locks a release ZIP, the build preserves it, writes the new ZIP with a timestamp in the same folder, and reports that path with a warning. The old `DebugPlus/release/` folder is no longer used. Debug builds do not package. Set `-p:PackageDebugPlus=false` for a compile-only Release build.

The server component builds independently of an SPT installation using `SPTarkov.Server.Core` and `SPTarkov.Reflection` 4.1.0 from NuGet; Reflection supplies HarmonyX 2.16.1 transitively. Its metadata accepts SPT 4.1.x. Client and Fika builds still require the matching local game assemblies, and offline server checks use shipped database files as fixtures.

Builds never install implicitly. `tools/Build.ps1` exposes separate Build, Check, Package and Install actions; Build and Check do not package. Package and Install produce the same two root-level release ZIPs, with timestamped staging copies and separate hash manifests under `artifacts/packages/`. Install includes the companion only when Fika is present. Version numbers come from `DebugPlus/VERSION.txt`; packaging rejects mismatched assemblies and stale UI assets. The UI bundle must first be built with `tools/Build-Assets.ps1` using Unity 2022.3.43f1 and an isolated CJ-SDK asset folder. No existing project's UI assets or design are used.

```powershell
./tools/Build.ps1 -Action Check
./tools/Build-Assets.ps1 -Preview
./tools/Build.ps1 -Action Package
./tools/Build.ps1 -Action Install -HeadlessRoot 'F:/SPT 4.1.x - Headless'
```

Install the client/Common/bundle into `BepInEx/plugins/DebugPlus`. Include the Fika companion on every Fika participant and host; headless needs the three assemblies but no UI bundle. Install Server/Common into `SPT_Runtime/user/mods/DebugPlus`. The installer backs up replaced files, removes the old root-level DebugPlus DLL from plugin discovery, preserves server policy, and verifies deployed hashes. It never starts or stops applications.

On the next server start, `SPT_Runtime/user/mods/DebugPlus/config.json` defaults to:

```json
{ "AdminProfileIds": [] }
```

Add authorized **PMC profile IDs**, available from the workbench Settings page. These are character IDs, not account/session IDs. The same server policy controls menu and raid commands. Unauthorized profiles can browse the UI but cannot mutate the game. Missing or mismatched server/companion components disable affected actions. Changes made to your own persistent inventory at the main menu require administrator access.

See [server operations and recovery](DebugPlus.Server/README.md) and [Fika synchronization](DebugPlus.Fika/README.md). An uncertain inventory result locks further inventory debugging for that raid; do not replay uncertain spawn commands under new IDs.

## Validation and acceptance

Focused offline checks cover native backend templates and inventory placement, authorization, stale previews, ID remapping, duplicate requests, packet identity, save rollback and recovery. Native UI Toolkit fixture renders cover 1920×1080, 2560×1440 and 3440×1440. These checks do not establish live gameplay acceptance.

The UI fixtures cover all eleven tabs, with normal, narrow/scaled, and expanded window variants, plus minimum-height logging and expanded activity/sections. Checks dispatch native wheel events, verify scrollbar thumb bounds, aligned input edges, reachable actions, logging scroll retention and details, item filtering/selection/recycling, and a real open dropdown's theme, height, scrolling, and keyboard selection. Fixture data exercises layout and interaction without running game commands; live game input, target changes, and gameplay acceptance remain user-controlled.

Logging checks additionally cover real BepInEx persistence, NLog severity/final-rule behavior and reattachment, gate restoration, repeated/concurrent messages, formatting failures, bounded storage, rotation, and disk errors. Live acceptance remains user-controlled: reveal a suppressed Unity message and native category, change levels, verify file output and restart persistence, restore defaults across two raids, and add to an open stash without a session reload.

User-controlled acceptance: open/close the UI and type while native menus are visible; run two consecutive raids; test each health/effect toggle, blocked teleports, full inventory, nested kits and replacement failure; extract and verify saved gear; spawn/equip from the main menu. Repeat with listen-host and headless Fika, an administrator and an ordinary peer, including remote targets, disconnects, reconnects, transit, missing companions, ground loot and synchronized doors/weather. Verify other clients' views before relying on a command result.

Deferred: free camera, noclip, progression/quest editing, disposable-profile rollback, scenario recording/replay.
# Kit action availability

Spawn accepts any nonempty included equipment selection. An empty saved kit is an empty loadout: Preview removing worn equipment lists the items and nested contents that confirmation will delete. Remove and delete worn equipment requires that preview and preserves pockets, secure containers, special slots and the personal dogtag. Unchecking every slot of a nonempty kit does not request equipment clearing. Preview results and errors are shown in both the kit panel and the feedback bar. The native regression harness exercises client availability, native removal preflight and serialization; server checks cover removal-only synchronization, protected slots, stale previews, retries and save rollback. Protocol 3 is required on client, server and Fika companions.
