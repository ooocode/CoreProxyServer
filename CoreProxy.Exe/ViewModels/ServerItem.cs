using System.ComponentModel.DataAnnotations;

namespace CoreProxy.ViewModels
{
    public class ServerItem
    {
        [Range(1, 65535)]
        public int LocalListenerPort { get; set; }

        [Required]
        [Url]
        public required string ServerHttpAddress { get; set; }

        [Required]
        public required string Password { get; set; }

        [Required]
        [Range((int)RequestHttpVersion.Auto, (int)RequestHttpVersion.Version30)]
        public required RequestHttpVersion RequestHttpVersion { get; set; }

        public bool IsUseSignalR { get; set; }

        public static ServerItem Create(int localListenerPort, string serverHttpAddress, string password,
             RequestHttpVersion requestHttpVersion = RequestHttpVersion.Auto, bool isUseSignalR = false)
        {
            return new ServerItem
            {
                LocalListenerPort = localListenerPort,
                ServerHttpAddress = serverHttpAddress,
                Password = password,
                RequestHttpVersion = requestHttpVersion,
                IsUseSignalR = isUseSignalR
            };
        }
    }

    public enum RequestHttpVersion
    {
        Auto = 0,

        Version11 = 1,

        Version20 = 2,

        Version30 = 3
    }
}
