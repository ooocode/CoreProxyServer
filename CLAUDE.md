# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 概览

这是一个**内网穿透 / TCP 转发代理**仓库：客户端（`CoreProxy.Exe`）通过一条 gRPC 双向流把流量发到服务端（`CoreProxy.Server.Orleans`），服务端把每条流桥接成一条到目标服务器的真实 TCP 连接。仓库是一个多项目 .NET 仓库（git 根在上一级），核心与长期演进目标是 `CoreProxy.Server.Orleans`（服务端，Linux 部署，AOT 编译）。

仓库布局（git 根 `CoreProxyServer/`）：

- **`CoreProxy.Server.Orleans/`** — 服务端。本仓库的现代核心：gRPC 隧道（`Greeter.StreamHandler`）、SignalR Hub、HTTP 转发、自定义文件日志。当前的工作焦点。
- **`CoreProxy.Server.Orleans.Tests/`** — 服务端 MSTest 测试。
- **`CoreProxy.Exe/`** — Windows 客户端/分析器，反向连接服务端（旧的演进路径，与服务端有并行实现；改动前先确认是否应改服务端）。
- **`CoreProxy.Server.Lib/`** — 更早的服务端存留，多数逻辑已迁往 Orleans 项目，多数为遗留代码。
- **`SignalrServerWebApp/`** — 独立 SignalR 设备网关应用（另有自己的入口）。
- **`tools/bench/`** — 并发压测客户端，用于验证服务端并发承载上限。
- **`proto/TextFile.proto`** — 全仓库共享的**唯一**协议定义，被服务端/客户端/bench 三处同时引用，改动协议必须三处同步、防止漂移（`CoreProxy.Server.Orleans.csproj` 用 `..\proto\TextFile.proto` 引入）。

协议关键点：`service Greeter` 有 `StreamHandler`（隧道）、`P2PStreamHandler`（p2p）、`GetStatus`、`GetSignalrOnlineClients`；`HttpData { bytes Payload; int64 UnixTimeMilliseconds }`。

## 构建与测试命令

**目标框架随配置切换**（csproj 刻意如此，测试项目镜像同一逻辑）：`Debug` → `net10.0`，`Release` → `net11.0`（预览版 SDK）。本地日常开发用 `Debug`。CI 用 `.NET 11.0.x` 预览版 SDK 做 `-c Release` 的 AOT 发布。

```bash
# 构建（在仓库根或 CoreProxy.Server.Orleans 下）
dotnet build CoreProxy.Server.Orleans/CoreProxy.Server.Orleans.csproj -c Debug

# 跑全部服务端测试
dotnet test CoreProxy.Server.Orleans.Tests/CoreProxy.Server.Orleans.Tests.csproj -c Debug

# 跑单个用例（按方法名或类名过滤）
dotnet test CoreProxy.Server.Orleans.Tests/CoreProxy.Server.Orleans.Tests.csproj -c Debug \
  --filter "FullyQualifiedName~StreamHandlerTests"
dotnet test CoreProxy.Server.Orleans.Tests/CoreProxy.Server.Orleans.Tests.csproj -c Debug \
  --filter "Name~SlowTargetResponseAfterClientHalfCloseIsNotTruncated"
```

测试框架是 **MSTest**（`[TestClass]`/`[TestMethod]`，`DataTestMethod` 用于参数化）。测试直接引用被测项目，经真实 socket 走完整 `StreamHandler`，不 mock 传输层（见下）。

**并发压测**（`tools/bench`）：用 gRPC 客户端对运行中的服务端做阶梯并发建连 + echo 校验。`--batch` 必须>0 分批建连，否则一次性建 8000+ 条流会把客户端自己打成建连风暴、误判成服务端上限。另注意服务端 `Http2MaxStreamsPerConnection=8`，客户端按每连接 ≤8 流分散到多个 channel。

```bash
# 先启动服务端（默认端口见下），再：
dotnet run -c Release --project tools/bench -- \
  --server http://127.0.0.1:6110 --steps 8,16,32,64,128,256,512,1024 \
  --hold-ms 2000 --batch 64
```

## 服务端架构（CoreProxy.Server.Orleans）

入口在 [Program.cs](CoreProxy.Server.Orleans/Program.cs)，Kestrel 配置全部内联于此，是理解行为的关键：

- **协议与端口**：HTTP/2 only。生产端口 `6099`（`appsettings.json` 里 `Urls`），bench 常用 `6110`。`Http2MaxStreamsPerConnection=8`。
- **HTTP/2 流量控制窗口是"核心中的核心"**：`InitialConnectionWindowSize=64MB`、`InitialStreamWindowSize=32MB`、`MaxFrameSize=16777215`，故意调大以跑满带宽。
- **保活语义**：`KeepAlivePingDelay=30s` / `KeepAlivePingTimeout=20s`。重置计时器的是任意入站帧，所以只能探测"完全静默"连接；真实回收耗时 ≈ Delay+Timeout。改保活前先读 `kestrel-http2-keepalive-semantics` 相关注释与线上结论。
- **证书**：`GetHttpsCertificate()` 在 `BaseDirectory/ssl/` 下自签/读取 pfx；客户端口令 = 证书原始字节的 SHA-256 十六进制串，写成 `ssl/client-password.txt`。非回环 IP 的连接需在 `Authorization: Password <hash>` 里带上它（`MyGrpcService.CheckPassword`）。

服务端各能力组件（`Services/` 与 `Internal/`）：

- **`MyGrpcService.StreamHandler`（隧道核心）**：双向 gRPC 流 ↔ 目标 TCP。`TargetResolver` 解析目标（带 TTL 缓存）→ `TargetConnection.ConnectAsync` 建真实 TCP → 客户端循环与目标循环双向转发。切换/校对半关闭行为时重点读 [MyGrpcService.cs](CoreProxy.Server.Orleans/Services/MyGrpcService.cs) 内关于"是否补发 FIN"、"5 秒收尾窗口 vs 无限等待"的注释——这是最容易改错、测试覆盖最密的逻辑。
- **`TargetConnection`（Internal）**：刻意**不用** Kestrel 的 `IConnectionFactory`/`SocketConnectionContextFactory`，而是自持一个裸 `Socket`。原因：`FlushAsync` 返回时数据可能还没进内核，半关闭补发的 FIN 会抢在数据前面把流截断（实测 423905 字节丢 565 字节）。用裸 `SendAsync`（返回即已交内核）才能硬保证 FIN 顺序。
- **`TargetResolver`（Internal）**：目标主机名解析 + TTL 缓存（默认 60s，可用 `TargetDnsCacheTtlSeconds` 配 0 禁用）。刻意不做 single-flight。构造参数可注入替身解析器与 `TimeProvider` 供测试。
- **`GlobalState`（Internal）**：静态 `ConcurrentDictionary`，记录活跃连接供 `GetStatus` 查询。它是进程内静态状态，跨连接的生命周期清理都围绕它。
- **`FileLogger`（Internal）**：自定义异步文件日志 —— 有界队列（满即丢、可计数）+ 后台消费/flush 服务。`StopAsync` 有停机保证（先 `queue.Complete()` 再兜底排空），且 `ExecuteAsync` 的 I/O 异常绝不冒泡成宿主故障。.NET 10 的 `BackgroundService.StartAsync` 不再同步执行 `ExecuteAsync`，停机处理依赖这一语义。
- **SignalR Hub**：`ChatHub` 维护设备在线表（`GloableSessionsManager.SignalrOnlineClients`）；`StreamHub.EchoStream` 走 `IConnectionFactory`（注意与隧道核心不同，这里用是 Kestrel 工厂）；`P2PStreamHandler` 用 Channel 做 master/slave 会话桥接。
- **`HttpMap`**：`/stream`（GET 建隧道）+ `/push/{connectionId}`（POST 写流量）的纯 HTTP 转发路径，用 `connectionFactory`。

## 测试策略（重要）

`CoreProxy.Server.Orleans.Tests` 的测试是**活的传输层测试**，不是 mock：

- `Infrastructure/Tunnel.cs` 把真实 `MyGrpcService.StreamHandler` 与假的流/`ServerCallContext`、**真实的 Kestrel socket 目标连接**（`TestTargetServer`，用真 Socket 扮演目标服务器）装配起来，逐字节校验转发正确性、半关闭 FIN、取消/停机、口令校验、建连超时等。
- 可复现性优先：`TargetResolver`/`FileLogger` 测试注入 `TimeProvider`（手动时钟）与替身解析器；`FileLogger` 用临时目录。
- 有随机分片/随机大小的压力测试（`RandomizedBidirectionalStress`、`FinArrivesAfterAllDataWhenTargetReadsSlowly`），专门抓"半关闭丢尾字节"这类偶发 bug。
- 改转发/半关闭/建连逻辑后，`StreamHandlerTests` 是必跑回归集；改日志后跑 `FileLoggerTests`；改解析后跑 `TargetResolverTests`。

## 验证标准

用户对"彻底消灭 bug"有硬性要求：**结论必须有可复现的实验证据**，低样本量/偶发成功不算数（见 memory：验证标准-实验证据优先）。涉及并发上限、吞吐、连接回收耗时这类主张时，用 `tools/bench` 与实测数据佐证，不要只凭推理。生产部署与仓库构建产物是两套（线上在 `Documents\0rdp\`），别混为一谈。

## 发布

GitHub Actions（`.github/workflows/dotnet.yml`）在 push/PR 到 main 时构建：
- linux-arm64：刻意留在 **debian:10（glibc 2.28）** 容器里 AOT（老库房兼容），源已改指 archive.debian.org 且开启 apt 重试。勿改基础镜像。
- linux-x64：`setup-dotnet 11.0.x` 后 AOT 发布。
- push 到 main 成功后自动打 `latest` release，附两个 zip。
