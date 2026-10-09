using System;
using System.Linq;
using System.Text;
using BepInEx.Configuration;

namespace DebugPlus.Logging;

public sealed class LogSource
{
    public string Name { get; }
    public ConfigEntry<bool> Enabled { get; }
    public ConfigEntry<CaptureLevel> Minimum { get; }
    internal LogSource(string name, ConfigFile config)
    {
        Name = name;
        var safe = name.All(c => char.IsLetterOrDigit(c) || "-_. ".Contains(c));
        var section = safe ? "Native Logging - " + name : "Native Logging encoded - " + BitConverter.ToString(Encoding.UTF8.GetBytes(name)).Replace("-", "");
        Enabled = config.Bind(section, "Capture", false, "Reveal and capture native EFT category: " + name);
        Minimum = config.Bind(section, "Minimum severity", CaptureLevel.Info, "Minimum captured severity for " + name + ". Ordinary game output is unchanged.");
        void Validate() { if (!Enum.IsDefined(typeof(CaptureLevel), Minimum.Value)) Minimum.Value = CaptureLevel.Info; }
        Validate(); Minimum.SettingChanged += (_, _) => Validate();
    }
}
