using System;
using Comfort.Common;
using DebugPlus.Config;
using EFT;
using UnityEngine;

namespace DebugPlus.Components;

public class PlayerComponent : MonoBehaviour
{
    private static Player _player;

    private static WeaponAnimEventsQueueDebug _weaponAnimDebug;
    private static WeaponDurabilityDebug _weaponDuraDebug;
    private static WeaponOverheatDebug _weaponOverHeatDebug;
    private static MalfunctionDebug _weaponMalfDebug;
    
    void Start()
    {
        _player = Singleton<GameWorld>.Instance.MainPlayer;

        _weaponAnimDebug = _player.GetOrAddComponent<WeaponAnimEventsQueueDebug>();
        _weaponAnimDebug.SetDebugObject(_player);
        
        _weaponDuraDebug = _player.GetOrAddComponent<WeaponDurabilityDebug>();
        _weaponDuraDebug.SetDebugObject(_player);
        
        _weaponOverHeatDebug = _player.GetOrAddComponent<WeaponOverheatDebug>();
        _weaponOverHeatDebug.SetDebugObject(_player);
        
        _weaponMalfDebug = _player.GetOrAddComponent<MalfunctionDebug>();
        _weaponMalfDebug.SetDebugObject(_player);
    }

    private void Update()
    {
        InfiniteStamina();
        DebugComponents();
    }

    private static void InfiniteStamina()
    {
        if (!DebugPlusConfig.InfiniteStamina.Value) return;
        
        _player.Physical.Stamina.Current = _player.Physical.Stamina.TotalCapacity.Value;
        _player.Physical.HandsStamina.Current = _player.Physical.HandsStamina.TotalCapacity.Value;
        _player.Physical.Oxygen.Current = _player.Physical.Oxygen.TotalCapacity.Value;
    }

    private static void DebugComponents()
    {
        _weaponAnimDebug.enabled = DebugPlusConfig.WeaponAnimEventsQueueDebug.Value;
        _weaponDuraDebug.enabled = DebugPlusConfig.WeaponDuraDebug.Value;
        _weaponOverHeatDebug.enabled = DebugPlusConfig.WeaponOverHeatDebug.Value;
        _weaponMalfDebug.enabled = DebugPlusConfig.WeaponMalfDebug.Value;
    }
}