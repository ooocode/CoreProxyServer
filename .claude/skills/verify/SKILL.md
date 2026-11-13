# verify — CoreProxy.Server.Orleans runtime verification

How to drive the gRPC tunnel surface (`Greeter.StreamHandler`) against a
**running** server and observe real behavior. Do not verify by running the
MSTest suite — that's CI's job.

## Build

```bash
dotnet build CoreProxy.Server.Orleans/CoreProxy.Server.Orleans.csproj -c Debug
```

Debug now targets **net11.0** (RC2 SDK installed at `C:\Program Files\dotnet\sdk\11.0.100-rc.2.*`),
same as Release. `#if !DEBUG` skips the server certificate, so Debug binds **plaintext HTTP/2**.

## Run the server

```bash
dotnet run --project CoreProxy.Server.Orleans/CoreProxy.Server.Orleans.csproj \
  -c Debug --no-build --urls http://127.0.0.1:6110
```

`appsettings.json` says `https://*:6099`; override with `--urls` for a local plaintext run.
`CheckPassword` returns early for loopback peers, so a client on 127.0.0.1 needs no
`Authorization: Password <hash>` header. Verify it's up:
`tail` the run output for `Now listening on: http://127.0.0.1:6110`.

## Drive a tunnel

Each `StreamHandler` = one gRPC bidi stream + one real TCP connect to the target.
Target URL goes in the **`X-Requested-With`** request header (not a message body).
The server sends one **empty `HttpData` payload first** as the "connected" handshake,
then relays bytes. See `tools/bench/Tunnel` and `CoreProxy.Server.Orleans.Tests/Infrastructure/Tunnel.cs`
for reference clients.

A minimal driver project (net11.0) referencing the shared proto works:
`Grpc.Net.Client` + `Google.Protobuf` + `Grpc.Tools` (`GrpcServices="Client"`) with
`<Protobuf Include="TextFile.proto" />`. Packages are already in the NuGet cache
(grpc.net.client 2.71.0, google.protobuf 3.31.1, grpc.tools 2.72.0), so it restores offline.

### `MoveNext` gotcha
This Grpc.Net.Client version's `IAsyncStreamReader<T>.MoveNext` takes a
`CancellationToken`, **not** a `TimeSpan`. Wrap in `new CancellationTokenSource(TimeSpan...)`.

### Don't hang the driver
If you start a local echo/accept loop for the target, do **not** `await` it at the
end of `Main` — it never completes, the process never exits, and a piping shell
(`| tail`) buffers all output and shows nothing.

## Flows worth driving (target types)

| Target | Path exercised |
|---|---|
| `http://127.0.0.1:<port>/` | IP literal — short-circuits `IPAddress.TryParse`, **no DNS** |
| `http://lvh.me:<port>/` | domain → net11 `DnsResolver` (+ `IMemoryCache` when TTL>0); 解析走系统 DNS |
| same domain again | TTL cache-hit path |
| `http://does-not-exist.invalid:<port>/` | NXDOMAIN → clean `InvalidArgument` |

`tools/bench` targets a literal `127.0.0.1`, so it does **not** exercise the DNS/cache path.

## Gotchas / findings from the field

- **TargetResolver 默认走 net11 `DnsResolver`（系统 DNS，未指定服务器）。** 构造函数第三个参数
  `resolveAsync` 可注入解析委托（仅供测试）；`StreamHandlerCacheAndThroughputTests` 已注入固定回环，
  不再依赖真实 DNS。`TargetResolverTests` 仍走真实 DNS，CI 用
  `--filter "FullyQualifiedName!~TargetResolverTests"` 排除。
- **Raw `SocketException` on connect-refused escapes as `RPC_Unknown`.** `ConnectToTargetAsync`
  only wraps `OperationCanceledException` → `Unavailable`; a refused TCP connect throws
  `SocketException 10061` which gRPC maps to `Unknown: Exception was thrown by handler`.
  Pre-existing, not from the TargetResolver diff (see `Services/TunnelGrpcService.cs`).

## Logs

File logger writes under `CoreProxy.Server.Orleans/bin/<Config>/net11.0/logs/YYYY/MM/DD/`
— `log_<date>_<pid>.txt` and `error_<date>_<pid>.txt`. The pid matches the running server
process (find via `netstat -ano | grep <port>`).
