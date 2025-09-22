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
        
        throw new NotImplementedException();
    }

    public static void UpdateHost(HostConfig _newConfig)
    {
        throw new NotImplementedException();
    }
    public static IEnumerable<HostConfig> QueryHosts()
    {
        throw new NotImplementedException();
    }
    public static void Join(string _hostId)
    {
        throw new NotImplementedException();
    }
    public static void Leave()
    {
        throw new NotImplementedException();
    }
    #endregion
    
    #region Client API
    public static object C_Listen<T>(Action<T> _onReceived)
    {
        throw new NotImplementedException();
    }
    public static void C_Send<T>(T _msgData)
    {
        throw new NotImplementedException();
    }
    #endregion
    
    #region Server Only API
    public static void S_Listen<T>(Action<string, T> _onReceived)
    {
        throw new NotImplementedException();
    }
    public static void S_Send<T>(string _targetPlayerId, T _msgData)
    {
        throw new NotImplementedException();
    }
    public static void S_Broadcast<T>(T _msgData)
    {
        throw new NotImplementedException();
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