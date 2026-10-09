using System;
using System.Linq;
using BepInEx.Configuration;
using DebugPlus.Config;
using DebugPlus.Logging;
using UnityEngine;

namespace DebugPlus.UI;

public sealed partial class WorkbenchWindow
{
    private void LoggingPage()
    {
        var service = ClientLogging.Instance;
        if (!service) { Notice(_content, "Logging service unavailable."); return; }
        LoggingOptionView Option(string label, ConfigEntry<bool> entry) => new() { Name = label, Hint = entry.Description.Description, Read = () => entry.Value, Write = value => entry.Value = value };
        long version = -1;
        LoggingEntryView[] cached = Array.Empty<LoggingEntryView>();
        var view = new LoggingView(new[] {
            Option("Unity info", DebugPlusConfig.UnityInfoLogging), Option("Warnings", DebugPlusConfig.UnityWarningLogging),
            Option("Errors", DebugPlusConfig.UnityErrorLogging), Option("Exceptions", DebugPlusConfig.UnityExceptionLogging),
            Option("Assertions", service.Assertions), Option("Enable native gate", service.NativeGate), Option("AI diagnostics", service.AiGate)
        }, () => service.Sources.Select(source => new LoggingSourceView { Name = source.Name, Read = () => source.Enabled.Value,
            Write = value => source.Enabled.Value = value, ReadLevel = () => source.Minimum.Value.ToString(),
            WriteLevel = value => { if (Enum.TryParse<CaptureLevel>(value, out var level)) source.Minimum.Value = level; } }).ToArray(),
        () =>
        {
            if (version == service.Buffer.Version) return cached;
            version = service.Buffer.Version;
            return cached = service.Buffer.Snapshot().Select(entry => new LoggingEntryView { Time = entry.Time.ToLocalTime().ToString("HH:mm:ss.fff"),
                Source = entry.Source, Level = entry.Level.ToString(), Message = entry.Message, Detail = entry.Detail, Text = entry.Text, Entry = entry }).ToArray();
        }, () => service.NativeStatus + " · " + service.AiStatus + "\n" + service.HookStatus + " · " + service.FileStatus +
            " · Evicted: " + service.Buffer.Dropped + " · Truncated: " + service.Buffer.Truncated + " · File dropped: " + service.FileDropped + "\n" + service.OutputPath,
        service.RestoreDefaults, service.Buffer.Clear, text => GUIUtility.systemCopyBuffer = text,
        (entries, json) => SafeReport(() => service.Export(entries.Select(e => (CapturedLog)e.Entry), json)));
        _content.Add(view);
        view.FitViewport(_content);
        _refreshers.Add(view.Refresh); // Existing workbench cadence is 2 Hz, below the 4 Hz ceiling.
    }
}
