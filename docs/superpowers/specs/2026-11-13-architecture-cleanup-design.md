# CoreProxy 架构整理设计（第一期 + 第二期）

## 目标

在不改变转发语义、协议和部署方式的前提下，把仓库从"多个部分重叠的解决方案 + 大文件 + 静态全局状态"整理成边界清晰、可单独构建和测试的结构。

## 非目标

- 不改 gRPC 隧道、半关闭、DNS 解析、YARP、SignalR 的运行时行为。
- 不改 `/api/invoke` 的鉴权策略：它**有意保持匿名可用**（外部设备/脚本直接调用），本次只把这一意图在代码和测试里写清楚。
- 不重命名 `CoreProxy.Server.Orleans` 项目和命名空间：CI 发布路径、部署脚本都依赖它，收益低、风险高。
- 不做数据面/控制面拆分、多实例、客户端多服务器 failover；这些属于第三期，需要单独目标。
- 不动 `CoreProxy.Exe` 的 COM 入口（`Com/Impl/RunImpl.cs` 当前是半成品，单独处理）。
- 不自动 `git commit`：用户未授权提交，所有改动保留在工作区，由用户统一提交。

## 约束

- 服务端 Debug/Release 均为 `net11.0`，NativeAOT 发布；客户端为 `net10.0`，同样 AOT。
- 所有现有测试（`CoreProxy.Server.Orleans.Tests`、`CoreProxy.Exe.Tests`）必须继续通过；`TargetResolverTests` 依赖真实 DNS，CI 里排除。
- AOT 兼容：新增的配置读取不得使用反射绑定；JSON 继续走 source-gen。

## 已确认的决策

| 决策 | 结论 |
|---|---|
| 遗留项目 | 直接删除 `CoreProxy.Server.Lib`、`SignalrServerWebApp`、`tools/linktest` |
| 解决方案 | 根目录新建唯一 `CoreProxy.sln`，删除三个旧 `.sln` |
| `/api/invoke` | 保持匿名；删除注释掉的鉴权代码，修正测试期望 |
| 静态状态 | `GlobalState` / `GloableSessionsManager` 改为 DI 单例 `TunnelRegistry` / `DeviceRegistry` |
| 配置 | 新增 `CoreProxyOptions`，手工解析 IConfiguration，保持现有默认值与容错规则 |
| 共享常量 | 新建 `CoreProxy.Shared`（多目标 net10/net11），先只放隧道收尾窗口和请求头名 |

## 现状证据

- 三个 `.sln` 互相部分重叠，没有根 solution；CI 只 publish，不 build/test。
- `CoreProxy.Server.Lib` 807 行，无任何项目引用；`SignalrServerWebApp`、`tools/linktest` 只在 sln 中挂名。
- `Program.cs` 374 行混合证书、Kestrel、DI、中间件、endpoint、JSON context。
- `AdminApi.cs` 386 行混合 4 个端点、DTO、鉴权和业务。
- `GlobalState`、`GloableSessionsManager` 为进程内静态可变字典；测试只能靠全局清理隔离。
- 命名/文档漂移：`Gloable` 拼写错误；CLAUDE.md 描述的 `FileLogger`、`StreamHub`、`HttpMap`、`P2PStreamHandler` 在代码中不存在；`Http2MaxStreamsPerConnection` 文档写 8、配置为 100。
- 现有测试已失败 2 条：`RequiresAuth_MatchesProtectedPathsOnly("/api/invoke", true)`，与"有意开放"的现状矛盾。
