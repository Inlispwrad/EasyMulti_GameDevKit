using EasyMultiSdk.Networking;

namespace EasyMultiSdk;

public static class EasyMulti
{
    // Lobby Server
    public const string SERVER_LOBBY_URL = "127.0.0.1";
    
    public static readonly HostConfig CachedHostConfig = new();

    private static INetworkClient? netConnection;
    
    #region Connect API
    public static async void Host(HostConfig? _config = null)
    {
        if(_config != null)
            CachedHostConfig.Set(_config);
        
        var natInfo = await NatHelper.TryGetNatInfoAsync();
        if (natInfo.Success)
        {
            // NAT success -> Upload Nat Info
            await LobbyApi.RegisterHostAsync(CachedHostConfig, natInfo);
        }
        else
        {
            // NAT failed -> Request Relay
            var relayInfo = await LobbyApi.RequestRelayAsync(CachedHostConfig);
            await ConnectToRelayAsync(relayInfo);
        }
    }
    public static async void UpdateHost(HostConfig _newConfig)
    {
        CachedHostConfig.Set(_newConfig);
        await LobbyApi.UpdateHostAsync(_newConfig);
    }
    public static async Task<IEnumerable<HostConfig>> QueryHosts()
    {
        return await LobbyApi.GetHostListAsync();
    }
    public static async void Join(string _hostId)
    {
        var hostInfo = await LobbyApi.GetHostInfoAsync(_hostId);

        if (hostInfo.RelayInfo != null)
        {
            await ConnectToRelayAsync(hostInfo.RelayInfo);
        }
        else
        {
            // TODO:: If NAT success -> Connect to p2p
            throw new NotImplementedException("P2P Not Implemented Yet");
        }
    }
    public static void Leave()
    {
        netConnection?.Disconnect();
        netConnection = null;
    }
    #endregion
    
    #region Client API
    public static IDisposable C_Listen<T>(Action<T> _handler)
    {
        return netConnection?.C_Listen(_handler) ?? new DummySubscription();
    }
    public static void C_Send<T>(T _msgData)
    {
        netConnection?.C_Send(_msgData);   
    }
    #endregion
    
    #region Server Only API
    public static IDisposable S_Listen<T>(Action<string, T> _handler)
    {
        return netConnection?.S_Listen(_handler) ?? new DummySubscription();
    }
    public static void S_Send<T>(string _targetPlayerId, T _msgData)
    {
        netConnection?.S_Send(_targetPlayerId, _msgData);  
    }
    public static void S_Broadcast<T>(T _msgData)
    {
        netConnection?.S_Broadcast(_msgData); 
    }
    #endregion
    
    
    #region Private Helpers
    private static async Task ConnectToRelayAsync(RelayInfo _relayInfo)
    {
        netConnection = new TcpNetworkClient(); // TODO:: Use TCP for relay for now.
        await netConnection.ConnectAsync(_relayInfo.Address, _relayInfo.Port);
    }
    private class DummySubscription : IDisposable
    {
        public void Dispose() { }
    }
    #endregion

}