using CoreProxy.Server.Orleans.Internal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CoreProxy.Server.Orleans.Tests
{
    /// <summary>
    /// FileLogger 与文件落盘的单元测试。
    /// <para>全部使用注入的替身时钟与临时目录，把"时间推进"和"磁盘位置"都变成确定性输入，结果可复现，不依赖真实时钟或随机目录。</para>
    /// </summary>
    [TestClass]
    public sealed class FileLoggerTests
    {
        /// <summary>手动推进的本地时钟，用来确定性地制造不同日志时间。</summary>
        private sealed class ManualTimeProvider : TimeProvider
        {
            //GetLocalNow() 不是虚方法，它由 GetUtcNow() + LocalTimeZone 推导，所以这里固定一个 +08:00 时区
            private static readonly TimeZoneInfo Tz =
                TimeZoneInfo.CreateCustomTimeZone("test-utc+8", TimeSpan.FromHours(8), "test-utc+8", "test-utc+8");

            private DateTimeOffset utc = new(2024, 1, 15, 2, 30, 0, TimeSpan.Zero); //本地即 10:30 +08:00

            public void Advance(TimeSpan delta) => utc += delta;

            public override TimeZoneInfo LocalTimeZone => Tz;

            public override DateTimeOffset GetUtcNow() => utc;

            public override long GetTimestamp() => utc.UtcTicks;
        }

        private static LogEntry Info(string message, DateTimeOffset ts) =>
            new("Cat", LogLevel.Information, 1, message, null, ts);

        private static LogEntry Error(string message, DateTimeOffset ts) =>
            new("Cat", LogLevel.Error, 2, message, "BOOM", ts);

        // ---------------- 级别过滤 ----------------

        [TestMethod]
        public void BelowMinimumLevelIsDropped()
        {
            var queue = new FileLogQueue(16);
            var options = new FileLoggerOptions { MinimumLevel = LogLevel.Information };
            var logger = new FileLogger("Cat", queue, options, new ManualTimeProvider());

            logger.Log(LogLevel.Trace, 0, "x", null, (s, _) => s?.ToString()!);
            logger.Log(LogLevel.Debug, 0, "x", null, (s, _) => s?.ToString()!);

            Assert.AreEqual(0, QueuedCount(queue), "低于最小级别的日志不应入队");
            Assert.IsFalse(logger.IsEnabled(LogLevel.Debug));
        }

        [TestMethod]
        public void AtOrAboveMinimumLevelIsEnqueued()
        {
            var queue = new FileLogQueue(16);
            var options = new FileLoggerOptions { MinimumLevel = LogLevel.Information };
            var logger = new FileLogger("Cat", queue, options, new ManualTimeProvider());

            logger.Log(LogLevel.Information, 7, "msg", null, (s, _) => s!.ToString()!);

            Assert.AreEqual(1, QueuedCount(queue));
            var entry = Dequeue(queue);
            Assert.AreEqual("Cat", entry.CategoryName);
            Assert.AreEqual(7, entry.EventId.Id);
            Assert.AreEqual("msg", entry.Message);
            Assert.IsNull(entry.ExceptionText);
        }

        [TestMethod]
        public void LogWithoutFrameworkFilteringStillHonoursIsEnabled()
        {
            //直接调用 ILogger.Log（绕过框架的 IsEnabled 检查）时，实现自身也必须过滤
            var queue = new FileLogQueue(16);
            var options = new FileLoggerOptions { MinimumLevel = LogLevel.Warning };
            var logger = new FileLogger("Cat", queue, options, new ManualTimeProvider());

            logger.Log(LogLevel.Information, 0, "ignored", null, (s, _) => s?.ToString()!);

            Assert.AreEqual(0, QueuedCount(queue));
        }

        // ---------------- 入队字段捕获 ----------------

        [TestMethod]
        public void ExceptionTextCarriesFullStackTrace()
        {
            var queue = new FileLogQueue(16);
            var clock = new ManualTimeProvider();
            var logger = new FileLogger("Cat", queue, new FileLoggerOptions(), clock);

            var boom = new InvalidOperationException("kaboom");
            logger.Log(LogLevel.Error, 9, "ctx", boom, (s, _) => $"ctx={s}");

            var entry = Dequeue(queue);
            Assert.AreEqual("ctx=ctx", entry.Message);
            StringAssert.Contains(entry.ExceptionText!, "kaboom");
            StringAssert.Contains(entry.ExceptionText!, "InvalidOperationException");
        }

        [TestMethod]
        public void TimestampComesFromInjectedClock()
        {
            var queue = new FileLogQueue(16);
            var clock = new ManualTimeProvider();
            var logger = new FileLogger("Cat", queue, new FileLoggerOptions(), clock);

            logger.Log(LogLevel.Information, 0, "m", null, (s, _) => "m");

            Assert.AreEqual(clock.GetLocalNow(), Dequeue(queue).Timestamp);
        }

        // ---------------- 渲染格式 ----------------

        [TestMethod]
        public void FormatProducesExpectedHeaderAndBody()
        {
            var ts = new DateTimeOffset(2024, 1, 15, 10, 30, 5, 123, TimeSpan.FromHours(8));
            var entry = new LogEntry("Cat", LogLevel.Information, 42, "hello", null, ts);

            var text = FileLogWriter.FormatToString(entry);
            //AppendLine 在 Windows 上产生 \r\n，逐行比较前先归一化行尾
            var lines = text.TrimEnd('\r', '\n').Replace("\r\n", "\n").Split('\n');
            Assert.AreEqual("2024-01-15 10:30:05.123 +08:00 Information: Cat[42]", lines[0]);
            Assert.AreEqual("    hello", lines[1]);
        }

        [TestMethod]
        public void MultiLineExceptionIsIndentedLineByLine()
        {
            var text = FileLogWriter.FormatToString(Error("fail", new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.FromHours(8))));

            StringAssert.Contains(text, "    fail");
            var exceptionBlock = text[(text.IndexOf("BOOM", StringComparison.Ordinal) - 4)..];
            StringAssert.Contains(exceptionBlock, "    BOOM");
        }

        // ---------------- 队列满丢弃 ----------------

        [TestMethod]
        public void FullQueueDropsAndCountsInsteadOfBlocking()
        {
            var queue = new FileLogQueue(4);

            for (var i = 0; i < 8; i++)
            {
                queue.TryEnqueue(Info($"m{i}", new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.FromHours(8))));
            }

            Assert.AreEqual(4, queue.DroppedCount, "超出容量的部分应被丢弃");
            Assert.AreEqual(4, QueuedCount(queue), "缓冲区最多保留容量上限");
        }

        private static LogEntry Dequeue(FileLogQueue q)
        {
            Assert.IsTrue(q.Reader.TryRead(out var entry), "队列应非空");
            return entry;
        }

        private static int QueuedCount(FileLogQueue q) => q.Reader.Count;

        // ---------------- 落盘与跨天分目录 ----------------

        [TestMethod]
        public async Task WritesRoutedAndSplitByLevelOnGracefulStop()
        {
            var temp = CreateTempDir();
            try
            {
                var queue = new FileLogQueue(64);
                var options = new FileLoggerOptions { Directory = temp, FlushInterval = TimeSpan.FromSeconds(60) };
                var svc = new FileLogBackgroundService(queue, options);

                var ts = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.FromHours(8));
                queue.TryEnqueue(Info("info-line", ts));
                queue.TryEnqueue(Error("error-line", ts));
                queue.TryEnqueue(Info("info-2", ts));

                await svc.StartAsync(CancellationToken.None);
                //立即停止：即使 flush 间隔还没到，停机也必须把缓冲区里的内容写盘
                await svc.StopAsync(CancellationToken.None);

                var dayDir = Path.Combine(temp, "2024", "1", "15");
                var logFile = Path.Combine(dayDir, $"log_20240115_{Environment.ProcessId}.txt");
                var errorFile = Path.Combine(dayDir, $"error_20240115_{Environment.ProcessId}.txt");

                Assert.IsTrue(File.Exists(logFile), "普通日志文件应存在");
                Assert.IsTrue(File.Exists(errorFile), "错误日志文件应存在");

                var logText = File.ReadAllText(logFile);
                StringAssert.Contains(logText, "info-line");
                StringAssert.Contains(logText, "error-line");
                StringAssert.Contains(logText, "info-2");

                var errorText = File.ReadAllText(errorFile);
                StringAssert.Contains(errorText, "error-line");
                Assert.IsFalse(errorText.Contains("info-line"), "Information 日志不应进入错误文件");
                Assert.IsFalse(errorText.Contains("info-2"));
            }
            finally
            {
                Directory.Delete(temp, true);
            }
        }

        [TestMethod]
        public async Task EntriesAcrossMidnightLandInDifferentDayDirectories()
        {
            var temp = CreateTempDir();
            try
            {
                var queue = new FileLogQueue(64);
                var options = new FileLoggerOptions { Directory = temp };
                var svc = new FileLogBackgroundService(queue, options);

                //制造跨天的时间戳：服务启动日固定，但日志日期不同——修复了"目录固定在启动日"的旧 bug
                var day15 = new DateTimeOffset(2024, 1, 15, 23, 59, 59, TimeSpan.FromHours(8));
                var day16 = new DateTimeOffset(2024, 1, 16, 0, 0, 1, TimeSpan.FromHours(8));

                queue.TryEnqueue(Info("tail-of-15", day15));
                queue.TryEnqueue(Info("head-of-16", day16));

                await svc.StartAsync(CancellationToken.None);
                await svc.StopAsync(CancellationToken.None);

                var probe = Path.Combine(temp, "2024", "1", "15", $"log_20240115_{Environment.ProcessId}.txt");
                Assert.IsTrue(File.Exists(probe), "15 日日志应写入 15 日目录");
                Assert.IsTrue(File.Exists(Path.Combine(temp, "2024", "1", "16", $"log_20240116_{Environment.ProcessId}.txt")), "16 日日志应写入 16 日目录而非启动日目录");

                StringAssert.Contains(File.ReadAllText(Path.Combine(temp, "2024", "1", "15", $"log_20240115_{Environment.ProcessId}.txt")), "tail-of-15");
                StringAssert.Contains(File.ReadAllText(Path.Combine(temp, "2024", "1", "16", $"log_20240116_{Environment.ProcessId}.txt")), "head-of-16");
            }
            finally
            {
                Directory.Delete(temp, true);
            }
        }

        [TestMethod]
        public async Task StopAsyncDrainsEveryBufferedEntry()
        {
            var temp = CreateTempDir();
            try
            {
                //容量必须大于入队总数，才能验证"停机把缓冲全写完"这条不变量；
                //有界队列在满时的丢弃行为另有专项测试覆盖，这里不混在一起
                var queue = new FileLogQueue(1024);
                var options = new FileLoggerOptions { Directory = temp, FlushInterval = TimeSpan.FromMinutes(1) };
                var svc = new FileLogBackgroundService(queue, options);

                var ts = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.FromHours(8));
                const int count = 300;
                for (var i = 0; i < count; i++)
                {
                    queue.TryEnqueue(Info($"line-{i}", ts));
                }

                await svc.StartAsync(CancellationToken.None);
                await svc.StopAsync(CancellationToken.None);

                var text = File.ReadAllText(Path.Combine(temp, "2024", "1", "15", $"log_20240115_{Environment.ProcessId}.txt"));
                for (var i = 0; i < count; i++)
                {
                    StringAssert.Contains(text, $"line-{i}");
                }
                Assert.AreEqual(0, QueuedCount(queue), "停机后队列应被清空");
            }
            finally
            {
                Directory.Delete(temp, true);
            }
        }

        [TestMethod]
        public async Task IOFailureDoesNotKillTheBackgroundService()
        {
            var temp = CreateTempDir();
            //指向一个"不可写"的路径：父目录本身是文件，无法在其下建目录
            var blocked = Path.Combine(temp, "not-a-dir");
            var options = new FileLoggerOptions { Directory = blocked };
            var queue = new FileLogQueue(8);
            var svc = new FileLogBackgroundService(queue, options);

            try
            {
                //创建同名文件把"目录路径"占掉，使 FileStream 打开时抛 IOException
                await File.WriteAllTextAsync(blocked, "occupied");

                await svc.StartAsync(CancellationToken.None);
                var ts = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.FromHours(8));
                for (var i = 0; i < 8; i++)
                {
                    queue.TryEnqueue(Info($"boom-{i}", ts));
                }

                //写盘必然失败，但服务不能因此抛异常退出（ExecuteAsync 抛异常会被宿主当成致命错误）
                await Task.Delay(200);
                await svc.StopAsync(CancellationToken.None);

                Assert.IsNull(svc.LastFault, "磁盘 I/O 失败不应冒泡成后台循环的未处理异常");
                Assert.AreEqual(0, QueuedCount(queue), "失败条目也应被消费掉，不能阻塞队列");
            }
            finally
            {
                File.Delete(blocked);
                Directory.Delete(temp, true);
            }
        }

        [TestMethod]
        public void ZeroCapacityIsRejected()
        {
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => new FileLogQueue(0));
            Assert.ThrowsException<ArgumentOutOfRangeException>(() => new FileLogQueue(-1));
        }

        [TestMethod]
        public void EntriesAfterCompleteAreDropped()
        {
            var queue = new FileLogQueue(8);
            var ts = new DateTimeOffset(2024, 1, 15, 10, 0, 0, TimeSpan.FromHours(8));

            Assert.IsTrue(queue.TryEnqueue(Info("before", ts)));
            queue.Complete();

            Assert.IsFalse(queue.TryEnqueue(Info("after", ts)), "Complete 之后不应再接收日志");
            Assert.AreEqual(1, queue.DroppedCount);
        }

        [TestMethod]
        public void AddFileLoggerRegistersResolvableServices()
        {
            var temp = CreateTempDir();
            try
            {
                var services = new ServiceCollection();
                services.AddLogging(b => b.AddFileLogger(o =>
                {
                    o.Directory = temp;
                    o.MinimumLevel = LogLevel.Warning;
                    o.QueueCapacity = 128;
                }));

                using var provider = services.BuildServiceProvider();

                //provider 与 hosted service 必须拿到同一个队列实例，否则写与读会分属两个通道
                var queue = provider.GetRequiredService<FileLogQueue>();
                Assert.AreEqual(1, provider.GetServices<IHostedService>().OfType<FileLogBackgroundService>().Count(), "应恰好注册一个后台落盘服务");

                var factory = provider.GetRequiredService<ILoggerFactory>();
                var logger = factory.CreateLogger("Some.Category");

                Assert.IsFalse(logger.IsEnabled(LogLevel.Information), "MinimumLevel=Warning 时 Information 应被禁用");
                Assert.IsTrue(logger.IsEnabled(LogLevel.Warning));

                logger.Log(LogLevel.Warning, 1, "hello", null, (s, _) => s?.ToString()!);
                Assert.AreEqual(1, queue.Reader.Count, "共享队列里应能看到 provider 写出的日志");
            }
            finally
            {
                Directory.Delete(temp, true);
            }
        }

        private static string CreateTempDir()
        {
            //每个测试一个独占目录，避免并发测试互相撞目录
            var dir = Path.Combine(Path.GetTempPath(), "filelogger-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
