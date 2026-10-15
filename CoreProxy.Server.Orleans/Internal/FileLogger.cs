using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace CoreProxy.Server.Orleans.Internal
{
    /// <summary>
    /// 一条待落盘的日志记录。
    /// <para>格式化与写盘分离：这里只保存原始字段，行文本由 <see cref="FileLogWriter"/> 生成，
    /// 业务线程因此不做任何字符串拼接，日志开销恒定在一次入队。</para>
    /// </summary>
    public sealed record LogEntry(
        string CategoryName,
        LogLevel Level,
        EventId EventId,
        string Message,
        string? ExceptionText,
        DateTimeOffset Timestamp);

    /// <summary>文件日志配置。</summary>
    public sealed class FileLoggerOptions
    {
        /// <summary>低于此级别的日志直接丢弃。默认与旧实现一致为 Information。</summary>
        public LogLevel MinimumLevel { get; set; } = LogLevel.Information;

        /// <summary>日志根目录；为 null 或空时使用 <c>AppDomain.BaseDirectory/logs</c>。</summary>
        public string? Directory { get; set; }

        /// <summary>缓冲区最长滞留时间，到期强制 flush。设为 <see cref="TimeSpan.Zero"/> 表示每条都 flush。</summary>
        public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(1);

        /// <summary>待写队列容量。写盘跟不上时丢弃新日志，而不是把内存撑爆。</summary>
        public int QueueCapacity { get; set; } = 10_000;
    }

    /// <summary>
    /// 日志入队通道，在日志线程（写）与后台服务（读）之间共享。
    /// <para>有界队列是刻意的：磁盘卡住或故障时，宁可丢日志也不能让业务线程阻塞或内存无界增长。
    /// 旧实现用的是无界通道，一旦落盘速度跟不上就会持续吃内存。</para>
    /// </summary>
    public sealed class FileLogQueue
    {
        private readonly Channel<LogEntry> channel;
        private long dropped;

        public FileLogQueue(int capacity)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

            channel = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                SingleWriter = false,
                // FullMode.Wait 配合 TryWrite：满时既不阻塞调用方，又如实返回 false，丢弃量因此可精确统计。
                // 注意不可用 DropWrite/DropOldest——那两种模式下 TryWrite 会返回 true，丢弃会被静默吞掉。
                FullMode = BoundedChannelFullMode.Wait
            });
        }

        /// <summary>因队列满而被丢弃的日志条数（诊断与测试用）</summary>
        public long DroppedCount => Interlocked.Read(ref dropped);

        /// <summary>尝试入队；队列满返回 false 并累加丢弃计数，绝不阻塞调用线程。</summary>
        public bool TryEnqueue(LogEntry entry)
        {
            if (channel.Writer.TryWrite(entry))
            {
                return true;
            }

            Interlocked.Increment(ref dropped);
            return false;
        }

        public ChannelReader<LogEntry> Reader => channel.Reader;

        /// <summary>停止接收新日志，读取端会在消费完剩余条目后自然结束。</summary>
        public void Complete() => channel.Writer.TryComplete();
    }

    /// <summary>
    /// 文件日志写入器：按天分目录、按天分文件，长期持有文件句柄并缓冲写入。
    /// <para>旧实现每条日志都调用一次 <c>File.AppendAllTextAsync</c>，即一次 open/write/close；
    /// 高吞吐下这部分开销直接落在日志线程上，且每次都会分配一个文件流与编码器。
    /// 这里改为常开 <see cref="StreamWriter"/> + 定期 flush，写盘成本摊销到批量。</para>
    /// </summary>
    public sealed class FileLogWriter : IDisposable
    {
        private readonly string rootDirectory;
        private readonly int processId;
        private readonly Lock gate = new();
        private readonly StringBuilder builder = new();

        private DateOnly currentDate;
        private StreamWriter? logWriter;
        private StreamWriter? errorWriter;
        private bool disposed;

        /// <param name="directory">日志根目录；null/空表示 <c>AppDomain.BaseDirectory/logs</c></param>
        /// <param name="processId">用于区分同机多实例的文件名后缀</param>
        public FileLogWriter(string? directory, int processId)
        {
            rootDirectory = string.IsNullOrWhiteSpace(directory)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs")
                : directory;
            this.processId = processId;
        }

        /// <summary>
        /// 写入一条日志。目录随条目时间戳切换，跨零点后新条目进入新一天的目录。
        /// <para>整个写盘过程（含建目录、开文件）的 I/O 失败都被吞掉：磁盘满或文件被占用
        /// 只能让这一条日志丢失，绝不能让后台服务退出、连带后续所有日志一起丢。</para>
        /// </summary>
        public void Write(LogEntry entry)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            var date = DateOnly.FromDateTime(entry.Timestamp.DateTime);
            lock (gate)
            {
                try
                {
                    //旧实现把目录固定在服务启动那一刻，进程跨零点后新条目会被写进前一天(启动日)的目录，文件名却已是新日期
                    if (date != currentDate)
                    {
                        CloseWriters();
                        currentDate = date;
                    }

                    AppendLine(GetLogWriter(date), entry);

                    if (entry.Level >= LogLevel.Error)
                    {
                        AppendLine(GetErrorWriter(date), entry);
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        /// <summary>把缓冲区刷到磁盘。写失败同样只吞掉，不让 flush 循环挂掉。</summary>
        public void Flush()
        {
            lock (gate)
            {
                try
                {
                    logWriter?.Flush();
                    errorWriter?.Flush();
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                CloseWriters();
            }
        }

        /// <summary>复用同一个 StringBuilder 渲染后写出，避免每条日志都分配一个临时字符串。</summary>
        private void AppendLine(StreamWriter writer, LogEntry entry)
        {
            builder.Clear();
            Format(builder, entry);
            writer.Write(builder);
        }

        /// <summary>渲染一条日志的完整文本（含结尾换行），供写入与测试使用。</summary>
        public static string FormatToString(LogEntry entry) =>
            Format(new StringBuilder(), entry).ToString();

        /// <summary>渲染一条日志的完整文本（含结尾换行），供写入与测试使用。</summary>
        public static StringBuilder Format(StringBuilder builder, LogEntry entry)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(entry);

            builder.Append(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture));
            builder.Append(' ');
            builder.Append(entry.Level);
            builder.Append(": ");
            builder.Append(entry.CategoryName);
            builder.Append('[');
            builder.Append(entry.EventId.Id.ToString(CultureInfo.InvariantCulture));
            builder.Append(']');
            builder.AppendLine();

            AppendIndented(builder, entry.Message);

            //异常带堆栈的完整文本；旧实现只存 exception.Message，错误现场基本无法定位
            if (!string.IsNullOrEmpty(entry.ExceptionText))
            {
                AppendIndented(builder, entry.ExceptionText);
            }

            return builder;
        }

        /// <summary>正文与异常都按 4 空格缩进，且多行文本（堆栈）逐行缩进，避免顶格串行。</summary>
        private static void AppendIndented(StringBuilder builder, string text)
        {
            var start = 0;
            while (start <= text.Length)
            {
                var end = text.IndexOf('\n', start);
                var lineEnd = end < 0 ? text.Length : end;
                var length = lineEnd - start;

                //兼容 \r\n
                if (length > 0 && text[lineEnd - 1] == '\r')
                {
                    length--;
                }

                builder.Append("    ");
                builder.Append(text, start, length);
                builder.AppendLine();

                if (end < 0)
                {
                    break;
                }

                start = end + 1;
            }
        }

        private StreamWriter GetLogWriter(DateOnly date) =>
            logWriter ??= OpenWriter(Path.Combine(GetDirectory(date), $"log_{date:yyyyMMdd}_{processId}.txt"));

        private StreamWriter GetErrorWriter(DateOnly date) =>
            errorWriter ??= OpenWriter(Path.Combine(GetDirectory(date), $"error_{date:yyyyMMdd}_{processId}.txt"));

        private static StreamWriter OpenWriter(string path)
        {
            //目录按需创建；FileStream 不会替我们建目录，缺目录会直接抛 DirectoryNotFoundException
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            //UTF-8 无 BOM，与旧的 File.AppendAllTextAsync 默认编码保持一致
            return new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false };
        }

        private string GetDirectory(DateOnly date) =>
            Path.Combine(rootDirectory, date.Year.ToString(CultureInfo.InvariantCulture),
                date.Month.ToString(CultureInfo.InvariantCulture),
                date.Day.ToString(CultureInfo.InvariantCulture));

        private void CloseWriters()
        {
            logWriter?.Dispose();
            logWriter = null;
            errorWriter?.Dispose();
            errorWriter = null;
        }
    }

    public sealed class FileLogger(
        string categoryName,
        FileLogQueue queue,
        FileLoggerOptions options,
        TimeProvider timeProvider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return logLevel >= options.MinimumLevel;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId,
            TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            //旧实现不做这一层判断，直接依赖框架过滤；这里补上，避免绕过框架直接调用 ILogger 时写入被禁用的日志
            if (!IsEnabled(logLevel))
            {
                return;
            }

            queue.TryEnqueue(new LogEntry(
                categoryName,
                logLevel,
                eventId,
                formatter(state, exception),
                exception?.ToString(),
                //统一用本地时间：旧实现条目取 Now、目录取 UtcNow，两者会互相错位
                timeProvider.GetLocalNow()));
        }
    }

    public sealed class FileLoggerProvider(
        FileLogQueue queue,
        FileLoggerOptions options,
        TimeProvider timeProvider) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName)
        {
            return new FileLogger(categoryName, queue, options, timeProvider);
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// 后台落盘服务：把队列里的日志批量写入磁盘。
    /// <para>消费与 flush 分成两个循环：消费循环按到达顺序写缓冲，flush 循环按
    /// <see cref="FileLoggerOptions.FlushInterval"/> 定时落盘。这样即使日志突然静默，
    /// 缓冲区里的内容也一定会被刷出去，而不用等下一行日志到来。</para>
    /// <para><b>停机保证</b>：<see cref="StopAsync"/> 返回后，<see cref="FileLogQueue"/> 中
    /// 已入队的日志一定都已写盘并 flush。<see cref="StopAsync"/> 不依赖后台循环是否已经跑起来——
    /// .NET 10 的 <see cref="BackgroundService.StartAsync"/> 会把 <see cref="ExecuteAsync"/> 丢到线程池，
    /// 紧随其后的 StopAsync 完全可能赶在循环启动前就把 token 取消掉（实测如此），
    /// 此时若只靠循环消费，缓冲里的日志会被整批静默丢弃。</para>
    /// </summary>
    public sealed class FileLogBackgroundService(
        FileLogQueue queue,
        FileLoggerOptions options) : BackgroundService
    {
        private readonly FileLogWriter writer = new(options.Directory, Environment.ProcessId);
        private readonly TimeSpan flushInterval = options.FlushInterval;

        /// <summary>后台循环上最后一次未预期的异常（诊断用）。日志系统自身的故障不允许冒泡成宿主故障：
        /// ExecuteAsync 抛异常会被 BackgroundService 当作致命错误，直接停掉整个宿主。</summary>
        public Exception? LastFault { get; private set; }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            try
            {
                await Task.WhenAll(
                    DrainAsync(),
                    FlushAsync(stoppingToken));
            }
            catch (Exception ex)
            {
                LastFault = ex;
            }
        }

        private async Task DrainAsync()
        {
            //不传 stoppingToken：停机先 Complete 队列，这里要把剩余条目读完再退出。
            //读与写之间的竞争由 StopAsync 兜底：它一定在 ExecuteAsync 结束之后才做最终收尾。
            await foreach (var entry in queue.Reader.ReadAllAsync(CancellationToken.None))
            {
                writer.Write(entry);

                //未启用定时 flush 时退化为每条落盘，保证语义不弱于旧实现
                if (flushInterval <= TimeSpan.Zero)
                {
                    writer.Flush();
                }
            }
        }

        private async Task FlushAsync(CancellationToken stoppingToken)
        {
            if (flushInterval <= TimeSpan.Zero)
            {
                return;
            }

            using var timer = new PeriodicTimer(flushInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(stoppingToken))
                {
                    writer.Flush();
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            //先停止接收新日志，再让后台循环（若已启动）把剩余条目消费掉
            queue.Complete();
            await base.StopAsync(cancellationToken);

            //兜底收尾：base.StopAsync 返回后 ExecuteAsync 一定已结束，此时能安全独占 writer。
            //若后台循环从未真正跑起来，上面一步不会消费任何条目，这里把队列里剩下的全部写完。
            while (queue.Reader.TryRead(out var entry))
            {
                writer.Write(entry);
            }

            writer.Dispose(); //内含最终 flush，保证停机前的日志不丢
        }

        public override void Dispose()
        {
            writer.Dispose();
            base.Dispose();
        }
    }

    public static class FileLogExtensions
    {
        public static ILoggingBuilder AddFileLogger(this ILoggingBuilder builder)
        {
            return builder.AddFileLogger(static _ => { });
        }

        public static ILoggingBuilder AddFileLogger(this ILoggingBuilder builder, Action<FileLoggerOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configure);

            var options = new FileLoggerOptions();
            configure(options);

            //队列被 provider（写）与后台服务（读）共享，必须是同一个单例
            builder.Services.AddSingleton(new FileLogQueue(options.QueueCapacity));
            builder.Services.AddSingleton(options);
            //让测试/宿主注入的 TimeProvider 优先，没有才用系统时钟
            builder.Services.TryAddSingleton(TimeProvider.System);
            builder.Services.AddSingleton<ILoggerProvider, FileLoggerProvider>();
            builder.Services.AddHostedService<FileLogBackgroundService>();
            return builder;
        }
    }
}
