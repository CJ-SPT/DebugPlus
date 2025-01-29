using System;
using BepInEx;
using BepInEx.Logging;
using DebugPlus.Config;
using DebugPlus.Patches;
using DrakiaXYZ.VersionChecker;

#pragma warning disable

namespace DebugPlus;

[BepInPlugin("com.dirtbikercj.debugplus", "DebugPlus", "1.0.2")]
public class Plugin : BaseUnityPlugin
{
	public const int TarkovVersion = 33420;

	public static Plugin Instance { get; private set; }
	public static ManualLogSource Log { get; private set; }

	internal void Awake()
	{
		if (!VersionChecker.CheckEftVersion(Logger, Info, Config))
		{
			throw new Exception("Invalid EFT Version");
		}

		Instance = this;
		DontDestroyOnLoad(this);

		Log = Logger;

		DebugPlusConfig.InitConfig(Config);

		#region LOGGIN_PATCHES

		new LogPatch().Enable();
		new LogObjPatch().Enable();

		new LogFormatPatch().Enable();
		new LogFormatObjPatch().Enable();

		new LogWarningPatch().Enable();
		new LogWarningContextPatch().Enable();
		new LogWarningFormatPatch().Enable();
		new LogWarningFormatContextPatch().Enable();

		new LogErrorPatch().Enable();
		new LogErrorObjPatch().Enable();
		new LogErrorFormatPatch().Enable();
		new LogErrorFormatObjPatch().Enable();

		new LogExceptionPatch().Enable();
		new LogExceptionContextPatch().Enable();

		#endregion

		#region PLAYER_PATCHES

		new GodModePatch().Enable();
		new OnGameStartedPatch().Enable();

		#endregion
	}
}