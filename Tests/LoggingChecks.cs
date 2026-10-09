using DebugPlus.Logging;
using NLog;
using NLog.Config;
using NLog.Targets;

internal static class LoggingChecks
{
    public static void Run()
    {
        var checks = 0;
        void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
        var buffer = new LogBuffer();
        CaptureOrigin.NativeDepth = 0;
        Parallel.For(0, 6000, _ => buffer.Add(new CapturedLog("test", CaptureLevel.Info, "repeat")));
        Check(buffer.Snapshot().Length == 5000 && buffer.Dropped == 1000, "bounded concurrent capture preserves genuine repeats");
        var huge = new CapturedLog("test", CaptureLevel.Error, new string('界', 100000));
        Check(huge.Truncated && huge.Bytes <= 65536 && System.Text.Encoding.UTF8.GetByteCount(huge.Text) <= 65536, "UTF8 entry cap");
        var byteBuffer = new LogBuffer(maxBytes: 100000); byteBuffer.Add(huge); byteBuffer.Add(huge);
        Check(byteBuffer.Snapshot().Length == 1 && byteBuffer.Dropped == 1 && byteBuffer.Truncated == 2, "byte budget and truncation counters");
        buffer.Clear(); Check(buffer.Snapshot().Length == 0, "clear history");
        Check(CaptureOrigin.SafeString(null) == "<null>" && CaptureOrigin.SafeString(new BadString()) == "<ToString failed>", "null and hostile objects");
        Check(CaptureOrigin.Format("literal {broken}", new object[] { 1 }).Contains("arguments: 1"), "invalid formatting retains evidence");
        Check(CaptureOrigin.Format("value {0:0.00}", new object[] { 2.5 }) == "value 2.50", "format specifiers preserved");
        var gate = new GateOverride(); var value = false;
        gate.Apply(true, () => value, v => value = v); Check(value, "native gate enabled");
        gate.Apply(false, () => value, v => value = v); Check(!value, "native gate restored");
        value = true; gate.Apply(true, () => value, v => value = v); gate.Apply(false, () => value, v => value = v); Check(value, "preexisting enabled gate preserved");
        gate.Apply(true, () => value, v => value = v); value = false; gate.Apply(true, () => value, v => value = v); gate.Apply(false, () => value, v => value = v);
        Check(!value, "late native initialization becomes restoration baseline");
        var normal = new MemoryTarget("ordinary") { Layout = "${message}" };
        var configuration = new LoggingConfiguration(); configuration.AddTarget(normal);
        var original = new LoggingRule("inventory", LogLevel.Error, normal) { Final = true }; configuration.LoggingRules.Add(original);
        LogManager.Configuration = configuration;
        var captured = new List<CapturedLog>();
        using (var capture = new NativeCapture(captured.Add))
        {
            capture.Apply(configuration, new[] { new KeyValuePair<string, CaptureLevel>("inventory", CaptureLevel.Debug), new KeyValuePair<string, CaptureLevel>("disabled", CaptureLevel.Trace) });
            var logger = LogManager.GetLogger("inventory");
            logger.Trace("hidden trace"); logger.Debug("visible debug"); logger.Error("ordinary error");
            LogManager.GetLogger("disabled").Trace("previously disabled");
            Check(captured.Count == 3 && normal.Logs.SequenceEqual(new[] { "ordinary error" }), "native capture precedes final rules and preserves ordinary output");
            CaptureOrigin.UnityDepth++;
            try { logger.Error("Unity forwarding"); } finally { CaptureOrigin.UnityDepth--; }
            Check(captured.Count == 3, "scoped Unity forwarding excluded from native duplicate capture");
            CaptureOrigin.InSink = true;
            try { logger.Error("recursive output"); } finally { CaptureOrigin.InSink = false; }
            Check(captured.Count == 3, "recursive capture excluded");
            var replacement = new LoggingConfiguration(); LogManager.Configuration = replacement;
            capture.Apply(replacement, new[] { new KeyValuePair<string, CaptureLevel>("inventory", CaptureLevel.Info) });
            logger.Debug("below new minimum"); logger.Info("after reload"); logger.Info("after reload");
            Check(captured.Count == 5, "reload reattaches exactly once and legitimate repeated messages survive");
            Check(configuration.LoggingRules.Count == 1 && ReferenceEquals(configuration.LoggingRules[0], original), "only owned rules removed from old configuration");
            capture.Apply(replacement, Array.Empty<KeyValuePair<string, CaptureLevel>>()); logger.Error("capture disabled");
            Check(captured.Count == 5, "disabled source no longer captured");
            Check(replacement.AllTargets.Count == 0, "restore removes capture target as well as rules");
        }
        Check(LogManager.Configuration.LoggingRules.Count == 0, "native capture teardown");
        LogManager.Shutdown();
        var directory = Path.Combine(Path.GetTempPath(), "debugplus-log-checks-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var configPath = Path.Combine(directory, "logging.cfg");
            var config = new BepInEx.Configuration.ConfigFile(configPath, false);
            var source = new LogSource("inventory", config);
            Check(!source.Enabled.Value && source.Minimum.Value == CaptureLevel.Info, "new source defaults remain disabled");
            source.Enabled.Value = true; source.Minimum.Value = CaptureLevel.Trace; config.Save();
            var restored = new LogSource("inventory", new BepInEx.Configuration.ConfigFile(configPath, false));
            Check(restored.Enabled.Value && restored.Minimum.Value == CaptureLevel.Trace, "real BepInEx settings survive restart");
            var changed = 0; source.Enabled.SettingChanged += (_, _) => changed++;
            source.Enabled.Value = false;
            Check(changed == 1, "config notifications support workbench and F12 synchronization");
            var path = Path.Combine(directory, "client.log");
            using (var writer = new RotatingLogWriter(path, 250, 5))
            { for (var i = 0; i < 30; i++) writer.Enqueue(new CapturedLog("test", CaptureLevel.Info, "message " + i)); }
            Check(File.Exists(path + ".5") && !File.Exists(path + ".6") && File.ReadAllText(path).Contains("message 29"), "asynchronous ordered output and five-file rotation");
            var blocker = Path.Combine(directory, "file-not-directory"); File.WriteAllText(blocker, "blocked");
            using (var writer = new RotatingLogWriter(Path.Combine(blocker, "client.log")))
            {
                writer.Enqueue(new CapturedLog("test", CaptureLevel.Info, "write failure"));
                Check(SpinWait.SpinUntil(() => writer.Error != null, 3000), "file failure is reported without throwing to caller");
                buffer.Add(new CapturedLog("test", CaptureLevel.Info, "viewer survives"));
                Check(buffer.Snapshot().Length == 1, "viewer survives disk failure");
            }
        }
        finally
        {
            if (!Path.GetFullPath(directory).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(directory).StartsWith("debugplus-log-checks-", StringComparison.Ordinal)) throw new Exception("Unexpected test cleanup path.");
            Directory.Delete(directory, true);
        }
        Console.WriteLine($"Logging checks: {checks} passed.");
    }
    private sealed class BadString { public override string ToString() => throw new InvalidOperationException(); }
}
