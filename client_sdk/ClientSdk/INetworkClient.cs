namespace EasyMultiSdk.Networking
{
    /// <summary>
    /// Abstract Net Client API, support relay and p2p
    /// </summary>
    public interface INetworkClient
    {
        /// <summary>
        /// Connect to the host (Relay or p2p)
        /// </summary>
        /// <param name="_host"></param>
        /// <param name="_port"></param>
        /// <returns></returns>
        Task ConnectAsync(string _host, int _port);

        void Disconnect();
        
        /// <summary>
        /// Send msg to host (peer only)
        /// </summary>
        /// <param name="_msgData"></param>
        /// <typeparam name="T"> Any serializable object. It will also be used as a msg channel. </typeparam>
        void C_Send<T>(T _msgData);
        
        /// <summary>
        /// Send msg to the specific peer (host only)
        /// </summary>
        /// <param name="_targetId"></param>
        /// <param name="_msgData"></param>
        /// <typeparam name="T"> Any serializable object. It will also be used as a msg channel. </typeparam>
        void S_Send<T>(string _targetId, T _msgData);
        
        /// <summary>
        /// Broadcast msg to all peers (host only)
        /// </summary>
        /// <param name="_msgData"></param>
        /// <typeparam name="T"> Any serializable object. It will also be used as a msg channel. </typeparam>
        void S_Broadcast<T>(T _msgData);
        
        /// <summary>
        /// Client listen for msg from host.
        /// </summary>
        /// <param name="_handler"></param>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        IDisposable C_Listen<T>(Action<T> _handler);
        
        /// <summary>
        /// Host listen for msg from peers.
        /// </summary>
        /// <param name="_handler"></param>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        IDisposable S_Listen<T>(Action<string, T> _handler);
    }
}