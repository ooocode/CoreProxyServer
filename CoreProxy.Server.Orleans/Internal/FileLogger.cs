using System.Threading.Channels;

namespace CoreProxy.Server.Orleans.Internal
{
    public record LogEntry(
            string CategoryName,
            LogLevel Level,
            EventId EventId,
            string Message,
            DateTimeOffset Timestamp);

    public class FileLogger(string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel >= LogLevel.Information;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId,
            TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            FileLogBackgroundService._channel.Writer.TryWrite(new LogEntry(
                categoryName,
                logLevel,
                eventId,
                message,
                DateTimeOffset.Now));
        }
    }

    public class FileLoggerProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName)
        {
            return new FileLogger(categoryName);
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }
    }

    public class FileLogBackgroundService : BackgroundService
    {
        public static readonly Channel<LogEntry> _channel = Channel.CreateUnbounded<LogEntry>();
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            string dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            await foreach (var item in _channel.Reader.ReadAllAsync(CancellationToken.None))
            {
                string logFile = Path.Combine(dir, $"log_{item.Timestamp:yyyyMMdd}_{Environment.ProcessId}.txt");
                var logMessage = $"{item.Timestamp:HH:mm:ss} {item.Level}: {item.CategoryName}[{item.EventId.Id}]{Environment.NewLine}" +
                    $"             {item.Message}{Environment.NewLine}";
                File.AppendAllText(logFile, logMessage);

                if (item.Level >= LogLevel.Error)
                {
                    string errorLogFile = Path.Combine(dir, $"error_{item.Timestamp:yyyyMMdd}_{Environment.ProcessId}.txt");
                    File.AppendAllText(errorLogFile, logMessage);
                }
            }
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _channel.Writer.Complete();
            return base.StopAsync(cancellationToken);
        }
    }


    public static class FileLogExtensions
    {
        public static ILoggingBuilder AddFileLogger(this ILoggingBuilder builder)
        {
            builder.Services.AddSingleton<ILoggerProvider, FileLoggerProvider>();
            builder.Services.AddHostedService<FileLogBackgroundService>();
            return builder;
        }
    }
}
