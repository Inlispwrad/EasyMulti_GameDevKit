namespace EasyMultiSdk.Networking;

#region IDs

public struct Identify
{
    public enum NetType
    {
        Host,
        Peer
    };

    public NetType Type;
    
    
    public Identify(NetType _type)
    {
        Type = _type;
    }
}

public readonly struct HostId
{
    public string Value { get; }
    public HostId(string _value) => Value = _value ?? throw new ArgumentNullException(nameof(_value));
    public override string ToString() => Value;
}
public readonly struct PeerId
{
    public Guid Value { get; }
    public PeerId(Guid _value) => Value = _value;
    public override string ToString() => Value.ToString();
}
#endregion


#region Host



#endregion