using System.Net;
using System.Net.Sockets;
using System.Text;

namespace EasyMulti.LobbyServer;

public class UdpEchoServer
{
    public static void Start(int _listenPort)
    {
        using var udpClient = new UdpClient(_listenPort);
        Console.WriteLine($"UDP Server listening on port {_listenPort}...");

        var remoteEp = new IPEndPoint(IPAddress.Any, 0);
        while (true)
        {
            var data = udpClient.Receive(ref remoteEp);
            var message = Encoding.UTF8.GetString(data);
            Console.WriteLine($"Received from {remoteEp}: {message}");
        }
    }
}