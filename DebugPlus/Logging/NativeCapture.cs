#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;
using NLog;
using NLog.Config;
using NLog.Targets;

namespace DebugPlus.Logging;

internal sealed class NativeCapture : IDisposable
{
    private readonly Action<CapturedLog> _receive;
    private readonly List<LoggingRule> _owned = new();
    private LoggingConfiguration _configuration;
    private CaptureTarget _target;
    public NativeCapture(Action<CapturedLog> receive) { _receive = receive; }
    public void Apply(LoggingConfiguration configuration, IEnumerable<KeyValuePair<string, CaptureLevel>> selections)
    {
        Remove();
        if (configuration == null) return;
        var selected = selections.ToArray();
        if (selected.Length == 0) { LogManager.ReconfigExistingLoggers(); return; }
        _configuration = configuration;
        _target = new CaptureTarget(_receive) { Name = "DebugPlus.Capture." + Guid.NewGuid().ToString("N") };
        configuration.AddTarget(_target);
        var index = 0;
        foreach (var selection in selected)
        {
            var rule = new LoggingRule(selection.Key, LogLevel.FromOrdinal((int)selection.Value), LogLevel.Fatal, _target) { Final = false };
            configuration.LoggingRules.Insert(index++, rule); _owned.Add(rule);
        }
        LogManager.ReconfigExistingLoggers();
    }
    private void Remove()
    {
        if (_configuration == null) return;
        foreach (var rule in _owned) _configuration.LoggingRules.Remove(rule);
        _owned.Clear();
        _configuration.RemoveTarget(_target.Name); _target.Dispose();
        _configuration = null; _target = null;
    }
    public void Dispose() { Remove(); LogManager.ReconfigExistingLoggers(); }
    private sealed class CaptureTarget : NLog.Targets.Target
    {
        private readonly Action<CapturedLog> _receive;
        public CaptureTarget(Action<CapturedLog> receive) { _receive = receive; }
        protected override void Write(LogEventInfo entry)
        {
            if (CaptureOrigin.InSink || CaptureOrigin.UnityDepth > 0) return;
            try
            {
                CaptureOrigin.InSink = true;
                _receive(new CapturedLog(entry.LoggerName, (CaptureLevel)entry.Level.Ordinal,
                    entry.FormattedMessage, entry.Exception == null ? "" : CaptureOrigin.SafeString(entry.Exception)));
            }
            catch { /* Logging must never affect the caller. */ }
            finally { CaptureOrigin.InSink = false; }
        }
    }
}
