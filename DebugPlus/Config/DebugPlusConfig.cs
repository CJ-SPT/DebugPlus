using System;
using System.Collections.Generic;
using BepInEx.Configuration;

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.

namespace DebugPlus.Config;

internal static class DebugPlusConfig
{
    private static readonly List<ConfigEntryBase> ConfigEntries = [];
    
    private const string Logging = "Logging";
    private const string Player = "Player";
    private const string BsgDebug = "BSG Debug Tools";
    private const string Overlays = "Overlays";

    #region LOGGING

    public static ConfigEntry<bool> UnityInfoLogging { get; private set; }
    public static ConfigEntry<bool> UnityWarningLogging { get; private set; }
    public static ConfigEntry<bool> UnityErrorLogging { get; private set; }
    public static ConfigEntry<bool> UnityExceptionLogging { get; private set; }

    #endregion

    #region PLAYER

    public static ConfigEntry<bool> GodMode { get; private set; }
    public static ConfigEntry<bool> InfiniteStamina { get; private set; }

    #endregion

    #region BSG_DEBUG

    public static ConfigEntry<bool> WeaponAnimEventsQueueDebug { get; private set; }
    public static ConfigEntry<bool> WeaponDuraDebug { get; private set; }
    public static ConfigEntry<bool> WeaponOverHeatDebug { get; private set; }
    public static ConfigEntry<bool> WeaponMalfDebug { get; private set; }

    #endregion

    #region OVERLAYS

    public static ConfigEntry<int> OverlayFontSize { get; private set; }
    public static ConfigEntry<float> OverlayMaxDist { get; private set; }
    public static ConfigEntry<bool> ShowSpawnPointOverlays { get; private set; }

    #endregion
    
    public static void InitConfig(ConfigFile config)
    {
        LoggingConfig(config);
        PlayerConfig(config);
        BsgDebugConfig(config);
        OverlayConfig(config);
        
        RecalcOrder();
    }

    private static void LoggingConfig(ConfigFile config)
    {
        ConfigEntries.Add(UnityInfoLogging = config.Bind(
            Logging,
            "Enable Info Logging",
            false,
            new ConfigDescription(
                "Enables info logging.",
                null,
                new ConfigurationManagerAttributes { })));

        ConfigEntries.Add(UnityWarningLogging = config.Bind(
            Logging,
            "Enable Warning Logging",
            false,
            new ConfigDescription(
                "Enables warning logging.",
                null,
                new ConfigurationManagerAttributes { })));

        ConfigEntries.Add(UnityErrorLogging = config.Bind(
            Logging,
            "Enable Error Logging",
            false,
            new ConfigDescription(
                "Enables error logging.",
                null,
                new ConfigurationManagerAttributes { })));

        ConfigEntries.Add(UnityExceptionLogging = config.Bind(
            Logging,
            "Enable Exception Logging",
            false,
            new ConfigDescription(
                "Enables exception logging.",
                null,
                new ConfigurationManagerAttributes { })));
    }
    
    private static void PlayerConfig(ConfigFile config)
    {
        ConfigEntries.Add(GodMode = config.Bind(
            Player,
            "God Mode",
            false,
            new ConfigDescription(
                "Enables GodMode.",
                null,
                new ConfigurationManagerAttributes { })));
        
        ConfigEntries.Add(InfiniteStamina = config.Bind(
            Player,
            "Infinite Stamina",
            false,
            new ConfigDescription(
                "Enables infinite stamina.",
                null,
                new ConfigurationManagerAttributes { })));
    }
    
    private static void BsgDebugConfig(ConfigFile config)
    {
        ConfigEntries.Add(WeaponAnimEventsQueueDebug = config.Bind(
            BsgDebug,
            "Weapon Animation Debug",
            false,
            new ConfigDescription(
                "Shows weapon animation debug information.",
                null,
                new ConfigurationManagerAttributes { })));
        
        ConfigEntries.Add(WeaponDuraDebug = config.Bind(
            BsgDebug,
            "Weapon Durability Debug",
            false,
            new ConfigDescription(
                "Shows weapon durability debug information.",
                null,
                new ConfigurationManagerAttributes { })));
        
        ConfigEntries.Add(WeaponOverHeatDebug = config.Bind(
            BsgDebug,
            "Weapon Overheat Debug",
            false,
            new ConfigDescription(
                "Shows weapon overheat debug information.",
                null,
                new ConfigurationManagerAttributes { })));

        ConfigEntries.Add(WeaponMalfDebug = config.Bind(
            BsgDebug,
            "Weapon Malfunction Debug",
            false,
            new ConfigDescription(
                "Shows weapon malfunction debug information.",
                null,
                new ConfigurationManagerAttributes { })));
    }

    private static void OverlayConfig(ConfigFile config)
    {
        ConfigEntries.Add(OverlayFontSize = config.Bind(
            Overlays,
            "Font size",
            18,
            new ConfigDescription(
                "Sets the font size of overlays.",
                new AcceptableValueRange<int>(6, 32),
                new ConfigurationManagerAttributes { })));
        
        ConfigEntries.Add(OverlayMaxDist = config.Bind(
            Overlays,
            "Render distance",
            200f,
            new ConfigDescription(
                "Max distance to render an overlay",
                new AcceptableValueRange<float>(0f, 1000f),
                new ConfigurationManagerAttributes { })));
        
        ConfigEntries.Add(ShowSpawnPointOverlays = config.Bind(
            Overlays,
            "Show spawn point overlay",
            false,
            new ConfigDescription(
                "Shows overlays with information above each spawn point.",
                null,
                new ConfigurationManagerAttributes { })));
    }
    
    private static void RecalcOrder()
    {
        // Set the Order field for all settings, to avoid unnecessary changes when adding new settings
        var settingOrder = ConfigEntries.Count;
        foreach (var entry in ConfigEntries)
        {
            var attributes = entry.Description.Tags[0] as ConfigurationManagerAttributes;
            if (attributes != null)
            {
                attributes.Order = settingOrder;
            }

            settingOrder--;
        }
    }
}