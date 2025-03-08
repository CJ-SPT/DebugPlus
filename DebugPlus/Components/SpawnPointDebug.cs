using System.Collections.Generic;
using System.Linq;
using DebugPlus.Config;
using DebugPlus.Utils;
using EFT.Game.Spawning;
using UnityEngine;

using OCB = DebugPlus.Utils.OverlayContentBuilder;

namespace DebugPlus.Components;

/// <summary>
/// Renders spawn point information for bot zones
/// </summary>
public class SpawnPointDebug : MonoBehaviour
{
	private List<BotZone> _botZones = [];
	private readonly List<SpawnPointInfo> _spawnPointInfos = [];
	
	public void RefreshZones()
	{
		// This method is just lol, but works for this use case.
		_botZones = LocationScene.GetAllObjectsAndWhenISayAllIActuallyMeanIt<BotZone>()
			.ToList();
		
		_spawnPointInfos.Clear();
		
		foreach (var zone in _botZones)
		{
			IterateSpawnPoints(zone);
		}
		
		foreach (var point in _spawnPointInfos)
		{
			point.Sphere.GetOrAddComponent<OverlayProvider>()
				.SetOverlayContent(point.Content, Enable);
		}
	}
	
	private void Awake()
	{
		RefreshZones();
	}
	
	private void OnDestroy()
	{
		foreach (var obj in _spawnPointInfos.ToArray())
		{
			_spawnPointInfos.Remove(obj);
		}
	}

	private void IterateSpawnPoints(BotZone zone)
	{
		var zoneColor = GetRandomColor();
		
		foreach (var spawnPoint in zone.SpawnPoints)
		{
			CreateSpawnPointInfo(spawnPoint, zone, zoneColor);
		}
	}
	
	private void CreateSpawnPointInfo(ISpawnPoint spawnPoint, BotZone zone, Color color)
	{
		var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
		sphere.transform.position = spawnPoint.Position;
		sphere.transform.localScale = new Vector3(0.5f, 0.5f, 0.5f);
				
		var sphereRenderer = sphere.GetComponent<Renderer>();
		sphereRenderer.material.color = color;
		
		var infoText = GetPointInfoText(spawnPoint, zone);
		
		var pointInfo = new SpawnPointInfo()
		{
			Sphere = sphere,
			Content = infoText
		};
		
		_spawnPointInfos.Add(pointInfo);
	}

	private static string GetPointInfoText(ISpawnPoint spawnPoint, BotZone zone)
	{
		OCB.Clear();
		
		OCB.AppendLabeledValue("BotZone Name", zone.ShortName, Color.gray, Color.green);
		OCB.AppendLabeledValue("BotZone Id", zone.Id.ToString(), Color.gray, Color.green);
		OCB.AppendLabeledValue("Spawn Point Name", spawnPoint.Name, Color.gray, Color.green);
		OCB.AppendLabeledValue("Position", spawnPoint.Position.ToString(), Color.gray, Color.green);
		OCB.AppendLabeledValue("Side Mask", spawnPoint.Sides.ToString(), Color.gray, Color.green);
		OCB.AppendLabeledValue("IsSniper", spawnPoint.IsSnipeZone.ToString(), Color.gray, Color.green);
		
		return OCB.ToString();
	}
	
	private static Color GetRandomColor()
	{
		return new Color(
			Random.Range(0f, 1f), 
			Random.Range(0f, 1f), 
			Random.Range(0f, 1f));
	}
	
	private static bool Enable()
	{
		return DebugPlusConfig.ShowSpawnPointOverlays.Value;
	}
	
	private class SpawnPointInfo
	{
		public GameObject Sphere;
		public string Content;
	}
}