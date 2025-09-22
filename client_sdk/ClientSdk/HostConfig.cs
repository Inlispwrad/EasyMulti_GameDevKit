
namespace EasyMultiSdk.Networking;

public class HostConfig
{
    public enum Protocol : Byte
    {
        Udp = 0, Tcp, Kcp, 
        Websocket, SocketIo
    }
    
    // Host identity (Optional)
    public string? HostId { get; set; }

    // Protocols
    public Protocol ProtocolType { get; set; } = Protocol.Udp;
    public int Port { get; set; } = 0; // 0 means dynamic port

    // Game Room Settings
    public string GameCode { get; set; } = "";
    public int Capacity { get; set; } = 2;
    public string Password { get; set; } = ""; // Placehold

    // Meta
    public IReadOnlyDictionary<string, string>? Meta { get; set; }

    public void Set(HostConfig _config)
    {
        ProtocolType = _config.ProtocolType;
        Port = _config.Port;
        GameCode = _config.GameCode;
        Capacity = _config.Capacity;
        Password = _config.Password;
        Meta = _config.Meta?.ToDictionary(_kv => _kv.Key, _kv => _kv.Value);
    }
}