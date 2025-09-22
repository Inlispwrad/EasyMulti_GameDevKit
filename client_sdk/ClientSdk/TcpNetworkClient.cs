using System.Net.Sockets;
using System.Text.Json;

namespace EasyMultiSdk.Networking;

public class TcpNetworkClient : INetworkClient
{
    private TcpClient client;
    private NetworkStream stream;
    
    // Subscription
    private readonly Dictionary<Type, List<Delegate>> clientHandlers = new();
    private readonly Dictionary<Type, List<Delegate>> serverHandlers = new();
    
    public async Task ConnectAsync(string _host, int _port)
    {
        client = new TcpClient();
        await client.ConnectAsync(_host, _port);
        stream = client.GetStream();

        _ = Task.Run(ReceiveLoop);  
    }
    public void Disconnect()
    {
        client?.Close();
        client = null;
    }
    public void C_Send<T>(T _msgData)
    {
        var packet = new Packet
        {
            Channel = typeof(T).FullName,
            Payload = JsonSerializer.SerializeToUtf8Bytes(_msgData)
        };
        SendRaw(packet);
    }
    public void S_Send<T>(string _targetId, T _msgData)
    {
        var packet = new Packet
        {
            TargetId = _targetId,
            Channel = typeof(T).FullName,
            Payload = JsonSerializer.SerializeToUtf8Bytes(_msgData)
        };
        SendRaw(packet);
    }
    public void S_Broadcast<T>(T _msgData)
    {
        var packet = new Packet
        {
            TargetId = "*",
            Channel = typeof(T).FullName,
            Payload = JsonSerializer.SerializeToUtf8Bytes(_msgData)
        };
        SendRaw(packet);
    }
    public IDisposable C_Listen<T>(Action<T> _handler)
    {
        if (!clientHandlers.ContainsKey(typeof(T)))
            clientHandlers[typeof(T)] = new List<Delegate>();
        
        clientHandlers[typeof(T)].Add(_handler);
        return new Subscription(() => clientHandlers[typeof(T)].Remove(_handler));
    }

    public IDisposable S_Listen<T>(Action<string, T> _handler)
    {
        if(!serverHandlers.ContainsKey(typeof(T)))
            serverHandlers[typeof(T)] = new List<Delegate>();
        
        serverHandlers[typeof(T)].Add(_handler);
        return new Subscription(() => serverHandlers[typeof(T)].Remove(_handler));
    }
    
    #region private helpers
    private class Packet
    {
        /// <summary>
        /// Target Player ID
        /// - null or empty: Client sends to Host
        /// - "*"：broadcast to all clients
        /// - specific ID: Host send it to a specific Client
        /// </summary>
        public string TargetId;

        /// <summary>
        /// Msg channel（typeof(T).FullName）
        /// </summary>
        public string Channel;

        /// <summary>
        /// Serialized msg data
        /// </summary>
        public byte[] Payload;

        public Packet()
        {
            TargetId = null;                  // Default no target (client to host)
            Channel = string.Empty;           // Default empty
            Payload = Array.Empty<byte>();    // Default empty payload
        }
    }

    private class Subscription : IDisposable
    {
        private readonly Action onDisposable;
        public Subscription(Action _onDispose) => onDisposable = _onDispose;
        public void Dispose() => onDisposable();
    }
    private void SendRaw(Packet _packet)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(_packet);
        stream.Write(data, 0, data.Length);
    }
    private async Task ReceiveLoop()
    {
        var buffer = new byte[8192];
        while (client?.Connected == true)
        {
            int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
            if (bytesRead <= 0) break;
            
            var packet = JsonSerializer.Deserialize<Packet>(buffer.AsSpan(0, bytesRead));
            if(packet != null)
                Dispatch(packet);           
        }
    }
    private void Dispatch(Packet _packet)
    {
        var type = Type.GetType(_packet.Channel);
        if (type == null) return; // Dispose if the channel not found.
        
        var obj = JsonSerializer.Deserialize(_packet.Payload, type);
        if (obj == null) return; // Dispose if the msg data not found.
        
        if (string.IsNullOrEmpty(_packet.TargetId))
        {
            // peer received msg fron host
            if (clientHandlers.TryGetValue(type, out var handlers))
            {
                foreach (var handler in handlers)
                    handler.DynamicInvoke(obj);
            }
        }
        else
        {
            // Host received msg from peer
            if (serverHandlers.TryGetValue(type, out var handlers))
            {
                foreach (var handler in handlers)
                    handler.DynamicInvoke(_packet.TargetId, obj);           
            }
        }
    }
    #endregion
}