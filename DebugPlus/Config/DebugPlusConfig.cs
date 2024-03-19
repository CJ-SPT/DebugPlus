using BepInEx.Configuration;

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.

namespace DebugPlus.Config
{
    internal class DebugPlusConfig
    {
        private static readonly string header = "Debug Plus";

        public static ConfigEntry<bool> UnityInfoLogging;
        public static ConfigEntry<bool> UnityWarningLogging;
        public static ConfigEntry<bool> UnityErrorLogging;
        public static ConfigEntry<bool> UnityExceptionLogging;

        public static void InitConfig(ConfigFile config)
        {
            UnityInfoLogging = config.Bind(
                header,
                "Enable Info Logging",
                true,
                new ConfigDescription("Enable info output")
                );

            UnityWarningLogging = config.Bind(
                header,
                "Enable Warning Logging",
                true,
                new ConfigDescription("Enable warning output")
                );

            UnityErrorLogging = config.Bind(
                header,
                "Enable Error Logging",
                true,
                new ConfigDescription("Enable error output")
                );

            UnityExceptionLogging = config.Bind(
                header,
                "Enable Exception Logging",
                true,
                new ConfigDescription("Enable exception output")
                );
        }
    }
}
