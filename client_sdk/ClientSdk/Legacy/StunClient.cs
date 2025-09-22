// using System.Net;
// using System.Net.Sockets;
//
// namespace EasyMultiSdk.Networking;
//
//
// public class NatInfo
// {
//     public string PublicIp { get; set; }
//     public int PublicPort { get; set; }
//     public string LocalIp { get; set; }
//     public int LocalPort { get; set; }
//     public string NatType { get; set; }
//     public override string ToString()
//     {
//         return $"IP: {PublicIp}, Port: {PublicPort}, Type: {NatType}";
//     }
//
// }
//
// public static class StunClient
// {
//     public static NatInfo? QueryNat(int _localPort)
//     {
//         var stunServer = new IPEndPoint(
//             Dns.GetHostAddresses("stun.l.google.com")
//                 .First(ip => ip.AddressFamily == AddressFamily.InterNetwork),
//             19302
//         );
//
//         using var udpClient = new UdpClient(_localPort);
//         var request = BuildStunRequest();
//         udpClient.Send(request, request.Length, stunServer);
//         
//         var response = udpClient.Receive(ref stunServer);
//         
//         var info = ParseStunResponse(response);
//         if (info != null)
//         {
//             info.LocalIp = ((IPEndPoint)udpClient.Client.LocalEndPoint!).Address.ToString();
//             info.LocalPort = ((IPEndPoint)udpClient.Client.LocalEndPoint!).Port;
//         }
//         return info;
//     }
//
//     private static byte[] BuildStunRequest()
//     {
//         var buffer = new byte[20];
//         buffer[0] = 0x00; buffer[1] = 0x01; // Binding Request
//         buffer[2] = 0x00; buffer[3] = 0x00; // Message Length = 0
//         buffer[4] = 0x21; buffer[5] = 0x12; buffer[6] = 0xA4; buffer[7] = 0x42; // Magic Cookie
//
//         var rnd = new Random();
//         for (int i = 8; i < 20; i++)
//             buffer[i] = (byte)rnd.Next(0, 256); // Transaction ID
//
//         return buffer;
//     }
//
//     private static NatInfo? ParseStunResponse(byte[] _response)
//     {
//         Console.WriteLine("尝试解析 STUN 响应...");
//         Console.WriteLine("响应长度: " + _response.Length);
//         
//         const int _HEADER_LENGTH = 20;
//         const ushort _XOR_MAPPED_ADDRESS_TYPE = 0x0020;
//         const int _MAGIC_COOKIE = 0x2112A442;
//
//         int offset = _HEADER_LENGTH;
//         while (offset + 4 <= _response.Length)
//         {
//             ushort type = (ushort)((_response[offset] << 8) | _response[offset + 1]);
//             ushort length = (ushort)((_response[offset + 2] << 8) | _response[offset + 3]);
//             offset += 4;
//
//             if (type == _XOR_MAPPED_ADDRESS_TYPE)
//             {
//                 byte family = _response[offset + 1];
//                 ushort xPort = (ushort)((_response[offset + 2] << 8) | _response[offset + 3]);
//                 int port = xPort ^ (_MAGIC_COOKIE >> 16);
//                 
//                 byte[] ipBytes = new byte[4];
//                 for (int i = 0; i < 4; i++)
//                 {
//                     ipBytes[i] = (byte)(_response[offset + 4 + i] ^ ((_MAGIC_COOKIE >> ((3 - i) * 8)) & 0xFF));
//                 }
//                 
//                 string ip = new IPAddress(ipBytes).ToString();
//                 
//                 Console.WriteLine("属性类型: " + type.ToString("X4"));
//                 Console.WriteLine("属性长度: " + length);
//                 Console.WriteLine("解析出的 IP: " + ip);
//                 Console.WriteLine("解析出的端口: " + port);
//
//                 return new NatInfo
//                 {
//                     PublicIp = ip,
//                     PublicPort = port,
//                     NatType = "Unknown"
//                 };
//             }
//             offset += length;
//         }
//
//         return null;
//     }
//     
//     public static NatInfo DetectNatType(int localPort)
//     {
//         var servers = new[] {
//             "stun.l.google.com",
//             "stun1.l.google.com",
//             "stun2.l.google.com",
//             "stun.stunprotocol.org"
//         };
//
//         var results = new List<NatInfo>();
//
//         foreach (var host in servers)
//         {
//             try
//             {
//                 var ip = Dns.GetHostAddresses(host).First(ip => ip.AddressFamily == AddressFamily.InterNetwork);
//                 var endpoint = new IPEndPoint(ip, 19302);
//
//                 using var udpClient = new UdpClient(localPort);
//                 var request = BuildStunRequest();
//                 udpClient.Send(request, request.Length, endpoint);
//
//                 var response = udpClient.Receive(ref endpoint);
//                 var info = ParseStunResponse(response);
//
//                 if (info != null)
//                 {
//                     info.LocalIp = ((IPEndPoint)udpClient.Client.LocalEndPoint!).Address.ToString();
//                     info.LocalPort = ((IPEndPoint)udpClient.Client.LocalEndPoint!).Port;
//                     results.Add(info);
//                 }
//             }
//             catch
//             {
//                 // 忽略异常
//             }
//         }
//
//         if (results.Count >= 2)
//         {
//             var first = results[0];
//             var second = results[1];
//
//             if (first.PublicIp != second.PublicIp || first.PublicPort != second.PublicPort)
//             {
//                 return new NatInfo
//                 {
//                     PublicIp = first.PublicIp,
//                     PublicPort = first.PublicPort,
//                     LocalIp = first.LocalIp,
//                     LocalPort = first.LocalPort,
//                     NatType = "Symmetric NAT"
//                 };
//             }
//             else
//             {
//                 return new NatInfo
//                 {
//                     PublicIp = first.PublicIp,
//                     PublicPort = first.PublicPort,
//                     LocalIp = first.LocalIp,
//                     LocalPort = first.LocalPort,
//                     NatType = "Cone NAT"
//                 };
//             }
//         }
//
//         return new NatInfo { PublicIp = "", PublicPort = 0, NatType = "Detection Failed" };
//     }
// }