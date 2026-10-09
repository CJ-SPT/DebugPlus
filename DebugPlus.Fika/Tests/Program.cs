using System;
using System.IO;
using DebugPlus.Common;
using DebugPlus.Fika;
using Fika.Core.Networking.LiteNetLib.Utils;
using Newtonsoft.Json.Linq;

var checks = 0;
void Check(bool success, string message) { if (!success) throw new Exception(message); checks++; }
void Reject(Action action, string message)
{
    try { action(); }
    catch (InvalidDataException) { checks++; return; }
    throw new Exception(message);
}

var request = new CommandRequest { SessionId = Guid.NewGuid().ToString("N"), ActorId = "administrator", TargetId = "owner", Kind = CommandKind.SpawnItem };
var clear = new CommandRequest { SessionId = request.SessionId, ActorId = request.ActorId, TargetId = request.TargetId, Kind = CommandKind.EquipKit,
    Args = new() { ClearEquipment = true, ConfirmReplacement = true, ExpectedInventoryHash = "preview" } };
var clearPacket = new WorkbenchPacket { Kind = MessageKind.Execute, Session = clear.SessionId, Payload = WorkbenchPacket.Encode(clear) };
Check(clearPacket.Decode<CommandRequest>() is { Args.ClearEquipment: true, Args.Items.Count: 0 },
    "Empty equipment replacement intent survives multiplayer serialization.");
var packet = new WorkbenchPacket { Kind = MessageKind.Execute, Session = request.SessionId, Nonce = Guid.NewGuid().ToString("N"), Payload = WorkbenchPacket.Encode(request), Ready = true, Message = "Ready ✓" };
var writer = new NetDataWriter();
packet.Serialize(writer);
var reader = new NetDataReader(writer.CopyData());
var decoded = new WorkbenchPacket();
decoded.Deserialize(reader);
Check(decoded.Compatible && decoded.Kind == MessageKind.Execute && decoded.Session == packet.Session && decoded.Nonce == packet.Nonce && decoded.Ready && decoded.Message == packet.Message && reader.AvailableBytes == 0, "Wire envelope did not round-trip.");
var decodedRequest = decoded.Decode<CommandRequest>();
Check(decodedRequest?.RequestId == request.RequestId && decodedRequest.ActorId == request.ActorId && decodedRequest.TargetId == request.TargetId, "Command identity did not round-trip.");
decoded.Version = "1.0.0";
Check(!decoded.Compatible, "Incompatible version accepted.");
decoded.Version = Protocol.ModVersion;
decoded.ProtocolVersion++;
Check(!decoded.Compatible, "Incompatible protocol accepted.");

packet.Payload = new string('a', Protocol.MaxPayloadBytes + 1);
Reject(() => packet.Serialize(new NetDataWriter()), "Oversized outgoing payload accepted.");
void Malformed(int length, int actual)
{
    var data = new NetDataWriter();
    data.Put((byte)MessageKind.Request); data.Put(Protocol.Version);
    data.Put(Protocol.ModVersion, 32); data.Put(request.SessionId, 64); data.Put("", 64); data.Put(false); data.Put("", 1024);
    data.Put(length); data.Put(new byte[actual]);
    new WorkbenchPacket().Deserialize(new NetDataReader(data.CopyData()));
}
Reject(() => Malformed(-1, 0), "Negative allocation accepted.");
Reject(() => Malformed(Protocol.MaxPayloadBytes + 1, 0), "Oversized incoming allocation accepted.");
Reject(() => Malformed(10, 1), "Truncated incoming payload accepted.");

var delta = new JObject { ["SessionId"] = request.SessionId, ["RequestId"] = request.RequestId, ["TargetId"] = request.TargetId, ["Roots"] = new JArray() };
Check(WorkbenchPacket.DeltaMatches(delta.ToString(), request), "Matching inventory delta rejected.");
foreach (var key in new[] { "SessionId", "RequestId", "TargetId" })
{
    var changed = (JObject)delta.DeepClone(); changed[key] = "different";
    Check(!WorkbenchPacket.DeltaMatches(changed.ToString(), request), "Mismatched " + key + " accepted.");
}
Check(!WorkbenchPacket.DeltaMatches("[]", request) && !WorkbenchPacket.DeltaMatches("not JSON", request), "Malformed inventory metadata accepted.");
var invalidType = (JObject)delta.DeepClone(); invalidType["TargetId"] = new JObject();
Check(!WorkbenchPacket.DeltaMatches(invalidType.ToString(), request), "Object-valued identity accepted.");
Check(!WorkbenchPacket.DeltaMatches("{\"RequestId\":\"one\",\"RequestId\":\"two\"}", request), "Duplicate identity fields accepted.");
Console.WriteLine($"Passed {checks} Fika packet and inventory identity checks.");
