using System.Net;
using System.Net.Sockets;
using System.Text;

// 端到端联调压测：本机 TCP echo 目标 → 客户端本地代理端口(HTTP CONNECT) → 服务端 StreamHandler。
// 每轮：对代理端口发 CONNECT 建隧道，写入随机数据，逐字节校验 echo 回来的数据 == 发出的数据
//（这条回环同时证明了两个方向都无损，且链路确实打通到目标）。

int proxyPort = 11079;
int rounds = 2000;
int minBytes = 1024, maxBytes = 256 * 1024;
int concurrency = 8;
// 可选命令行：
//   linktest <rounds> <concurrency>                    —— 本机 echo 字节校验模式
//   linktest --rst <rounds> <concurrency>              —— 校验通过后 SO_LINGER=0 强制 RST（异常收尾）
//   linktest --unreachable <rounds> <concurrency>      —— 连必然关闭的目标端口，期望隧道失败（真实故障）
//   linktest <rounds> <concurrency> <host> <port>      —— 真实目标 HTTP 校验模式（经真实服务器连公网目标）
string mode = "echo";
int offset = 0;
if (args.Length > 0 && args[0] is "--rst" or "--unreachable")
{
    mode = args[0][2..];
    offset = 1;
}
if (args.Length >= offset + 1) rounds = int.Parse(args[offset]);
if (args.Length >= offset + 2) concurrency = int.Parse(args[offset + 1]);
string? targetHost = args.Length >= offset + 4 ? args[offset + 2] : null;
int targetPort = args.Length >= offset + 4 ? int.Parse(args[offset + 3]) : 0;
bool rstMode = mode == "rst";
bool unreachableMode = mode == "unreachable";
bool httpMode = mode == "echo" && targetHost is not null;
long totalBytes = 0;
int ok = 0, fail = 0, fail400 = 0, failOther = 0;
var gate = new object();
var failures = new List<string>();

// 1) echo/RST 模式：本机 echo 目标服务器（真实模式与不可达模式不建）
int echoPort = 0;
if (!httpMode && !unreachableMode)
{
    var echoListener = new TcpListener(IPAddress.Loopback, 0);
    echoListener.Start();
    echoPort = ((IPEndPoint)echoListener.LocalEndpoint!).Port;
    _ = Task.Run(async () =>
    {
        try
        {
            while (true)
            {
                var client = await echoListener.AcceptTcpClientAsync();
                _ = Task.Run(async () =>
                {
                    var buf = new byte[64 * 1024];
                    var ns = client.GetStream();
                    try
                    {
                        int n;
                        while ((n = await ns.ReadAsync(buf)) > 0)
                            await ns.WriteAsync(buf.AsMemory(0, n)); // echo 原样返回
                    }
                    catch { }
                    finally { client.Dispose(); }
                });
            }
        }
        catch { }
    });
}
Console.WriteLine(unreachableMode
    ? $"不可达目标: 127.0.0.1:1  客户端代理: 127.0.0.1:{proxyPort}  轮数:{rounds}  并发:{concurrency}  [期望隧道失败]"
    : httpMode
        ? $"目标: {targetHost}:{targetPort}  客户端代理: 127.0.0.1:{proxyPort}  轮数:{rounds}  并发:{concurrency}  [HTTP模式]"
        : $"echo 目标: 127.0.0.1:{echoPort}  客户端代理: 127.0.0.1:{proxyPort}  轮数:{rounds}  并发:{concurrency}{(rstMode ? "  [RST模式]" : "")}");

var sw = System.Diagnostics.Stopwatch.StartNew();

await Parallel.ForEachAsync(Enumerable.Range(0, rounds), new ParallelOptions { MaxDegreeOfParallelism = concurrency }, async (i, _) =>
{
    using var timeout = new CancellationTokenSource(httpMode ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(30));
    var ct = timeout.Token;
    int reqBytes = Random.Shared.Next(minBytes, maxBytes + 1);
    var request = new byte[reqBytes];
    Random.Shared.NextBytes(request); // 线程安全随机，避免并发下共享 Random 产生损坏字节

    try
    {
        using var proxy = new TcpClient();
        await proxy.ConnectAsync(IPAddress.Loopback, proxyPort, ct);

        using var tunnel = proxy.GetStream();

        if (unreachableMode)
        {
            // 目标端口 1 必然无监听：服务端建连应失败。
            // 客户端可能仍回 200（Kestrel 对未显式写状态的响应默认 200），所以不能只看响应头；
            // 以"发探针后收不到任何回显、连接被关闭/超时"作为隧道确实未打通的判据。
            var connectReq = Encoding.ASCII.GetBytes(
                "CONNECT 127.0.0.1:1 HTTP/1.1\r\nHost: 127.0.0.1:1\r\n\r\n");
            await tunnel.WriteAsync(connectReq, ct);
            try { await ReadHeaderAsync(tunnel, ct); } catch { }

            bool tunnelWorked = false;
            try
            {
                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                probeCts.CancelAfter(TimeSpan.FromSeconds(5));
                await tunnel.WriteAsync(new byte[] { 0x41 }, probeCts.Token);
                var probeBuf = new byte[16];
                tunnelWorked = await tunnel.ReadAsync(probeBuf, probeCts.Token) > 0;
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }

            lock (gate)
            {
                if (!tunnelWorked)
                {
                    ok++; // 期望行为：隧道确实没打通
                }
                else
                {
                    fail++;
                    failOther++;
                    if (failures.Count < 10) failures.Add($"轮{i}: 不可达目标 127.0.0.1:1 居然打通了");
                }
            }
            return;
        }

        if (httpMode)
        {
            // 真实目标模式：CONNECT 到真实公网目标，发 HTTP GET，校验响应为 200
            var connectReq = Encoding.ASCII.GetBytes(
                $"CONNECT {targetHost}:{targetPort} HTTP/1.1\r\nHost: {targetHost}:{targetPort}\r\n\r\n");
            await tunnel.WriteAsync(connectReq, ct);
            Ensure200(await ReadHeaderAsync(tunnel, ct));

            // 经隧道发 HTTP 请求
            if (targetPort == 443)
            {
                // HTTPS CONNECT 隧道是裸 TCP，客户端需自行 TLS——不做 TLS 就发 HTTP 到 443 无法校验。
                // 这里约定传 80（明文 HTTP）；若传 443 直接断言失败。
                throw new InvalidOperationException("HTTPS 目标需 TLS 处理，本工具仅支持明文 HTTP(80) 目标");
            }
            var httpReq = Encoding.ASCII.GetBytes(
                $"GET / HTTP/1.1\r\nHost: {targetHost}\r\nConnection: close\r\n\r\n");
            await tunnel.WriteAsync(httpReq, ct);

            // 读响应：以 HTTP/1.1 200 开头即视为成功（取前若干字节）
            var responseHead = await ReadHeaderAsync(tunnel, ct);
            if (!responseHead.StartsWith("HTTP/1.1 200", StringComparison.Ordinal))
                throw new InvalidOperationException($"HTTP 非 200: {Escape(responseHead)}");
        }
        else
        {
            // echo 模式：CONNECT 到本机 echo，双向逐字节校验
            var connectReq = Encoding.ASCII.GetBytes(
                $"CONNECT 127.0.0.1:{echoPort} HTTP/1.1\r\nHost: 127.0.0.1:{echoPort}\r\n\r\n");
            await tunnel.WriteAsync(connectReq, ct);
            Ensure200(await ReadHeaderAsync(tunnel, ct));

            var readTask = Task.Run(async () =>
            {
                var collected = new MemoryStream();
                var buf = new byte[64 * 1024];
                while (collected.Length < request.Length)
                {
                    var n = await tunnel.ReadAsync(buf, ct);
                    if (n == 0) break;
                    collected.Write(buf, 0, n);
                }
                return collected.ToArray();
            }, ct);

            await tunnel.WriteAsync(request, ct);
            var echoed = await readTask;

            if (echoed.Length != request.Length || !EqualsBytes(echoed, request))
                throw new InvalidOperationException($"字节不一致: 期望{request.Length} 实际{echoed.Length} 预览[{PreviewBytes(echoed)}]");

            if (rstMode)
            {
                // 正常收发完成后再模拟客户端异常断开：linger=0 让内核直接发 RST（跳过四次挥手）。
                // 服务端应把它归为"正常收尾"，不得记 Error。
                proxy.Client.LingerState = new LingerOption(true, 0);
                proxy.Close();
            }
        }

        lock (gate) { totalBytes += request.Length; ok++; }
    }
    catch (Exception ex)
    {
        lock (gate)
        {
            fail++;
            if (ex.Message.Contains("400 Bad Request")) fail400++;
            else failOther++;
        }
        lock (failures)
            if (failures.Count < 10) failures.Add($"轮{i}: {ex.GetType().Name}: {ex.Message}");
    }
});

sw.Stop();

Console.WriteLine(unreachableMode
    ? $"完成 {rounds} 轮: 隧道失败符合预期={ok} 意外打通={fail}  耗时 {sw.Elapsed.TotalSeconds:F1}s"
    : $"完成 {rounds} 轮: 成功={ok} 失败={fail} (其中400 BadRequest={fail400}, 其他={failOther})  互通 {totalBytes / 1024.0 / 1024.0:F1} MiB  耗时 {sw.Elapsed.TotalSeconds:F1}s");
if (failures.Count > 0)
{
    Console.WriteLine("--- 失败明细(前10) ---");
    failures.ForEach(f => Console.WriteLine("  " + f));
}
return fail == 0 ? 0 : 1;

// 读 HTTP 响应头：以 \r\n\r\n 结束
static async Task<string> ReadHeaderAsync(Stream s, CancellationToken ct)
{
    var header = new MemoryStream();
    var one = new byte[1];
    while (true)
    {
        if (await s.ReadAsync(one, ct) != 1) break;
        header.WriteByte(one[0]);
        var h = header.GetBuffer();
        var len = (int)header.Length;
        if (len >= 4 && h[len - 4] == 13 && h[len - 3] == 10 && h[len - 2] == 13 && h[len - 1] == 10) break;
    }
    return Encoding.ASCII.GetString(header.GetBuffer(), 0, (int)header.Length);
}

static void Ensure200(string head)
{
    if (!head.StartsWith("HTTP/1.1 200", StringComparison.Ordinal))
        throw new InvalidOperationException($"CONNECT 失败: {Escape(head)}");
}

static string PreviewBytes(byte[] b) => Escape(Encoding.ASCII.GetString(b, 0, Math.Min(b.Length, 80)));

static string Escape(string s) => s.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\0", "*");

static bool EqualsBytes(byte[] a, byte[] b)
{
    if (a.Length != b.Length) return false;
    for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
    return true;
}
