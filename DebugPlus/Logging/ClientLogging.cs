using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using DebugPlus.Config;
using Newtonsoft.Json;
using NLog;
using UnityEngine;

namespace DebugPlus.Logging;

public sealed class ClientLogging : MonoBehaviour
{
    public static ClientLogging Instance { get; private set; }
    private readonly Dictionary<string, LogSource> _sources = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _discovered = new();
    private readonly ConcurrentDictionary<string, byte> _known = new(StringComparer.Ordinal);
    private NativeCapture _native;
    private RotatingLogWriter _writer;
    private LoggingPatches _patches;
    private ConfigFile _config;
    private int _dirty = 1;
    private float _nextPoll;
    private readonly GateOverride _nativeGate = new();
    private readonly GateOverride _aiGate = new();
    private LoggingSettings _aiSettings;
    public LogBuffer Buffer { get; } = new();
    public ConfigEntry<bool> Assertions { get; private set; }
    public ConfigEntry<bool> NativeGate { get; private set; }
    public ConfigEntry<bool> AiGate { get; private set; }
    public IReadOnlyList<LogSource> Sources => _sources.Values.OrderBy(s => s.Name, StringComparer.Ordinal).ToArray();
    public string OutputPath => _writer?.Path ?? "";
    public string FileStatus => _writer?.Error ?? "File output ready";
    public long FileDropped => _writer?.Dropped ?? 0;
    public string NativeStatus => "Native logging gate: " + (AbstractLogger.IsLogsEnabled ? "enabled" : "disabled") +
        (LogManager.Configuration == null ? " · Waiting for native logging configuration" : "");
    public string AiStatus => _aiSettings ? "AI diagnostic gate: " + (_aiSettings.AILogging ? "enabled" : "disabled") : "AI settings not available yet";
    public string HookStatus { get; internal set; } = "";
    private void Awake()
    {
        Instance = this; _config = Plugin.Instance.Config;
        Assertions = _config.Bind("Logging", "Enable Assertion Logging", false, "Capture Unity assertions and forward them as warnings.");
        NativeGate = _config.Bind("Logging", "Enable native logging gate", false, "Override EFT's global log gate. Category capture still requires selecting a category below.");
        AiGate = _config.Bind("Logging", "Enable AI diagnostics", false, "Enable runtime AI diagnostic messages. Enable Unity info capture to see them. Editor-only AI file logging is unavailable.");
        foreach (var name in NativeCategories.Split('|')) { _known.TryAdd(name, 0); AddSource(name); }
        _writer = new RotatingLogWriter(Path.Combine(BepInEx.Paths.BepInExRootPath, "LogOutput", "DebugPlus", "client.log"));
        _native = new NativeCapture(Receive);
        _patches = new LoggingPatches(); _patches.Enable();
        Application.logMessageReceivedThreaded += EngineLog;
        _config.SettingChanged += Changed;
        LogManager.ConfigurationChanged += ConfigurationChanged;
        LogManager.ConfigurationReloaded += ConfigurationReloaded;
    }
    internal static void Discover(string name)
    {
        if (!string.IsNullOrEmpty(name) && name.Length <= 256 && name.IndexOfAny(new[] { '*', '?' }) < 0)
        {
            var instance = Instance;
            if (!ReferenceEquals(instance, null) && instance._known.Count < 512 && instance._known.TryAdd(name, 0)) instance._discovered.Enqueue(name);
        }
    }
    private void AddSource(string name)
    { if (!_sources.ContainsKey(name)) { _sources.Add(name, new LogSource(name, _config)); Interlocked.Exchange(ref _dirty, 1); } }
    private void Changed(object sender, SettingChangedEventArgs args) => Interlocked.Exchange(ref _dirty, 1);
    private void ConfigurationChanged(object sender, NLog.Config.LoggingConfigurationChangedEventArgs args) => Interlocked.Exchange(ref _dirty, 1);
    private void ConfigurationReloaded(object sender, NLog.Config.LoggingConfigurationReloadedEventArgs args) => Interlocked.Exchange(ref _dirty, 1);
    private void Update()
    {
        // Constructors and config notifications may run off-thread; all mutations run here.
        for (var i = 0; i < 100 && _discovered.TryDequeue(out var name); i++) AddSource(name);
        if (Time.unscaledTime < _nextPoll) return;
        _nextPoll = Time.unscaledTime + .25f;
        if (!_aiSettings && AiGate.Value) _aiSettings = LoggingSettings.Instance;
        _nativeGate.Apply(NativeGate.Value, () => AbstractLogger.IsLogsEnabled, value => AbstractLogger.IsLogsEnabled = value);
        if (_aiSettings) _aiGate.Apply(AiGate.Value, () => _aiSettings.AILogging, value => _aiSettings.AILogging = value);
        if (Interlocked.Exchange(ref _dirty, 0) != 0)
        {
            try { _native.Apply(LogManager.Configuration, Sources.Where(s => s.Enabled.Value).Select(s => new KeyValuePair<string, CaptureLevel>(s.Name, s.Minimum.Value))); }
            catch (Exception ex) { HookStatus = "Native capture unavailable: " + ex.Message; }
        }
    }
    internal void Receive(CapturedLog entry) { Buffer.Add(entry); _writer?.Enqueue(entry); }
    internal void UnityLog(LogType type, string message, string detail = "")
    {
        var enabled = type switch {
            LogType.Log => DebugPlusConfig.UnityInfoLogging.Value,
            LogType.Warning => DebugPlusConfig.UnityWarningLogging.Value,
            LogType.Error => DebugPlusConfig.UnityErrorLogging.Value,
            LogType.Exception => DebugPlusConfig.UnityExceptionLogging.Value,
            LogType.Assert => Assertions.Value, _ => false };
        if (!enabled || CaptureOrigin.InSink) return;
        var level = type == LogType.Log ? CaptureLevel.Info : type == LogType.Warning || type == LogType.Assert ? CaptureLevel.Warning : CaptureLevel.Error;
        try
        {
            CaptureOrigin.InSink = true;
            var entry = new CapturedLog("Unity" + (type == LogType.Assert ? " assertions" : ""), level, message, detail);
            Receive(entry);
            // Preserve the original four toggles' BepInEx output semantics, including fatal exceptions.
            var bepinLevel = type == LogType.Exception ? BepInEx.Logging.LogLevel.Fatal :
                level == CaptureLevel.Info ? BepInEx.Logging.LogLevel.Info : level == CaptureLevel.Warning ? BepInEx.Logging.LogLevel.Warning : BepInEx.Logging.LogLevel.Error;
            Plugin.Log.Log(bepinLevel, entry.Message + (entry.Detail.Length == 0 ? "" : "\n" + entry.Detail));
        }
        catch { }
        finally { CaptureOrigin.InSink = false; }
    }
    private void EngineLog(string message, string stack, LogType type)
    { if (CaptureOrigin.UnityDepth == 0 && CaptureOrigin.NativeDepth == 0) UnityLog(type, message, stack); }
    public void RestoreDefaults()
    {
        DebugPlusConfig.UnityInfoLogging.Value = DebugPlusConfig.UnityWarningLogging.Value =
            DebugPlusConfig.UnityErrorLogging.Value = DebugPlusConfig.UnityExceptionLogging.Value = false;
        Assertions.Value = NativeGate.Value = AiGate.Value = false;
        foreach (var source in Sources) { source.Enabled.Value = false; source.Minimum.Value = CaptureLevel.Info; }
        Interlocked.Exchange(ref _dirty, 1);
        _nextPoll = 0; Update();
    }
    public string Export(IEnumerable<CapturedLog> entries, bool json)
    {
        var directory = Path.Combine(BepInEx.Paths.ConfigPath, "DebugPlus", "exports"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "logs-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + (json ? ".json" : ".txt"));
        File.WriteAllText(path, json ? JsonConvert.SerializeObject(entries, Formatting.Indented) : string.Join(Environment.NewLine, entries.Select(e => e.Text)));
        return path;
    }
    public object Diagnostics() => new { NativeStatus, AiStatus, HookStatus, OutputPath, FileStatus, FileDropped,
        Dropped = Buffer.Dropped, Truncated = Buffer.Truncated,
        Unity = new { Info = DebugPlusConfig.UnityInfoLogging.Value, Warning = DebugPlusConfig.UnityWarningLogging.Value,
            Error = DebugPlusConfig.UnityErrorLogging.Value, Exception = DebugPlusConfig.UnityExceptionLogging.Value, Assertions = Assertions.Value },
        NativeGate = NativeGate.Value, AiGate = AiGate.Value,
        Categories = Sources.Select(s => new { s.Name, Enabled = s.Enabled.Value, Minimum = s.Minimum.Value.ToString() }).ToArray() };
    private void OnDestroy()
    {
        Application.logMessageReceivedThreaded -= EngineLog;
        _config.SettingChanged -= Changed;
        LogManager.ConfigurationChanged -= ConfigurationChanged;
        LogManager.ConfigurationReloaded -= ConfigurationReloaded;
        _patches?.Dispose(); _native?.Dispose();
        _nativeGate.Apply(false, () => AbstractLogger.IsLogsEnabled, value => AbstractLogger.IsLogsEnabled = value);
        if (_aiSettings) _aiGate.Apply(false, () => _aiSettings.AILogging, value => _aiSettings.AILogging = value);
        _writer?.Dispose(); if (Instance == this) Instance = null;
    }
    internal const string NativeCategories = "Export|FilesChecker|aiData|aiMoveData|aiMapSettingsData|aiCoversData|ai_decision|aiVision|anticheat|application|assets|backend|backendCache|backend_queue|exfiltration|insurance|inventory|data_transfer|objectPool|ping|player|scenes|screen|display|fps|ui_sounds|localization|surprises|resources|push-notifications|spawns|spawnsystem|maperrors|channels|assetBundle|hideout|ConnectionDiagnostics|speaker|spatial-audio|winter|network-connection|network-messages|hands-states|anim-events-emitter|anim-events-container|animEvents|fast-animator-controller|net-simulation-app-logger|botProfiles|seasons|autocull|network-send-messages|CI|Default|traffic|health-system|quests";
}
