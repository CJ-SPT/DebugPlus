using System.Reflection;
using System.Text.Json;
using SPTarkov.DI.Annotations;

namespace DebugPlus.Server;

public sealed class ServerConfiguration
{
    public string[] AdminProfileIds { get; set; } = [];
}

[Injectable(InjectionType.Singleton)]
public sealed class ServerPolicy
{
    public string DirectoryPath { get; } = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
    private string ConfigurationPath => Path.Combine(DirectoryPath, "config.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public void Initialize()
    {
        Directory.CreateDirectory(DirectoryPath);
        if (!File.Exists(ConfigurationPath)) File.WriteAllText(ConfigurationPath, JsonSerializer.Serialize(new ServerConfiguration(), Options));
        _ = Read();
    }

    // Re-read the server-owned file, so revocation takes effect on the next command.
    public string[] Read()
    {
        var config = JsonSerializer.Deserialize<ServerConfiguration>(File.ReadAllText(ConfigurationPath), Options)
            ?? throw new InvalidOperationException("DebugPlus config.json is empty.");
        return (config.AdminProfileIds ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToArray();
    }
}
