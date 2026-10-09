using System;
using System.IO;
using System.Text;
using DebugPlus.Common;
using Fika.Core.Networking.LiteNetLib.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DebugPlus.Fika;

internal enum MessageKind { Hello, Welcome, Request, Execute, OwnerResult, Result, State, InventoryCommit, Replica, ReplicaAck }

// Only command DTOs use JSON. Native EFT item descriptors must never use this envelope's JSON codec.
public sealed class WorkbenchPacket : INetSerializable
{
    internal MessageKind Kind;
    internal string Session = "";
    internal string Version = Protocol.ModVersion;
    internal int ProtocolVersion = Protocol.Version;
    internal string Nonce = "";
    internal bool Ready;
    internal string Message = "";
    internal string Payload = "";

    public void Serialize(NetDataWriter writer)
    {
        writer.Put((byte)Kind);
        writer.Put(ProtocolVersion);
        writer.Put(Version, 32);
        writer.Put(Session, 64);
        writer.Put(Nonce, 64);
        writer.Put(Ready);
        writer.Put(Message, 1024);
        var bytes = Encoding.UTF8.GetBytes(Payload);
        if (bytes.Length > Protocol.MaxPayloadBytes) throw new InvalidDataException("DebugPlus packet exceeds the payload limit.");
        writer.Put(bytes.Length);
        writer.Put(bytes);
    }

    public void Deserialize(NetDataReader reader)
    {
        Kind = (MessageKind)reader.GetByte();
        ProtocolVersion = reader.GetInt();
        Version = reader.GetString(32);
        Session = reader.GetString(64);
        Nonce = reader.GetString(64);
        Ready = reader.GetBool();
        Message = reader.GetString(1024);
        var length = reader.GetInt();
        if (length < 0 || length > Protocol.MaxPayloadBytes || length > reader.AvailableBytes)
            throw new InvalidDataException("Invalid DebugPlus payload length.");
        var bytes = new byte[length];
        reader.GetBytes(bytes, length);
        Payload = Encoding.UTF8.GetString(bytes);
    }

    internal bool Compatible => ProtocolVersion == Protocol.Version && Version == Protocol.ModVersion;
    internal static readonly JsonSerializerSettings JsonSettings = new()
    {
        TypeNameHandling = TypeNameHandling.None,
        MaxDepth = 64,
        MetadataPropertyHandling = MetadataPropertyHandling.Ignore
    };
    internal static string Encode<T>(T value) => JsonConvert.SerializeObject(value, JsonSettings);
    internal T? Decode<T>() where T : class => JsonConvert.DeserializeObject<T>(Payload, JsonSettings);

    internal static bool DeltaMatches(string delta, CommandRequest request)
    {
        if (string.IsNullOrWhiteSpace(delta) || Encoding.UTF8.GetByteCount(delta) > Protocol.MaxPayloadBytes) return false;
        try
        {
            using var reader = new JsonTextReader(new StringReader(delta)) { MaxDepth = 64 };
            var metadata = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
            return Matches("RequestId", request.RequestId) && Matches("SessionId", request.SessionId) && Matches("TargetId", request.TargetId);
            bool Matches(string name, string expected) => metadata[name]?.Type == JTokenType.String && (string?)metadata[name] == expected;
        }
        catch (JsonException) { return false; }
    }
}

internal sealed class ReplicationMessage
{
    public string Id { get; set; } = "";
    public CommandRequest Request { get; set; } = new();
    public string Delta { get; set; } = "";
    public bool World { get; set; }
}

internal sealed class ReplicaAcknowledgement
{
    public string Id { get; set; } = "";
    public bool Success { get; set; }
}
