# DebugPlus

**An in-game debugging workbench for SPT. Spawn equipment, build test loadouts, control raid conditions, and inspect what the game is doing—all from one interface.**

DebugPlus brings player, inventory, AI, world, and logging tools together for mod developers, server administrators, and players who want to experiment. Reproduce an injury, test a weapon, revisit a location, or set up a bot encounter without rebuilding your setup by hand each time.

Press **F10** to open the workbench. Browse items visually, select players and bots by name, save favorite tools, and review command results in the activity log. The window's position, size, scale, and opening key are customizable.

## Player and health tools

Set up the conditions you need for testing:

- Enable god mode, unlimited body and arm stamina, or unlimited oxygen.
- Hold energy and hydration steady, or refill both immediately.
- Repair gear and clear the held weapon's malfunction.
- Inspect individual body parts, set health, apply damage, heal, restore limbs, or perform a full recovery.
- Add or remove light bleeding, heavy bleeding, fractures, pain, tremor, and exhaustion, with duration and intensity controls where supported by the game.

Player tools act on the selected target. Gameplay overrides reset between raids, and deliberate debug health commands can still apply while god mode is enabled.

## Item browser and spawning

Find equipment through a visual catalog with item icons, localized names, category filters, and favorites. Search by name or template ID, then choose quantity, condition, and found-in-raid status.

- **At the main menu:** add items directly to your PMC stash.
- **In a raid:** add items to the selected player's inventory or place them on the ground.
- Copy template IDs from the item's technical details when you need them for mod development.

Spawning respects native stack limits, inventory space, and item restrictions. Main-menu additions update the existing inventory interface without requiring a backend reload. Items default to **not found in raid** and follow normal saving and extraction rules.

## Saved kits and loadouts

Capture equipment as a reusable kit, import native weapon or equipment builds, and share DebugPlus kits through JSON import and export. Kits preserve attachments, ammunition, and nested contents.

Choose which equipment slots to include, then spawn the kit into a stash, inventory, or onto the ground. To equip it directly, preview the replacement before confirming. Empty saved kits can also remove worn equipment after a preview.

**Equipment replacement deletes displaced gear and its contents.** The preview shows what will be removed. Pockets, secure containers, special slots, and your personal dogtag are excluded from kit operations.

## Teleportation and saved locations

Move around a map using the method that suits your test:

- Teleport to an aimed surface or enter coordinates and facing direction.
- Jump to a map spawn point or another player, or bring a selected player to you.
- Return to the previous debug teleport position.
- Save named bookmarks for each map and revisit them later.

Standard placement checks for ground and player clearance. An optional exact-placement mode skips those checks for authored coordinates.

## Bots, doors, time, and weather

Create encounters by choosing a bot role, difficulty, count, and available spawn area. Inspect active bots and pause, resume, heal, kill, or despawn an individual bot or the current bot population.

Select an aimed or nearby door to open, close, lock, unlock, or breach it where the door supports that action. Copy its identifier for development work.

Set the time of day and optionally freeze daylight. Adjust clouds, rain, fog, wind, and thunder individually, or start from Clear, Overcast, and Storm presets. Restore normal time and weather controls when finished. Availability depends on the map's supported systems.

## Diagnostics and live logging

Inspect scene and player information, review command history, and export diagnostic reports. Existing weapon-debug controls and door/spawn-point overlays provide additional information during testing.

The dedicated **Logging** tab lets you:

- Capture Unity information, warnings, errors, exceptions, and assertions.
- Select native EFT logging categories and their minimum severity.
- Enable available runtime AI diagnostic messages.
- Search and filter a live viewer, inspect message details, and pause the display while capture continues.
- Export filtered entries as text or JSON.

Capture selections persist across restarts. Captured messages also write to a rotating log at `BepInEx/LogOutput/DebugPlus/client.log`; filtered exports are saved under `BepInEx/config/DebugPlus/exports`.

Logging works in menus and raids without administrator access. Capture covers Unity and native EFT messages; unavailable or compiled-out logging cannot be restored. Enable detailed categories as needed, since verbose logging can affect performance.

## Requirements and installation

DebugPlus **2.0.0** targets:

| Component | Version |
| --- | --- |
| SPT | 4.1.5 |
| EFT client build | 40743 |
| Optional Fika integration | Fika Core 2.4.3, with the matching DebugPlus Fika companion |

1. Close the game and server before installing.
2. Extract **DebugPlus-2.0.0-Release.zip** into your SPT game folder, merging both `BepInEx` and `SPT_Runtime`.
3. If upgrading from DebugPlus 1.x, remove the old `BepInEx/plugins/DebugPlus.dll` to avoid loading two copies.
4. Start the server and game, then press **F10** to open DebugPlus.
5. Configure administrator access below to enable gameplay and inventory changes.

The main download includes both the client workbench and its required server component.

### Administrator access

On its first start, the server component creates `SPT_Runtime/user/mods/DebugPlus/config.json`. No profiles have administrator access by default.

Open **Settings → Server access → Technical details for server setup**, copy your profile ID, and add it to the server configuration:

```json
{
  "AdminProfileIds": [
    "YOUR_PMC_PROFILE_ID"
  ]
}
```

Use the **PMC character profile ID**, not the account or session ID. Save the file and select **Refresh permissions** in DebugPlus. This setup is required for solo play too.

Without administrator access, you can still browse items, manage local kit files, and use logging. Gameplay changes and persistent inventory operations require authorization.

### Fika and headless hosts

For Fika sessions, also install **DebugPlus.Fika-2.0.0-Release.zip** on every participant and the host. Keep all DebugPlus components on matching versions, and install the server component on the SPT server serving the session.

A headless host needs `DebugPlus.dll`, `DebugPlus.Common.dll`, and `DebugPlus.Fika.dll` in `BepInEx/plugins/DebugPlus`; it does not need the workbench UI bundle.

The server's administrator list controls access, and the raid host coordinates multiplayer commands. Missing companions, incompatible versions, or interrupted synchronization disable affected tools.

## Using the workbench

Unavailable controls show why an action cannot run, such as needing an active raid, a live target, administrator access, or a supported map feature. Availability updates as the session changes.

Settings, favorites, kits, and bookmarks persist between sessions. Temporary gameplay overrides reset between raids, while spawned items and confirmed equipment changes follow normal game persistence.

**Opening DebugPlus does not pause the raid or automatically enable god mode.**
