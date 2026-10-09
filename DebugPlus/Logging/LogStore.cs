#nullable disable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DebugPlus.Logging;

public enum CaptureLevel { Trace, Debug, Info, Warning, Error, Fatal }

public sealed class CapturedLog
{
    public DateTime Time { get; }
    public string Source { get; }
    public CaptureLevel Level { get; }
    public string Message { get; }
    public string Detail { get; }
    public bool Truncated { get; }
    internal int Bytes { get; }
    public CapturedLog(string source, CaptureLevel level, string message, string detail = "")
    {
        Time = DateTime.UtcNow; Level = level;
        // Bound both UTF-8 output and retained UTF-16 strings, including metadata.
        var budget = 64 * 1024 - 512;
        Source = Limit(source ?? "Unknown", ref budget, out var a);
        Message = Limit(message ?? "<null>", ref budget, out var b);
        Detail = Limit(detail ?? "", ref budget, out var c);
        Truncated = a || b || c; Bytes = 64 * 1024 - budget;
    }
    private static string Limit(string text, ref int budget, out bool truncated)
    {
        int Cost(int length) => Math.Max(length * 2, Encoding.UTF8.GetByteCount(text, 0, length));
        var low = 0; var high = Math.Min(text.Length, budget / 2);
        while (low < high) { var mid = (low + high + 1) / 2; if (Cost(mid) <= budget) low = mid; else high = mid - 1; }
        var length = low;
        if (length < text.Length && length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
        truncated = length != text.Length;
        budget -= Cost(length);
        return truncated ? text.Substring(0, length) : text;
    }
    public string Text => $"{Time:O} | {Source} | {Level} | {Message}" +
        (Detail.Length == 0 ? "" : Environment.NewLine + Detail) + (Truncated ? " [truncated]" : "");
}

public sealed class LogBuffer
{
    private readonly object _sync = new();
    private readonly Queue<CapturedLog> _entries = new();
    private readonly int _maxCount, _maxBytes;
    private int _bytes;
    private long _dropped, _truncated, _version;
    public LogBuffer(int maxCount = 5000, int maxBytes = 16 * 1024 * 1024) { _maxCount = maxCount; _maxBytes = maxBytes; }
    public long Dropped { get { lock (_sync) return _dropped; } }
    public long Truncated { get { lock (_sync) return _truncated; } }
    public long Version { get { lock (_sync) return _version; } }
    public void Add(CapturedLog entry)
    {
        lock (_sync)
        {
            if (entry.Truncated) _truncated++;
            while (_entries.Count > 0 && (_entries.Count >= _maxCount || _bytes + entry.Bytes > _maxBytes))
            { _bytes -= _entries.Dequeue().Bytes; _dropped++; }
            if (entry.Bytes > _maxBytes) { _dropped++; _version++; return; }
            _entries.Enqueue(entry); _bytes += entry.Bytes; _version++;
        }
    }
    public CapturedLog[] Snapshot() { lock (_sync) return _entries.ToArray(); }
    public void Clear() { lock (_sync) { _entries.Clear(); _bytes = 0; _version++; } }
}

// Correlation is scoped to the actual call chain, never message text or timing.
internal static class CaptureOrigin
{
    [ThreadStatic] public static int UnityDepth;
    [ThreadStatic] public static int NativeDepth;
    [ThreadStatic] public static bool InSink;
    public static string SafeString(object value)
    { try { return value?.ToString() ?? "<null>"; } catch { return "<ToString failed>"; } }
    public static string Format(string format, object[] args)
    {
        if (args == null || args.Length == 0) return format ?? "<null>";
        try { return string.Format(format ?? "", args); }
        catch { return (format ?? "<null>") + " [arguments: " + string.Join(", ", args.Select(SafeString)) + "]"; }
    }
}

internal sealed class RotatingLogWriter : IDisposable
{
    private readonly object _sync = new();
    private readonly Queue<CapturedLog> _queue = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Task _worker;
    private readonly long _rotateBytes;
    private readonly int _archives;
    private int _queuedBytes;
    private bool _stopping;
    private long _dropped;
    private volatile string _error;
    public string Path { get; }
    public string Error => _error;
    public long Dropped => Interlocked.Read(ref _dropped);
    public RotatingLogWriter(string path, long rotateBytes = 10 * 1024 * 1024, int archives = 5)
    { Path = path; _rotateBytes = rotateBytes; _archives = archives; _worker = Task.Run(Run); }
    public void Enqueue(CapturedLog entry)
    {
        lock (_sync)
        {
            if (_stopping || _error != null) { Interlocked.Increment(ref _dropped); return; }
            if (_queue.Count >= 5000 || _queuedBytes + entry.Bytes > 16 * 1024 * 1024)
            { Interlocked.Increment(ref _dropped); return; }
            _queue.Enqueue(entry); _queuedBytes += entry.Bytes; _wake.Set();
        }
    }
    private void Run()
    {
        FileStream stream = null;
        try
        {
            while (true)
            {
                CapturedLog entry;
                lock (_sync)
                {
                    if (_queue.Count == 0) { if (_stopping) break; entry = null; }
                    else { entry = _queue.Dequeue(); _queuedBytes -= entry.Bytes; }
                }
                if (entry == null) { stream?.Flush(); _wake.WaitOne(500); continue; }
                if (stream == null) { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)); stream = Open(); }
                var bytes = Encoding.UTF8.GetBytes(entry.Text + Environment.NewLine);
                if (stream.Length > 0 && stream.Length + bytes.Length > _rotateBytes)
                {
                    stream.Dispose(); stream = null;
                    if (File.Exists(Path + "." + _archives)) File.Delete(Path + "." + _archives);
                    for (var i = _archives - 1; i >= 1; i--)
                        if (File.Exists(Path + "." + i)) File.Move(Path + "." + i, Path + "." + (i + 1));
                    File.Move(Path, Path + ".1"); stream = Open();
                }
                stream.Write(bytes, 0, bytes.Length);
            }
        }
        catch (Exception ex)
        {
            _error = "File output disabled: " + ex.Message;
            lock (_sync) { Interlocked.Add(ref _dropped, _queue.Count + 1); _queue.Clear(); _queuedBytes = 0; }
        }
        finally { try { stream?.Dispose(); } catch { } _wake.Dispose(); }
    }
    private FileStream Open() => new(Path, FileMode.Append, FileAccess.Write, FileShare.Read, 16384);
    public void Dispose()
    {
        lock (_sync) { _stopping = true; if (!_worker.IsCompleted) try { _wake.Set(); } catch (ObjectDisposedException) { } }
        _worker.Wait(TimeSpan.FromSeconds(3));
    }
}
