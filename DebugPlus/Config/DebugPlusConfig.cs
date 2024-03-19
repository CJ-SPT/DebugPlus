using BepInEx.Configuration;

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.

namespace DebugPlus.Config
{
    internal class DebugPlusConfig
    {
        private static readonly string header = "Debug Plus";

        public static ConfigEntry<bool> unityEngineDebugLogObj;

        public static void InitConfig(ConfigFile config)
        {
            unityEngineDebugLogObj = config.Bind(
                header,
                "Enable Base Logging",
                true,
                new ConfigDescription("Enable UnityEngine.Debug.Log() output")
                );
        }
    }
}
