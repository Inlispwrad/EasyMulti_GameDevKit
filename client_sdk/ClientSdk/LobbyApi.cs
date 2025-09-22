namespace EasyMultiSdk.Networking;


public static class LobbyApi
{
    public static async Task RegisterHostAsync(HostConfig _config, NatInfo _natInfo)
    {
        // TODO:: Register Host to the Lobby Server (with NatInfo)
        await Task.Delay(10);
    }

    public static async Task<RelayInfo> RequestRelayAsync(HostConfig _config)
    {
        // TODO:: Request RelayInfo from the Lobby Server
        await Task.Delay(10);
        return new RelayInfo
        {
            Address = EasyMulti.SERVER_LOBBY_URL,
            Port = 9000
        };
    }

    public static async Task UpdateHostAsync(HostConfig _config)
    {
        // TODO:: Update HostInfo to the Lobby Server
        await Task.Delay(10);
    }

    public static async Task<IEnumerable<HostConfig>> GetHostListAsync()
    {
        // TODO:: Request HostList from the Lobby Server
        await Task.Delay(10);
        return new List<HostConfig>();
    }

    public static async Task<HostInfo> GetHostInfoAsync(HostId _hostId)
    {
        // TODO:: Request a specific HostInfo from the Lobby Server
        await Task.Delay(10);
        return new HostInfo
        {
            HostId = _hostId,
            RelayInfo = new RelayInfo
            {
                Address = EasyMulti.SERVER_LOBBY_URL,
                Port = 9000
            }
        };
    }
}

public class RelayInfo
{
    public string Address { get; set; } = "";
    public int Port { get; set; }
}
    
public sealed class HostInfo
{
    public HostId HostId { get; set; }
    
    public string GameId { get; set; } = "";
    public byte Capacity { get; set; }
    public byte CurrentPlayers { get; set; }
        
    public RelayInfo? RelayInfo { get; set; }  
    public IReadOnlyDictionary<string, string>? Meta { get; set; }
}