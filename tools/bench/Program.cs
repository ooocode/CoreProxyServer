using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Hello;

namespace Bench;

/// <summary>
/// 并发隧道压测客户端。
///
/// 被测对象：CoreProxy.Server.Orleans 的 gRPC 隧道（Greeter.StreamHandler）。
/// 每条隧道 = 客户端一条 gRPC 双向流 + 服务端连到目标的一条真实 TCP 连接。
///
/// 每档并发都会：
///   1) 把存活隧道数拉到 N
///   2) 等全部握手完成
///   3) 让每条隧道跑一段 echo 往返，逐字节校验（证明隧道真的可用，而不只是"建立起来了"）
///   4) 调 GetStatus 读服务端真实连接数，与本地存活数比对
/// 隧道跨档累加保留，形成真实的阶梯加压。
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        var server = Arg(args, "--server", "http://127.0.0.1:6110");
        var steps = (Arg(args, "--steps", "8,16,32,64,128,256,512,1024")).Split(',', StringSplitOptions.RemoveEmptyEntries)
                                                                      .Select(int.Parse).ToArray();
        var holdMs = int.Parse(Arg(args, "--hold-ms", "2000"));
        var serverPid = int.Parse(Arg(args, "--server-pid", "0"));
        var intervalMs = int.Parse(Arg(args, "--interval-ms", "0"));
        var batch = int.Parse(Arg(args, "--batch", "0"));

        using var echo = new EchoServer();
        echo.Start();
        var targetUri = $"http://127.0.0.1:{echo.Port}/";
        Console.WriteLine($"[echo]    echo 目标服务器   127.0.0.1:{echo.Port}");
        Console.WriteLine($"[server]  被测服务器       {server}");
        Console.WriteLine($"[plan]    阶梯并发         {string.Join(", ", steps)}，每档停留 {holdMs}ms");
        Console.WriteLine();

        // 关键：服务端配置 Http2MaxStreamsPerConnection=8，
        // 所以每条 HTTP/2 连接最多 8 条并发流。客户端必须按"每连接≤8 流"分散到多个 channel，
        // 就像正规消费者那样，才不会因为单连接超流被服务端 REFUSED_STREAM 拒掉，
        // 从而测得服务端"在合理客户端下"的真实并发承载上限。
        const int StreamsPerConnection = 8;
        var clientPool = new List<Greeter.GreeterClient>();
        var allChannels = new List<GrpcChannel>();

        static GrpcChannel NewChannel(string server) => GrpcChannel.ForAddress(server, new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler
            {
                // 每个 channel 独占一条 HTTP/2 基础连接，这样每连接的流数完全由我们分配，不会撞上限
                EnableMultipleHttp2Connections = false,
                MaxConnectionsPerServer = 1,
                PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            }
        });

        Greeter.GreeterClient ClientFor(long id)
        {
            var idx = (int)((id - 1) / StreamsPerConnection);
            while (clientPool.Count <= idx)
            {
                var chNew = NewChannel(server);
                allChannels.Add(chNew);
                clientPool.Add(new Greeter.GreeterClient(chNew));
            }
            return clientPool[idx];
        }

        // 查询状态用的独立连接，不占隧道配额
        var statusChannel = NewChannel(server);
        var statusClient = new Greeter.GreeterClient(statusChannel);

        using var stop = new CancellationTokenSource();
        var tunnels = new ConcurrentDictionary<long, Tunnel>();
        long nextId = 0;

        // 预热：先建一条，确认协议与数据面都对
        {
            var warm = Tunnel.Start(ClientFor(Interlocked.Increment(ref nextId)), targetUri, nextId, stop.Token, startLoop: false);
            if (!await warm.Established.Task.WaitAsync(TimeSpan.FromSeconds(15)) || warm.Failure is not null)
            {
                Console.WriteLine($"[FATAL] 预热隧道未建立：{warm.Failure?.GetBaseException().Message}");
                return 2;
            }
            if (!await warm.ProbeAsync(TimeSpan.FromSeconds(10)))
            {
                Console.WriteLine("[FATAL] 预热隧道数据面校验失败（echo 数据不一致）");
                return 2;
            }
            Console.WriteLine("[warmup] 握手 + echo 往返校验通过\n");
            await warm.DisposeAsync();
        }

        Console.WriteLine($"{"并发",-7}{"本地存活",-9}{"失败",-6}{"建连ms",-8}{"服务端核对",-12}{"本档往返",-10}{"本档吞吐KB",-12}{"服务端SocketCount",-18}{"服务端CPU%",-11}{"服务端WS(MB)",-12}");
        var allErrors = new ConcurrentBag<string>();

        Process? serverProc = null;
        if (serverPid != 0)
        {
            try { serverProc = Process.GetProcessById(serverPid); } catch { }
        }

        foreach (var target in steps)
        {
            var stageSw = Stopwatch.StartNew();

            // 1+2) 建立本档新增的隧道。
            //     --batch>0 时分批建立（批内并发、批间等全部握手完成），避免一次性建 8000+ 条流
            //     把客户端进程自己打成建连风暴，从而误判成服务端上限。
            var establishSw = Stopwatch.StartNew();
            {
                var toOpen = target - tunnels.Count;
                var batchSize = batch > 0 ? batch : Math.Max(1, toOpen);
                for (var opened = 0; opened < toOpen;)
                {
                    var n = Math.Min(batchSize, toOpen - opened);
                    var thisBatch = new List<Tunnel>(n);
                    for (var i = 0; i < n; i++)
                    {
                        thisBatch.Add(Tunnel.Start(ClientFor(Interlocked.Increment(ref nextId)), targetUri,
                            nextId, stop.Token, intervalMs: intervalMs));
                    }
                    opened += n;

                    try
                    {
                        await Task.WhenAll(thisBatch.Select(t => t.Established.Task)).WaitAsync(TimeSpan.FromSeconds(120));
                    }
                    catch (TimeoutException)
                    {
                        allErrors.Add($"第 {target} 档建连超时（{thisBatch.Count(t => !t.Established.Task.IsCompleted)} 条未完成）");
                    }

                    foreach (var t in thisBatch)
                    {
                        if (t.Failure is null)
                        {
                            tunnels[t.Id] = t;
                        }
                        else
                        {
                            allErrors.Add($"握手失败 #{t.Id}: {t.Failure.GetBaseException().Message}");
                            await t.DisposeAsync();
                        }
                    }
                }
            }
            establishSw.Stop();

            // 3) 本档 echo 流量 + 校验
            var roundTripsBefore = tunnels.Values.Sum(t => t.RoundTrips);
            var bytesBefore = tunnels.Values.Sum(t => t.BytesEchoed);

            var cpuBefore = SampleCpu(serverProc);
            var wsampleBefore = serverProc?.WorkingSet64 ?? 0;

            await Task.Delay(holdMs);

            var roundTrips = tunnels.Values.Sum(t => t.RoundTrips) - roundTripsBefore;
            var bytes = tunnels.Values.Sum(t => t.BytesEchoed) - bytesBefore;
            var cpuAfter = SampleCpu(serverProc);
            var wsAfter = serverProc?.WorkingSet64 ?? 0;
            // 服务端 CPU：本档窗口内消耗的 CPU 时间占单核的百分比
            var serverCpu = cpuBefore >= 0 && cpuAfter >= 0 && holdMs > 0
                ? (cpuAfter - cpuBefore) / (holdMs / 1000.0) * 100
                : -1;

            stageSw.Stop();

            // 4) 读服务端真实连接数（用一条独立的 channel，不占用隧道配额）
            int socketCount = -1;
            try
            {
                var status = await statusClient.GetStatusAsync(new GetStatusRequest { IncludeDetail = false });
                socketCount = status.SocketCount;
            }
            catch (Exception ex)
            {
                allErrors.Add($"GetStatus 失败: {ex.GetBaseException().Message}");
            }

            var failed = tunnels.Values.Count(t => t.Failure is not null);
            var check = socketCount == tunnels.Count ? "一致" : $"差{socketCount - tunnels.Count}";
            Console.WriteLine($"{target,-7}{tunnels.Count,-9}{failed,-6}{establishSw.ElapsedMilliseconds,-8}{check,-12}{roundTrips,-10}{bytes / 1024.0,-12:N0}{socketCount,-18}"
                + $"{serverCpu,-11:N1}{wsAfter / 1024.0 / 1024.0,-12:N1}");
        }

        // 收尾：全部关闭后服务端连接数必须归零（验证无泄漏）
        Console.WriteLine("\n[cleanup] 关闭全部隧道...");
        foreach (var t in tunnels.Values) await t.DisposeAsync();
        tunnels.Clear();
        await Task.Delay(2000);

        int finalCount = -1;
        try
        {
            var status = await statusClient.GetStatusAsync(new GetStatusRequest { IncludeDetail = false });
            finalCount = status.SocketCount;
        }
        catch (Exception ex) { allErrors.Add($"收尾 GetStatus 失败: {ex.GetBaseException().Message}"); }

        Console.WriteLine($"[cleanup] 关闭后服务端 SocketCount = {finalCount}（期望 0）");

        if (!allErrors.IsEmpty)
        {
            Console.WriteLine($"\n=== 错误明细（{allErrors.Count} 条，最多显示 40）===");
            foreach (var e in allErrors.Take(40)) Console.WriteLine("  " + e);
        }

        foreach (var c in allChannels)
        {
            try { await c.ShutdownAsync(); } catch { }
        }
        try { await statusChannel.ShutdownAsync(); } catch { }
        Console.WriteLine(allErrors.IsEmpty && finalCount == 0 ? "\n[RESULT] 全部通过" : "\n[RESULT] 存在失败项");
        return allErrors.IsEmpty && finalCount == 0 ? 0 : 1;
    }

    private static double SampleCpu(Process? p)
    {
        if (p is null) return -1;
        try { p.Refresh(); return p.TotalProcessorTime.TotalSeconds; } catch { return -1; }
    }

    private static string Arg(string[] args, string name, string def)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : def;
    }
}

/// <summary>一条并发隧道：gRPC 双向流 + 后台 echo 校验循环</summary>
public sealed class Tunnel : IAsyncDisposable
{
    private const int PayloadSize = 256;

    private readonly AsyncDuplexStreamingCall<HttpData, HttpData> _call;
    private readonly CancellationToken _token;
    private readonly CancellationTokenSource _loopCts = new();
    private Task? _loop;

    private Tunnel(long id, AsyncDuplexStreamingCall<HttpData, HttpData> call, CancellationToken token)
    {
        Id = id;
        _call = call;
        _token = token;
    }

    public long Id { get; }

    public TaskCompletionSource<bool> Established { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Exception? Failure { get; private set; }

    public long RoundTrips => Interlocked.Read(ref _roundTrips);
    private long _roundTrips;

    public long BytesEchoed => Interlocked.Read(ref _bytesEchoed);
    private long _bytesEchoed;

    public static Tunnel Start(Greeter.GreeterClient client, string targetUri, long id, CancellationToken token,
        bool startLoop = true, int intervalMs = 0)
    {
        var headers = new Metadata { { "X-Requested-With", targetUri } };
        var call = client.StreamHandler(headers, cancellationToken: token);
        var tunnel = new Tunnel(id, call, token);
        tunnel._loop = tunnel.RunAsync(startLoop, intervalMs);
        return tunnel;
    }

    private async Task RunAsync(bool startLoop, int intervalMs)
    {
        try
        {
            // 握手：服务端先回一个空包表示目标连接已建立
            if (!await _call.ResponseStream.MoveNext(_token))
            {
                throw new IOException("响应流在握手前就结束了");
            }
            if (_call.ResponseStream.Current.Payload.Length != 0)
            {
                throw new IOException("第一个响应不是表示连接成功的空包");
            }

            Established.TrySetResult(true);

            if (!startLoop)
            {
                return; // 预热：不启动后台 echo 循环，改由调用方手动 ProbeAsync，避免并发写同一流
            }

            var payload = new byte[PayloadSize];
            Encoding.UTF8.GetBytes($"c{Id}:", payload);
            var linked = CancellationTokenSource.CreateLinkedTokenSource(_token, _loopCts.Token);
            var ct = linked.Token;

            while (!ct.IsCancellationRequested)
            {
                payload[PayloadSize - 1] = (byte)(Interlocked.Read(ref _roundTrips) & 0xFF);
                await _call.RequestStream.WriteAsync(new HttpData { Payload = ByteString.CopyFrom(payload) }, ct);

                if (!await _call.ResponseStream.MoveNext(ct)) break;
                var back = _call.ResponseStream.Current.Payload.Span;
                if (back.Length != PayloadSize || !back.SequenceEqual(payload))
                {
                    throw new IOException($"echo 数据不一致 #{Id} 往返 {RoundTrips}（收到 {back.Length} 字节）");
                }

                Interlocked.Increment(ref _roundTrips);
                Interlocked.Add(ref _bytesEchoed, PayloadSize * 2L);

                // 轻负载模式：每轮结束后睡一会儿，让隧道保持存活但 CPU 不被打满——
                // 这样才能单独测"并发连接数上限"，而不会被数据吞吐的 CPU 瓶颈抢在前面
                if (intervalMs > 0)
                {
                    await Task.Delay(intervalMs, ct);
                }
            }

            Established.TrySetResult(true); // 循环正常退出（被停止）时也放行，避免调用方空等
        }
        catch (OperationCanceledException)
        {
            Established.TrySetResult(true);
        }
        catch (Exception ex)
        {
            Failure = ex;
            Established.TrySetResult(true);
        }
    }

    /// <summary>独立做一次发+收并逐字节校验，用于预热验证</summary>
    public async Task<bool> ProbeAsync(TimeSpan timeout)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_token);
        cts.CancelAfter(timeout);
        var payload = Encoding.UTF8.GetBytes($"probe-{Id}-{Guid.NewGuid():N}");
        await _call.RequestStream.WriteAsync(new HttpData { Payload = ByteString.CopyFrom(payload) }, cts.Token);
        if (!await _call.ResponseStream.MoveNext(cts.Token)) return false;
        return _call.ResponseStream.Current.Payload.Span.SequenceEqual(payload);
    }

    public async ValueTask DisposeAsync()
    {
        _loopCts.Cancel();
        try { _call.RequestStream.CompleteAsync().Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { _call.Dispose(); } catch { }
        if (_loop is not null) { try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { } }
        _loopCts.Dispose();
    }
}

/// <summary>本机 TCP echo 目标：原样回显收到的字节，用于验证隧道数据面</summary>
public sealed class EchoServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();

    public int Port { get; private set; }

    public void Start()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint!).Port;
        _ = AcceptLoopAsync();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = EchoAsync(client);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
        }
    }

    private async Task EchoAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                var buffer = new byte[64 * 1024];
                while (true)
                {
                    var n = await stream.ReadAsync(buffer, _cts.Token);
                    if (n == 0) return;
                    await stream.WriteAsync(buffer.AsMemory(0, n), _cts.Token);
                }
            }
        }
        catch { /* 单条连接异常不影响整体 */ }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        _cts.Dispose();
    }
}
