using DebugPlus.Config;
using DebugPlus.Runtime;
using EFT;
using UnityEngine;

namespace DebugPlus.Components;

public sealed class PlayerComponent : MonoBehaviour
{
    private Player _player;
    private WeaponAnimEventsQueueDebug _animation;
    private WeaponDurabilityDebug _durability;
    private WeaponOverheatDebug _overheat;
    private MalfunctionDebug _malfunction;
    private void Update()
    {
        var runtime = WorkbenchRuntime.Instance;
        if (!runtime || !runtime.InRaid || runtime.IsHeadless) return;
        var player = WorkbenchRuntime.World?.MainPlayer;
        if (!player || !player.IsYourPlayer || !player.HealthController.IsAlive) return;
        if (_player != player)
        {
            _player = player;
            _animation = player.GetOrAddComponent<WeaponAnimEventsQueueDebug>(); _animation.SetDebugObject(player);
            _durability = player.GetOrAddComponent<WeaponDurabilityDebug>(); _durability.SetDebugObject(player);
            _overheat = player.GetOrAddComponent<WeaponOverheatDebug>(); _overheat.SetDebugObject(player);
            _malfunction = player.GetOrAddComponent<MalfunctionDebug>(); _malfunction.SetDebugObject(player);
        }
        if (_animation) _animation.enabled = DebugPlusConfig.WeaponAnimEventsQueueDebug.Value;
        if (_durability) _durability.enabled = DebugPlusConfig.WeaponDuraDebug.Value;
        if (_overheat) _overheat.enabled = DebugPlusConfig.WeaponOverHeatDebug.Value;
        if (_malfunction) _malfunction.enabled = DebugPlusConfig.WeaponMalfDebug.Value;
    }
}
