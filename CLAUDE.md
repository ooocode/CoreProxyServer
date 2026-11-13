# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 概览

这是一个**内网穿透 / TCP 转发代理**仓库：客户端（`CoreProxy.Exe`）通过一条 gRPC 双向流把流量发到服务端（`CoreProxy.Server.Orleans`），服务端把每条流桥接成一条到目标服务器的真实 TCP 连接。仓库是一个多项目 .NET 仓库（git 根在上一级），核心与长期演进目标是 `CoreProxy.Server.Orleans`（服务端，Linux 部署，AOT 编译）。

仓库布局（git 根 `CoreProxyServer/`，统一解决方案 `CoreProxy.sln`）：

- **`CoreProxy.Server.Orleans/`** — 服务端。gRPC 隧道（`Greeter.StreamHandler`）、SignalR Hub、YARP 反代、管理 API。当前工作焦点。
- **`CoreProxy.Server.Orleans.Tests/`** — 服务端 MSTest 测试（活的传输层测试）。
- **`CoreProxy.Exe/`** — Windows 客户端：本地监听 SOCKS5 / HTTP CONNECT，反向连接服务端。
- **`CoreProxy.Exe.Tests/`** — 客户端 MSTest 测试。
- **`CoreProxy.Shared/`** — 客户端与服务端共用的隧道常量（单目标 net10，net11 服务端可直接引用）。
- **`tools/bench/`** — 并发压测客户端（直连 gRPC 或经客户端 SOCKS5 的端到端模式）。
- **`proto/TextFile.proto`** — 全仓库共享的**唯一**协议定义，被服务端/客户端/bench 三处引用，改动协议必须三处同步（两个 csproj 都用 `..\proto\TextFile.proto` 引入）。

协议关键点：`service Greeter` 定义了 `StreamHandler`（隧道）、`P2PStreamHandler`（p2p）、`GetStatus`、`GetSignalrOnlineClients`；**服务端目前只实现 `StreamHandler` / `GetStatus` / `GetSignalrOnlineClients`，`P2PStreamHandler` 仅存在于 proto**。`HttpData { bytes Payload; int64 UnixTimeMilliseconds }`。

## 构建与测试命令

**目标框架**：服务端 Debug/Release 都是 `net11.0`（需要 .NET 11 SDK，本机为 `11.0.100`）；客户端与 bench 是 `net10.0`（客户端构建只需 .NET 10 SDK；`CoreProxy.Shared` 也是 net10，net11 服务端可直接引用）。CI 用 .NET 11 做服务端 AOT 发布。

```bash
# 构建整个解决方案（服务端、客户端、两个测试项目、bench、共享项目）
dotnet build CoreProxy.sln -c Debug

# 服务端全量测试
dotnet test CoreProxy.Server.Orleans.Tests/CoreProxy.Server.Orleans.Tests.csproj -c Debug

# 按方法名或类名过滤
dotnet test CoreProxy.Server.Orleans.Tests/CoreProxy.Server.Orleans.Tests.csproj -c Debug \
  --filter "FullyQualifiedName~StreamHandlerTests"
dotnet test CoreProxy.Server.Orleans.Tests/CoreProxy.Server.Orleans.Tests.csproj -c Debug \
  --filter "Name~SlowTargetResponseAfterClientHalfCloseIsNotTruncated"

# 客户端测试
dotnet test CoreProxy.Exe.Tests/CoreProxy.Exe.Tests.csproj -c Debug
```

测试框架是 **MSTest**。`TargetResolverTests` 走**真实系统 DNS**，离线环境会假性失败；CI 用 `--filter "FullyQualifiedName!~TargetResolverTests"` 排除这组。DNS 缓存/吞吐测试已改为注入固定解析委托，不依赖真实网络。

**并发压测**（`tools/bench`）：对运行中的服务端做阶梯并发建连 + echo 校验。`--batch` 必须 >0 分批建连，否则一次性建 8000+ 条流会把压测端自己打成建连风暴。

```bash
# 直连服务端 gRPC（明文 h2c）
dotnet run -c Release --project tools/bench -- \
  --server http://127.0.0.1:6110 --steps 8,16,32,64,128,256,512,1024 \
  --hold-ms 2000 --batch 64

# 端到端：压测端 → 客户端 SOCKS5 → gRPC → 服务端 → echo 目标
dotnet run -c Release --project tools/bench -- --no-build -- \
  --socks 127.0.0.1:11080 --server http://127.0.0.1:6110 \
  --steps 16,64 --payload 262144 --hold-ms 5000 --batch 16 \
  --server-pid <服务端PID> --client-pid <客户端PID>
```

常用参数：`--payload N`（单条消息字节数）、`--write-chunk N`（模拟浏览器小块写）、`--interval-ms N`（低流量保活）、`--server-pid/--client-pid`（采样 CPU/内存）。输出同时给出服务端、客户端、压测端三方 CPU。

服务端 `Http2MaxStreamsPerConnection` 由 `appsettings.json` 配置（默认 100）；bench 按每连接 ≤8 流分散到多个 channel，低于该上限，不会触发 REFUSED_STREAM。

## 服务端架构（CoreProxy.Server.Orleans）

`Program.cs` 是**组合根**（约 120 行）：只负责创建 builder、注册 DI、按顺序挂中间件、映射 gRPC/Hub 与端点。具体职责拆在：

- `Startup/KestrelConfigurator.cs` — Kestrel 端口协议、HTTP/2 窗口、保活、缓冲与帧大小。
- `Startup/CertificateFactory.cs` — 自签/读取 pfx、生成客户端口令。
- `Endpoints/AdminEndpoints.cs`、`Endpoints/WelcomeEndpoints.cs` — 管理 API 与首页路由。
- `Services/Admin/AdminApi*.cs` — 管理 API 按端点拆分的 partial 实现；`AdminDtos.cs` 与 `AdminJsonContext.cs` 是 AOT source-gen 的 JSON 类型。
- `Models/CoreProxyOptions.cs` — 类型化配置（手工解析，AOT 友好），替代散落的 `int.TryParse`。

关键行为：

- **协议与端口**：HTTPS 端点 `Http1AndHttp2`（ALPN 协商）；明文端点只允许 `Http2`（h2c prior knowledge，供 gRPC/bench 使用，HTTP/1.1 客户端会得到 400）。生产端口 `6099`，bench 常用 `6110`。
- **HTTP/2 流量控制窗口**：`InitialConnectionWindowSize=64MB`、`InitialStreamWindowSize=32MB`、`MaxFrameSize=16777215`，故意调大以跑满带宽。
- **保活**：`KeepAlivePingDelay=30s` / `KeepAlivePingTimeout=20s`。重置计时器的是任意入站帧，所以只能探测"完全静默"连接；真实回收耗时 ≈ Delay + Timeout。
- **证书**：`CertificateFactory` 在 `BaseDirectory/ssl/` 下自签/读取 pfx，SAN 覆盖随机域名、`localhost`、主机名与全部网卡 IP；客户端口令 = 证书原始字节的 SHA-256 十六进制串，写入 `ssl/client-password.txt`，非回环连接需在 `Authorization: Password <hash>` 带上（`TunnelGrpcService.CheckPassword`）。
- **管理 API 鉴权**：`/admin`、`/api/status`、`/api/devices`、`/api/proxy-link` 需要 HTTP Basic（不回环豁免）。**`/api/invoke` 有意匿名开放**，鉴权由调用方网络边界负责——这是产品决策，不要"顺手补上"。
- **进程内登记表**：`Internal/TunnelRegistry` 与 `Internal/DeviceRegistry` 是 DI 单例，替代旧的静态全局字典；管理页/`GetStatus` 通过它们读取快照。
- **性能相关**：服务端 csproj 固化 `System.GC.LOHThreshold=8MB`（大包 protobuf 载荷不进 LOH，AOT 实测大包吞吐 +38%）；`TunnelGrpcService.HandlerServerAsync` 初始 16KB 缓冲、读满后升级到 256KB，并复用 `HttpData` 实例。

服务端各能力组件：

- **`TunnelGrpcService.StreamHandler`（隧道核心）**：双向 gRPC 流 ↔ 目标 TCP。`TargetResolver` 解析目标（带 TTL 缓存）→ `TargetConnection.ConnectAsync` 建真实 TCP → 客户端循环与目标循环双向转发。半关闭/FIN 与"5 秒收尾窗口 vs 无限等待"的注释在 [TunnelGrpcService.cs](CoreProxy.Server.Orleans/Services/TunnelGrpcService.cs)，这是最容易改错、测试覆盖最密的逻辑。
- **`TargetConnection`（Internal）**：刻意**不用** Kestrel 的 `IConnectionFactory`，而是自持裸 `Socket`。`FlushAsync` 返回时数据可能还没进内核，半关闭补发的 FIN 会抢在数据前面截断流（实测 423905 字节丢 565 字节）；裸 `SendAsync` 返回即已交内核，才能保证 FIN 顺序。
- **`TargetResolver`（Internal）**：目标主机名解析 + `IMemoryCache` TTL（默认 60s，`TargetDnsCacheTtlSeconds=0` 禁用）。走 net11 `DnsResolver`（使用系统 DNS，未指定 DNS 服务器）；构造函数第三个参数可注入解析委托（测试用），生产不传。IP 字面量直接返回、不进缓存；无可用地址抛 `InvalidArgument`；`PickPreferred` 固定取一条（IPv4 优先）。
- **SignalR**：`ChatHub` 维护 `DeviceRegistry`（以 ConnectionId 为键，同一 DeviceId 允许多条连接）。`/api/devices` 与 `/api/invoke` 读取该登记表。
- **YARP 反代**：`Services/YarpProxy.cs`，token 由 `ProxyTokenProtector` 用证书口令派生密钥加密。

## 测试策略（重要）

`CoreProxy.Server.Orleans.Tests` 是**活的传输层测试**，不 mock：

- `Infrastructure/Tunnel.cs` 把真实 `TunnelGrpcService.StreamHandler`、假的双向流/`ServerCallContext`、**真实的 Kestrel socket 目标连接**（`TestTargetServer`）装配起来，逐字节校验转发、半关闭 FIN、取消/停机、口令、建连超时。
- 每个 `Tunnel` 自带 `TunnelRegistry` / `DeviceRegistry` 并注入服务；测试断言注册表状态，不再依赖静态全局状态。
- DNS/TTL 用例走真实网络，见上面的 CI 排除说明。
- 随机分片/随机大小的压力测试（`RandomizedBidirectionalStress`、`FinArrivesAfterAllDataWhenTargetReadsSlowly`）专门抓"半关闭丢尾字节"这类偶发 bug。
- 改转发/半关闭/建连逻辑后，`StreamHandlerTests` 是必跑回归集；改解析后跑 `TargetResolverTests`。

## 验证标准

用户对"彻底消灭 bug"有硬性要求：**结论必须有可复现的实验证据**，低样本量/偶发成功不算数。涉及并发上限、吞吐、连接回收耗时这类主张时，用 `tools/bench` 与实测数据佐证，不要只凭推理。生产部署与仓库构建产物是两套（线上在 `Documents\0rdp\`），别混为一谈。

## 发布

GitHub Actions（`.github/workflows/dotnet.yml`）在 push/PR 到 main 时：

- `build-linux-arm64`：刻意留在 **debian:10（glibc 2.28）** 容器里 AOT（老库房兼容），源指向 archive.debian.org 且开启 apt 重试。勿改基础镜像。
- `build-linux-x64`：`.NET 11.0.x` 后 AOT 发布。
- `test-server`：Linux 上构建并跑服务端测试（排除 `TargetResolverTests`）。
- `test-client`：`windows-latest` 上跑客户端测试（客户端含 Windows COM 源生成器，不在 Linux 构建）。
- push 到 main 成功后自动打 `latest` release，附两个 zip。
