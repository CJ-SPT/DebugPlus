using System.IO;
using Newtonsoft.Json;
using System;

public class JsonHelper
{
    public static T ParseJsonFromFile<T>(string filepath)
    {
        filepath = Environment.CurrentDirectory + "/" + filepath;
        var json = File.ReadAllText(filepath);
        return ParseJson<T>(json);
    }

    public static T ParseJson<T>(string json)
    {
        return JsonConvert.DeserializeObject<T>(json);
    }

    public static string SerializeJson<T>(T objectToSerialize)
    {
        return JsonConvert.SerializeObject(objectToSerialize, Formatting.Indented );
    }
}
