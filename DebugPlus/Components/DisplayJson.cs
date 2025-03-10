using System.Collections.Generic;
using Comfort.Common;
using DebugPlus.Config;
using DebugPlus.Utils;
using UnityEngine;
using OCB = DebugPlus.Utils.OverlayContentBuilder;

namespace DebugPlus.Components;

public class DisplayJson : MonoBehaviour
{
    private List<GameObject> spheres;

    private PrimitiveType GetPrimitiveType(string type)
    {
        switch (type.ToLower())
        {
            case "Sphere":
                return PrimitiveType.Sphere;
            case "Capsule":
                return PrimitiveType.Capsule;
            case "Cylinder":
                return PrimitiveType.Cylinder;
            case "Cube":
                return PrimitiveType.Cube;
            case "Plane":
                return PrimitiveType.Plane;
            case "Quad":
                return PrimitiveType.Quad;
            default:
                return PrimitiveType.Sphere;
        }
    }

    private void Awake()
    {
        spheres = new List<GameObject>();
        string filepath = "/BepInEx/plugins/file.json";
        LocationsJson locationJson = JsonHelper.ParseJsonFromFile<LocationsJson>(filepath);
        Plugin.Log.LogInfo(JsonHelper.SerializeJson(locationJson));
        string mapName = Singleton<EFT.GameWorld>.Instance.MainPlayer.Location.ToLower();
        Plugin.Log.LogInfo("Loading: " + mapName);

        foreach (JsonLocationEntry location in locationJson.locations[mapName])
        {
            GameObject renderedObject = GameObject.CreatePrimitive(GetPrimitiveType(location.objectType));
            renderedObject.GetComponent<Collider>().enabled = location.physics;
            renderedObject.transform.position = location.coordinates;
            renderedObject.transform.localScale = location.objectScale;
            renderedObject.GetComponent<Renderer>().material.color = location.objectColor;
            Plugin.Log.LogInfo(location.text);

            renderedObject.GetOrAddComponent<OverlayProvider>().SetOverlayContent(GetJsonText(location), Enable);

            spheres.Add(renderedObject);
        }
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
}