using System;
using BepInEx;
using BepInEx.Logging;
using DebugPlus.Config;
using DebugPlus.ConsoleCommands;
using DrakiaXYZ.VersionChecker;
using EFT.UI;
using SPT.Reflection.Patching;

namespace DebugPlus;

[BepInPlugin("com.dirtbikercj.debugplus", "DebugPlus", BuildInfo.Version)]
public class Plugin : BaseUnityPlugin
{
	public const int TarkovVersion = 40087;

	public static Plugin? Instance { get; private set; }
	public static ManualLogSource Log { get; private set; }

	private PatchManager? _patchManager;
	
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

		_patchManager = new PatchManager(this, true);
		_patchManager.EnablePatches();
		
		RegisterCommands();
	}

	private static void RegisterCommands()
	{
		ConsoleScreen.Processor.RegisterCommand<SpawnBotsAsync>();
		ConsoleScreen.Processor.RegisterCommand<ReloadFromServerAsync>();
		ConsoleScreen.Processor.RegisterCommand<StartRaidAsync>();
	}
}