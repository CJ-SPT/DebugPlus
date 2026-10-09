using System;
using System.Collections.Generic;
using DebugPlus.Common;
using UnityEngine;

namespace DebugPlus.Runtime;

public sealed class TargetInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Position { get; set; } = "";
    public bool IsBot { get; set; }
}
public sealed class ItemInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public Sprite Icon { get; set; }
}
public sealed class ActivityEntry
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string Message { get; set; } = "";
    public CommandStatus Status { get; set; }
}
public sealed class WorkbenchPreferences
{
    public float UiX { get; set; } = 80;
    public float UiY { get; set; } = 60;
    public float UiWidth { get; set; } = 1280;
    public float UiHeight { get; set; } = 780;
    public float UiScale { get; set; } = 1;
    public string ActivePage { get; set; } = "Overview";
    public string ToggleKey { get; set; } = "F10";
    public List<string> FavoriteItems { get; set; } = new();
    public List<string> FavoriteCommands { get; set; } = new();
}
