namespace EasyMultiSdk.Networking;

public class TcpNetworkClient : INetworkClient
{
    public Task ConnectAsync(string _host, int _port)
    {
        throw new NotImplementedException();
    }

    public void Disconnect()
    {
        throw new NotImplementedException();
    }

    public void C_Send<T>(T _msgData)
    {
        throw new NotImplementedException();
    }

    public void S_Send<T>(string _targetId, T _msgData)
    {
        throw new NotImplementedException();
    }

    public void S_Broadcast<T>(T _msgData)
    {
        throw new NotImplementedException();
    }

    public IDisposable C_Listen<T>(Action<T> _handler)
    {
        throw new NotImplementedException();
    }

    public IDisposable S_Listen<T>(Action<string, T> _handler)
    {
        throw new NotImplementedException();
    }
}