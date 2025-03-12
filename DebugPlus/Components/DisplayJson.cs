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
    private static Material mat;

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
        LocationsJson locationJson;
        try
        {
            locationJson = JsonHelper.ParseJsonFromFile<LocationsJson>(filepath);
        }
        catch (Exception e)
        {
            Plugin.Log.LogError("Loading the json failed.\n Error:" + e.Message);
            return;
        }

        if (locationJson == null || locationJson.locations == null)
        {
            Plugin.Log.LogError("Loading the json failed.\n Error: Missing locations in json, or JSON isn't properly formatted.");
            return;
        }
        //Plugin.Log.LogInfo(JsonHelper.SerializeJson(locationJson));
        string mapName = Singleton<EFT.GameWorld>.Instance.MainPlayer.Location.ToLower();
        Plugin.Log.LogInfo("Loading JSON for map: " + mapName);

        if (!locationJson.locations.ContainsKey(mapName))
        {
            Plugin.Log.LogInfo("No JSON entries on map: " + mapName);
            return;
        }

        foreach (JsonLocationEntry location in locationJson.locations[mapName])
        {
            CreatPoint(location);
        }
    }

    private void CreatPoint(JsonLocationEntry location)
    {
        GameObject renderedObject = GameObject.CreatePrimitive(location.objectType);
        renderedObject.GetComponent<Collider>().enabled = location.physics;
        renderedObject.transform.position = location.coordinates;
        renderedObject.transform.localScale = location.objectScale;
        renderedObject.GetComponent<Renderer>().material = GetMaterial();
        renderedObject.GetComponent<Renderer>().material.color = location.objectColor;
        Plugin.Log.LogInfo(location.text);

        var jsonDisplayInfo = new JsonDisplayInfo()
        {
            RenderedObject = renderedObject,
            Content = GetJsonText(location)
        };

        _jsonDisplayInfos.Add(jsonDisplayInfo);
    }

    private Material GetMaterial()
    {
        if (DisplayJson.mat == null)
        {
            Plugin.Log.LogInfo("Generating new material!");
            DisplayJson.mat = new Material(Shader.Find("Transparent/Diffuse"));

            DisplayJson.mat.SetOverrideTag("RenderType", "Transparent");
            DisplayJson.mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            DisplayJson.mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            DisplayJson.mat.SetInt("_ZWrite", 0);
            DisplayJson.mat.DisableKeyword("_ALPHATEST_ON");
            DisplayJson.mat.EnableKeyword("_ALPHABLEND_ON");
            DisplayJson.mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");

            DisplayJson.mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        }
        return DisplayJson.mat;
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

        OCB.AppendLabeledValue(entry.label, entry.text, entry.labelColor, entry.textColor, entry.label.Length > 0);

        return OCB.ToString();
    }

    private class JsonDisplayInfo
    {
        public GameObject RenderedObject;
        public string Content;
    }
}