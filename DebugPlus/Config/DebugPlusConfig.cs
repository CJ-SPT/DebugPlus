using BepInEx.Configuration;

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.

namespace DebugPlus.Config;

internal static class DebugPlusConfig
{
    private static readonly string _logging = "Logging";
    private static readonly string _player = "Player";
    private static readonly string _bsgDebug = "BSG Debug Tools";
    
    public static ConfigEntry<bool> UnityInfoLogging { get; private set; }
    public static ConfigEntry<bool> UnityWarningLogging { get; private set; }
    public static ConfigEntry<bool> UnityErrorLogging { get; private set; }
    public static ConfigEntry<bool> UnityExceptionLogging { get; private set; }

    public static ConfigEntry<bool> GodMode { get; private set; }
    public static ConfigEntry<bool> InfiniteStamina { get; private set; }

    public static ConfigEntry<bool> WeaponAnimEventsQueueDebug { get; private set; }
    public static ConfigEntry<bool> WeaponDuraDebug { get; private set; }
    public static ConfigEntry<bool> WeaponOverHeatDebug { get; private set; }
    
    public static ConfigEntry<bool> WeaponMalfDebug { get; private set; }
    
    public static void InitConfig(ConfigFile config)
    {
        LoggingConfig(config);
        PlayerConfig(config);
        BsgDebugConfig(config);
    }

    private static void LoggingConfig(ConfigFile config)
    {
        UnityInfoLogging = config.Bind(
            _logging,
            "Enable Info Logging",
            false,
            new ConfigDescription("Enable info output")
        );

        UnityWarningLogging = config.Bind(
            _logging,
            "Enable Warning Logging",
            false,
            new ConfigDescription("Enable warning output")
        );

        UnityErrorLogging = config.Bind(
            _logging,
            "Enable Error Logging",
            false,
            new ConfigDescription("Enable error output")
        );

        UnityExceptionLogging = config.Bind(
            _logging,
            "Enable Exception Logging",
            false,
            new ConfigDescription("Enable exception output")
        );
    }
    
    private static void PlayerConfig(ConfigFile config)
    {
        GodMode = config.Bind(
            _player,
            "God Mode",
            false,
            new ConfigDescription("Enable god mode")
            );
        
        InfiniteStamina = config.Bind(
            _player,
            "Infinite Stamina",
            false,
            new ConfigDescription("Enable infinite stamina")
        );
    }
    
    private static void BsgDebugConfig(ConfigFile config)
    {
        WeaponAnimEventsQueueDebug = config.Bind(
            _bsgDebug,
            "Weapon Animation Debug",
            false,
            new ConfigDescription("Weapon animation queue debug")
        );
        
        WeaponDuraDebug = config.Bind(
            _bsgDebug,
            "Weapon Durability Debug",
            false,
            new ConfigDescription("Weapon durability debug")
        );
        
        WeaponOverHeatDebug = config.Bind(
            _bsgDebug,
            "Weapon Overheat Debug",
            false,
            new ConfigDescription("Weapon over heat debug")
        );
        
        WeaponMalfDebug = config.Bind(
            _bsgDebug,
            "Weapon Malfunction Debug",
            false,
            new ConfigDescription("Weapon malfunction debug")
        );
    }
}