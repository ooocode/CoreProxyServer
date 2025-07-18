namespace CoreProxy.Server.Lib.Options
{
    public class TransportOptions
    {
        /// <summary>
        /// 每次最大4096个字节传输
        /// </summary>
        public bool UseMax4096Bytes { get; set; }

        /// <summary>
        /// 启用数据加密
        /// </summary>
        public bool EnableDataEncrypt { get; set; }
    }
}
