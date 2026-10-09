using DebugPlus.Common;
using SPTarkov.Server.Core.Models.Spt.Mod;

namespace DebugPlus.Server;

public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.CJ.DebugPlus.Server";
    public string Name { get; init; } = "DebugPlus Server";
    public string Author { get; init; } = "CJ";
    public SemanticVersioning.Version Version { get; init; } = new(Protocol.ModVersion);
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.0");
    public List<string>? Contributors { get; init; } = [];
    public List<string>? Incompatibilities { get; init; } = [];
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; } = [];
    public string? Url { get; init; }
    public string License { get; init; } = "See included LICENSE.txt";
    public bool? IsBundleMod { get; init; } = false;
    public bool HasPrepatcher { get; init; } = false;
}
