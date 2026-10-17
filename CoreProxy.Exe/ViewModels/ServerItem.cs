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

        public static ServerItem Create(int localListenerPort, string serverHttpAddress, string password)
        {
            return new ServerItem
            {
                LocalListenerPort = localListenerPort,
                ServerHttpAddress = serverHttpAddress,
                Password = password,
            };
        }
    }
}
