using System;

namespace DebugPlus.Logging;

internal sealed class GateOverride
{
    private bool _owned, _original;
    public void Apply(bool enabled, Func<bool> read, Action<bool> write)
    {
        if (enabled)
        {
            // A later native initialization can reset the value while the override is active.
            if (!_owned || !read()) _original = read();
            _owned = true; if (!read()) write(true);
        }
        else if (_owned) { if (read()) write(_original); _owned = false; }
    }
}
