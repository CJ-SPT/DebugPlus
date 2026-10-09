using DebugPlus.Common;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Utils;

namespace DebugPlus.Server;

[Injectable]
public sealed class DebugRouter(JsonUtil json, DebugService service) : StaticRouter(json,
[
    new RouteAction<EmptyRequestData>("/debugplus/access", (_, _, id, _, _) =>
        ValueTask.FromResult(json.Serialize(service.Access(id))!)),
    new RouteAction<ServerCommandRequest>("/debugplus/menu/preview", async (_, request, id, _, _) =>
        json.Serialize(await service.Preview(id, request))!),
    new RouteAction<ServerCommandRequest>("/debugplus/menu/execute", async (_, request, id, _, _) =>
        json.Serialize(await service.Execute(id, request))!),
    new RouteAction<ServerMenuInventorySync>("/debugplus/menu/sync", async (_, request, id, _, _) =>
        json.Serialize(await service.Synchronize(id, request))!)
]);

public sealed class ServerCommandRequest : CommandRequest, IRequestData;
public sealed class ServerMenuInventorySync : MenuInventorySync, IRequestData;
