namespace DebugPlus.Common;

// Read-only presentation policy. Execution still validates authority and native state.
public sealed class AvailabilityContext
{
    public bool ProfileReady { get; set; }
    public bool InRaid { get; set; }
    public bool InWorld { get; set; }
    public bool Authorized { get; set; }
    public bool IsCoop { get; set; }
    public bool CoopReady { get; set; }
    public bool TargetAlive { get; set; }
    public bool OwnTarget { get; set; }
}

public static class WorkbenchAvailability
{
    public const string EmptyKitPreview = "No equipment is included. Nothing will be spawned, replaced or deleted. Choose a kit with equipment or capture your current gear.";
    public static string KitPreviewPrompt(bool hasItems, bool clearEquipment = false) => clearEquipment
        ? "Preview the worn equipment this empty kit will remove. Protected personal slots are kept."
        : hasItems ? "Preview required before replacement." : EmptyKitPreview;
    // Spawning needs a selection; preview can also explain an empty selection.
    // Authority and native inventory readiness are checked when the action runs,
    // so a stale presentation context cannot silently disable every saved kit.
    public static string KitAction(CommandKind kind, CommandArguments args, bool busy)
    {
        if (busy) return "Wait for the current command to finish.";
        if (kind == CommandKind.PreviewKit) return "";
        if (args?.Items == null || (args.Items.Count == 0 && !(kind == CommandKind.EquipKit && args.ClearEquipment))) return "No equipment is included. Choose a kit with equipment or capture your current gear.";
        if (kind == CommandKind.EquipKit && string.IsNullOrWhiteSpace(args.ExpectedInventoryHash)) return "Preview the replacement before equipping this kit.";
        return "";
    }

    public static string Raid(AvailabilityContext state, bool player = false)
    {
        if (!state.InRaid) return "Enter an active raid to use this tool.";
        if (!state.Authorized) return "Administrator access is required.";
        if (state.IsCoop && !state.CoopReady) return "Waiting for compatible multiplayer companions and synchronization.";
        if (player && !state.TargetAlive) return "Select a live player in this raid.";
        return "";
    }

    public static string Inventory(AvailabilityContext state)
    {
        if (state.InRaid) return Raid(state, true);
        if (state.InWorld) return "Return to the main menu to change stash inventory.";
        if (!state.ProfileReady) return "Wait for your PMC profile to load.";
        if (!state.Authorized) return "Administrator access is required.";
        if (!state.OwnTarget) return "Menu inventory tools apply to your own PMC.";
        return "";
    }

    public static string Menu(AvailabilityContext state)
    {
        if (state.InWorld || state.InRaid) return "Return to the main menu to use this action.";
        if (!state.ProfileReady) return "Wait for your PMC profile to load.";
        if (!state.Authorized) return "Administrator access is required.";
        return "";
    }

    public static string Page(string page, AvailabilityContext state) => page switch
    {
        "Player" or "Health" or "Teleport" => Raid(state, true),
        "Bots" or "World" => Raid(state),
        "Items" => state.ProfileReady || state.InRaid ? "" : "Wait for the item catalog and your profile to load.",
        // Kit files, diagnostics, and preferences are useful even without mutation permission.
        _ => ""
    };
}
