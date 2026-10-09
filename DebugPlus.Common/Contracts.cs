using System;
using System.Collections.Generic;

namespace DebugPlus.Common;

public static class Protocol
{
    public const int Version = 3;
    public const string ModVersion = "2.0.0";
    public const int MaxItems = 2048;
    public const int MaxCount = 100;
    public const int MaxPayloadBytes = 2 * 1024 * 1024;
}

public enum CommandKind
{
    GodMode, Stamina, Oxygen, Energy, Hydration, RefillVitals, RepairGear, ClearMalfunction,
    SetHealth, Heal, Damage, RestoreLimb, Recover, AddEffect, RemoveEffect, RemoveAllEffects,
    SpawnItem, SpawnKit, EquipKit, Teleport, TeleportBack,
    SpawnBots, PauseBots, ResumeBots, HealBots, KillBots, DespawnBots,
    Door, SetTime, SetWeather, RestoreEnvironment, StartRaid, ReloadBackend, PreviewKit
}
public enum CommandStatus { Pending, Completed, Rejected, Uncertain }
public enum CommandScope { Player, World, Menu }

public class CommandRequest
{
    public int Protocol { get; set; } = Common.Protocol.Version;
    public string RequestId { get; set; } = Guid.NewGuid().ToString("N");
    public string SessionId { get; set; } = "";
    public string ActorId { get; set; } = "";
    public string TargetId { get; set; } = "";
    public CommandKind Kind { get; set; }
    public CommandArguments Args { get; set; } = new();
}

public sealed class CommandArguments
{
    public bool Enabled { get; set; }
    public bool Exact { get; set; }
    public bool FoundInRaid { get; set; }
    public bool ConfirmReplacement { get; set; }
    public bool ClearEquipment { get; set; }
    public string TemplateId { get; set; } = "";
    public string BodyPart { get; set; } = "Chest";
    public string Effect { get; set; } = "LightBleeding";
    public string Destination { get; set; } = "Inventory";
    public string ObjectId { get; set; } = "";
    public string Action { get; set; } = "";
    public string Role { get; set; } = "assault";
    public string Difficulty { get; set; } = "normal";
    public string MapId { get; set; } = "";
    public string ExpectedInventoryHash { get; set; } = "";
    public int Count { get; set; } = 1;
    public float Value { get; set; } = 100;
    public float Duration { get; set; } = 60;
    public float Strength { get; set; } = 1;
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Yaw { get; set; }
    public WeatherValues Weather { get; set; } = new();
    public List<DebugItemRecord> Items { get; set; } = new();
}

public sealed class CommandResult
{
    public string RequestId { get; set; } = "";
    public CommandStatus Status { get; set; }
    public string Message { get; set; } = "";
    public string DataJson { get; set; } = "";
    public static CommandResult Reject(CommandRequest request, string message) => new() { RequestId = request.RequestId, Status = CommandStatus.Rejected, Message = message };
    public static CommandResult Complete(CommandRequest request, string message, string data = "") => new() { RequestId = request.RequestId, Status = CommandStatus.Completed, Message = message, DataJson = data };
}

public sealed class AccessPolicy
{
    public string SessionId { get; set; } = "";
    public int Protocol { get; set; } = Common.Protocol.Version;
    public string Version { get; set; } = Common.Protocol.ModVersion;
    public string ProfileId { get; set; } = "";
    public string[] AdminProfileIds { get; set; } = Array.Empty<string>();
    public bool Authorized { get; set; }
    public string Message { get; set; } = "";
}

public sealed class InventoryPreview
{
    public string Hash { get; set; } = "";
    public string Summary { get; set; } = "";
}

// A client-created batch, including its native IDs and chosen destinations.
public class MenuInventorySync : CommandRequest
{
    public List<DebugItemRecord> Added { get; set; } = new();
    public List<string> RemovedIds { get; set; } = new();
}

public sealed class DebugItemRecord
{
    public string Id { get; set; } = "";
    public string TemplateId { get; set; } = "";
    public string ParentId { get; set; } = "";
    public string SlotId { get; set; } = "";
    public string LocationJson { get; set; } = "";
    public string StateJson { get; set; } = "";
}
public sealed class KitDocument
{
    public int Schema { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New kit";
    public List<DebugItemRecord> Items { get; set; } = new();
}
public sealed class TeleportBookmark
{
    public string Name { get; set; } = "";
    public string MapId { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public float Yaw { get; set; }
}
public sealed class WeatherValues
{
    public float Clouds { get; set; }
    public float Rain { get; set; }
    public float Fog { get; set; }
    public float Wind { get; set; } = .1f;
    public float Thunder { get; set; }
}

public static class Commands
{
    public static CommandScope Scope(CommandKind kind) => kind switch
    {
        CommandKind.StartRaid or CommandKind.ReloadBackend => CommandScope.Menu,
        CommandKind.SpawnBots or CommandKind.PauseBots or CommandKind.ResumeBots or CommandKind.HealBots or CommandKind.KillBots or CommandKind.DespawnBots or CommandKind.Door or CommandKind.SetTime or CommandKind.SetWeather or CommandKind.RestoreEnvironment => CommandScope.World,
        _ => CommandScope.Player
    };
    public static bool IsInventory(CommandKind kind) => kind is CommandKind.SpawnItem or CommandKind.SpawnKit or CommandKind.EquipKit;
    public static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    public static string Validate(CommandRequest request)
    {
        if (request.Protocol != Protocol.Version) return "DebugPlus protocol versions do not match.";
        if (!Guid.TryParseExact(request.RequestId, "N", out _)) return "Invalid request ID.";
        if (!Enum.IsDefined(typeof(CommandKind), request.Kind) || request.Args == null) return "Unknown command.";
        if (string.IsNullOrWhiteSpace(request.SessionId) || string.IsNullOrWhiteSpace(request.ActorId)) return "Session is not ready.";
        var a = request.Args;
        if (request.ActorId.Length > 128 || request.TargetId == null || request.TargetId.Length > 128 || request.SessionId.Length > 128) return "Invalid actor, target, or session identity.";
        foreach (var value in new[] { a.TemplateId, a.BodyPart, a.Effect, a.Destination, a.ObjectId, a.Action, a.Role, a.Difficulty, a.MapId, a.ExpectedInventoryHash })
            if (value == null || value.Length > 512) return "Invalid command text value.";
        foreach (var n in new[] { a.Value, a.Duration, a.Strength, a.X, a.Y, a.Z, a.Yaw })
            if (!Finite(n)) return "Values must be finite numbers.";
        if (a.Count < 1 || a.Count > Protocol.MaxCount) return $"Quantity must be 1–{Protocol.MaxCount}.";
        if (a.Duration < 0 || a.Duration > 86400 || a.Strength < 0 || a.Strength > 1000) return "Effect duration or strength is out of range.";
        if (a.Items == null || a.Items.Count > Protocol.MaxItems) return "Item tree is too large.";
        if (a.ClearEquipment && (request.Kind is not (CommandKind.PreviewKit or CommandKind.EquipKit) || a.Items.Count != 0)) return "Clearing equipment requires an empty equipment kit.";
        if (request.Kind == CommandKind.EquipKit && !a.ConfirmReplacement) return "Preview the replacement and choose Replace and delete.";
        return "";
    }
}
