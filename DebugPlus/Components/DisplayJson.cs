using System;
using System.Collections.Generic;
using Comfort.Common;
using DebugPlus.Config;
using DebugPlus.Utils;
using UnityEngine;
using OCB = DebugPlus.Utils.OverlayContentBuilder;

namespace DebugPlus.Components;

public class DisplayJson : MonoBehaviour
{
    private readonly List<JsonDisplayInfo> _jsonDisplayInfos = [];

    public void RefreshDisplays(object obj, EventArgs e)
    {
        if (!Enable())
        {
            foreach (var point in _jsonDisplayInfos)
            {
                Destroy(point.RenderedObject);
            }

            _jsonDisplayInfos.Clear();
            return;
        }

        _jsonDisplayInfos.Clear();

        CreateJsonDisplays();

        foreach (var point in _jsonDisplayInfos)
        {
            point.RenderedObject.GetOrAddComponent<OverlayProvider>()
                .SetOverlayContent(point.Content, Enable);
        }
    }

    private void CreateJsonDisplays()
    {
        string filepath = "/BepInEx/plugins/points.json";
        LocationsJson locationJson = JsonHelper.ParseJsonFromFile<LocationsJson>(filepath);
        //Plugin.Log.LogInfo(JsonHelper.SerializeJson(locationJson));
        string mapName = Singleton<EFT.GameWorld>.Instance.MainPlayer.Location.ToLower();
        Plugin.Log.LogInfo("Loading: " + mapName);

        if (!locationJson.locations.ContainsKey(mapName))
        {
            Plugin.Log.LogInfo("No entries on map: " + mapName);
            return;
        }

        foreach (JsonLocationEntry location in locationJson.locations[mapName])
        {
            GameObject renderedObject = GameObject.CreatePrimitive(location.objectType);
            renderedObject.GetComponent<Collider>().enabled = location.physics;
            renderedObject.transform.position = location.coordinates;
            renderedObject.transform.localScale = location.objectScale;
            renderedObject.GetComponent<Renderer>().material.color = location.objectColor;
            Plugin.Log.LogInfo(location.text);

            var jsonDisplayInfo = new JsonDisplayInfo()
            {
                RenderedObject = renderedObject,
                Content = GetJsonText(location)
            };

            _jsonDisplayInfos.Add(jsonDisplayInfo);
        }
    }

    private void Awake()
    {
        RefreshDisplays(null, null);

        DebugPlusConfig.ShowJsonOverlay.SettingChanged += RefreshDisplays;
    }

    private void OnDestroy()
    {
        foreach (var obj in _jsonDisplayInfos.ToArray())
        {
            Destroy(obj.RenderedObject);
            _jsonDisplayInfos.Remove(obj);
        }

        DebugPlusConfig.ShowJsonOverlay.SettingChanged -= RefreshDisplays;
    }

    private static bool Enable()
    {
        return DebugPlusConfig.ShowJsonOverlay.Value;
    }

    private static string GetJsonText(JsonLocationEntry entry)
    {
        OCB.Clear();

        OCB.AppendLabeledValue(entry.label, entry.text, entry.labelColor, entry.textColor);

        return OCB.ToString();
    }

    private class JsonDisplayInfo
    {
        public GameObject RenderedObject;
        public string Content;
    }
}