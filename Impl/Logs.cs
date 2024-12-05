namespace ServerWebApplication.Impl
{
    public static partial class Logs
    {
        [LoggerMessage(Level = LogLevel.Information, Message = "开始连接：{targetAddress}:{targetPort}")]
        public static partial void StartConnect(ILogger logger, string targetAddress, int targetPort);

        [LoggerMessage(Level = LogLevel.Information, Message = "成功连接：{targetAddress}:{targetPort}")]
        public static partial void SuccessConnect(ILogger logger, string targetAddress, int targetPort);

        [LoggerMessage(Level = LogLevel.Error, Message = "【WhenEach-Item】：{targetAddress}:{targetPort}\n{errorMessage}\n{stackTrace}")]
        public static partial void RunException(ILogger logger, string targetAddress, int targetPort, string errorMessage, string? stackTrace);
    }
}
