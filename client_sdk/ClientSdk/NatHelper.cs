namespace EasyMultiSdk.Networking
{
    public static class NatHelper
    {
        /// <summary>
        /// Try to get NAT info (STUN / UDP Hole Punching)
        /// </summary>
        /// <returns> NatInfo for Lobby Server </returns>
        public static async Task<NatInfo> TryGetNatInfoAsync()
        {
            await Task.Delay(10);
            return new NatInfo
            {
                Success = false,    // TODO:: False for now, go mid-point
                NatType = "Unknown",
                PublicIp = null,
                PublicPort = 0
            };
        }
    }
    public class NatInfo
    {
        public bool Success { get; set; }
        public string NatType { get; set; }
        public string PublicIp { get; set; }
        public int PublicPort { get; set; }
    }
}