using System.Threading.Tasks;
using Comfort.Common;
using EFT;
using EFT.Console.Core;
using EFT.UI;

namespace DebugPlus.ConsoleCommands;

[ConsoleCommand("ReloadFromServer", "", "", "Reloads all data from server")]
public class ReloadFromServerAsync : AsyncCommand
{
    public override object[] ArgumentsValue => [];
        
    public override async Task Execute()
    {
        var runtime = DebugPlus.Runtime.WorkbenchRuntime.Instance;
        if (!runtime) { ConsoleScreen.LogError("DebugPlus is not ready."); return; }
        var result = await runtime.Submit(DebugPlus.Common.CommandKind.ReloadBackend, new DebugPlus.Common.CommandArguments());
        if (result.Status == DebugPlus.Common.CommandStatus.Completed) ConsoleScreen.Log(result.Message);
        else ConsoleScreen.LogError(result.Message);
    }
}