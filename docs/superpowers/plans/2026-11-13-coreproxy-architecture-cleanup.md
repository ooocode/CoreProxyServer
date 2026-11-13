# CoreProxy 架构整理实施计划（第一期 + 第二期）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把仓库整理成单一根解决方案、职责清晰的源文件、可注入的注册表与类型化配置，同时不改变任何转发行为。

**Architecture:** 先做纯结构整理（删遗留、拆大文件、修命名），再把进程内静态字典替换为 DI 单例注册表，最后引入类型化配置和最小共享常量项目。每一步都以现有测试和真实启动冒烟作为验收。

**Tech Stack:** .NET 11（服务端，NativeAOT）、.NET 10（客户端，NativeAOT）、ASP.NET Core/Kestrel、gRPC、SignalR、MSTest。

**Spec:** `docs/superpowers/specs/2026-11-13-architecture-cleanup-design.md`

## Global Constraints

- 不改变 gRPC 隧道、半关闭、DNS、YARP、SignalR 的运行时行为。
- `/api/invoke` 保持匿名可用；不要恢复鉴权，只把意图写清楚并修正测试期望。
- 服务端 `net11.0`，客户端 `net10.0`；新增代码必须 AOT 兼容，配置解析不使用反射绑定。
- CI 只在 Linux 构建/测试服务端；客户端测试在 `windows-latest` 跑（COM 源生成器不在 Linux 上验证）。
- 不自动 `git commit`；每个任务以构建/测试通过为完成标准，改动留在工作区。
- 每个任务结束后运行的服务端测试至少包含：
  `dotnet test CoreProxy.Server.Orleans.Tests/CoreProxy.Server.Orleans.Tests.csproj -c Debug --filter "FullyQualifiedName!~TargetResolverTests"`
  （`TargetResolverTests` 依赖真实 DNS `114.114.114.114`，本地/CI 可能不可达。）

## Review Focus

- `/api/invoke` 的匿名意图：测试必须断言 `RequiresAuth("/api/invoke") == false`，且 `HandleInvokeAsync` 不再有一段注释掉的鉴权代码误导读者。
- 鉴权中间件顺序：拆分 `Program.cs` 后，鉴权必须仍在 `UseStaticFiles()` 之前，`/admin.html` 不能被静态文件中间件绕过。
- 注册表快照：`TunnelRegistry`/`DeviceRegistry` 的枚举必须基于快照数组，不能直接返回 `ConcurrentDictionary.Values` 给异步消费者。
- 测试隔离：删除静态字典后，每个测试必须显式创建并共享自己的注册表实例；不能留下任何 `static` 默认实例。
- 重命名完整性：`GloableSessionsManager` → `GlobalSessionsManager`、`MyGrpcService` → `TunnelGrpcService` 必须覆盖源码、测试、日志类别与测试类名，构建必须零残留引用。
- 共享项目目标框架：`CoreProxy.Shared` 必须同时被 net10 客户端与 net11 服务端引用，且 AOT 发布仍能成功。

---

### Task 1: 根解决方案、删除遗留项目、CI 构建入口

**Files:**
- Create: `CoreProxy.sln`
- Delete: `CoreProxy.Server.Lib/`, `SignalrServerWebApp/`, `tools/linktest/`
- Delete: `CoreProxy.Exe/CoreProxy.sln`, `CoreProxy.Server.Orleans/CoreProxy.Server.Orleans.sln`
- Delete: `ServerWebApplication.csproj.user`, `Properties/PublishProfiles/`, `Properties/serviceDependencies.local.json.user`
- Modify: `.github/workflows/dotnet.yml`

**Interfaces:**
- Produces: 根 solution 路径 `CoreProxy.sln`，后续任务都用它做全量构建。

- [ ] **Step 1: 删除遗留项目与旧解决方案**

```bash
git rm -r CoreProxy.Server.Lib SignalrServerWebApp tools/linktest
git rm CoreProxy.Exe/CoreProxy.sln CoreProxy.Server.Orleans/CoreProxy.Server.Orleans.sln
git rm -r Properties ServerWebApplication.csproj.user
```

- [ ] **Step 2: 创建根解决方案并加入现有项目**

```bash
dotnet new sln -n CoreProxy
dotnet sln CoreProxy.sln add \
  CoreProxy.Server.Orleans/CoreProxy.Server.Orleans.csproj \
  CoreProxy.Server.Orleans.Tests/CoreProxy.Server.Orleans.Tests.csproj \
  CoreProxy.Exe/CoreProxy.csproj \
  CoreProxy.Exe.Tests/CoreProxy.Exe.Tests.csproj \
  tools/bench/Bench.csproj
```

- [ ] **Step 3: 验证构建**

Run: `dotnet build CoreProxy.sln -c Debug`
Expected: `0 个错误`；不再出现 `CoreProxy.Server.Lib`、`SignalrServerWebApp`、`linktest` 的构建项。

（CI 改动统一放在 Task 8：服务端测试跑在 Linux，客户端含 Windows COM，测试跑在 `windows-latest`。）

---

### Task 2: 拆分 Program.cs

**Files:**
- Create: `CoreProxy.Server.Orleans/Startup/KestrelConfigurator.cs`
- Create: `CoreProxy.Server.Orleans/Startup/CertificateFactory.cs`
- Create: `CoreProxy.Server.Orleans/Endpoints/AdminEndpoints.cs`
- Create: `CoreProxy.Server.Orleans/Endpoints/WelcomeEndpoints.cs`
- Create: `CoreProxy.Server.Orleans/Models/AppJsonSerializerContext.cs`
- Modify: `CoreProxy.Server.Orleans/Program.cs`

**Interfaces:**
- Produces:
  - `internal static class KestrelConfigurator { public static void Configure(KestrelServerOptions options, IConfiguration configuration, X509Certificate2 certificate, int? maxStreamsPerConnection); }`
  - `internal static class CertificateFactory { public static X509Certificate2 LoadOrCreate(string baseDirectory); public static CertificatePassword ComputePassword(X509Certificate2 certificate, string baseDirectory); }`
  - `internal static class AdminEndpoints { public static void Map(WebApplication app, CertificatePassword password, TimeSpan invokeTimeout); }`
  - `internal static class WelcomeEndpoints { public static void Map(WebApplication app); }`

- [ ] **Step 1: 原样搬移证书代码**

把 `Program.cs` 顶部的 `GetHttpsCertificate()`、`GetCertificatePassword()`、`GetLocalIpAddresses()` 搬进 `CertificateFactory`，逻辑与注释逐字保留，只把 `AppDomain.CurrentDomain.BaseDirectory` 改为参数 `baseDirectory`。

- [ ] **Step 2: 原样搬移 Kestrel 配置**

把 `builder.WebHost.ConfigureKestrel(...)` 整段搬进 `KestrelConfigurator.Configure`，保留所有注释与 `#if !DEBUG`。`Http2MaxStreamsPerConnection` 改为使用参数 `maxStreamsPerConnection`（本任务仍从 `configuration` 读取，Task 6 再换成 `CoreProxyOptions`）。

- [ ] **Step 3: 搬移端点映射**

- `/admin`、`/api/status`、`/api/devices`、`/api/invoke`、`/api/proxy-link` → `AdminEndpoints.Map`。
- `/`（nginx 欢迎页）→ `WelcomeEndpoints.Map`。
- `AppJsonSerializerContext` → `Models/AppJsonSerializerContext.cs`，命名空间保持 `CoreProxy.Server.Orleans`（SignalR 的 `AppJsonSerializerContext.Default` 引用不变）。

- [ ] **Step 4: 精简 Program.cs 为组合根**

保留：`WebApplication.CreateSlimBuilder`、NLog 配置、`ProxyTokenProtector.Configure`、DI 注册、`builder.Build()`、中间件顺序、`MapGrpcService`、`MapHub`、两个 `Map` 调用、`app.Run()`。中间件顺序必须保持：

```csharp
app.UseYarpReverseProxy();
app.UseWebSockets();
app.Use(auth middleware);
app.UseStaticFiles();
AdminEndpoints.Map(app, certificatePassword, adminInvokeTimeout);
WelcomeEndpoints.Map(app);
```

- [ ] **Step 5: 验证构建与启动**

Run:
```bash
dotnet build CoreProxy.Server.Orleans/CoreProxy.Server.Orleans.csproj -c Debug
dotnet test CoreProxy.Server.Orleans.Tests/CoreProxy.Server.Orleans.Tests.csproj -c Debug --filter "FullyQualifiedName~StreamHandlerTests"
```
Expected: 构建 0 错误；StreamHandlerTests 全通过。

冒烟：
```bash
cd CoreProxy.Server.Orleans/bin/Debug/net11.0 && dotnet CoreProxy.Server.Orleans.dll --urls http://127.0.0.1:6110
curl -s -o /dev/null -w "%{http_code}\n" http://127.0.0.1:6110/           # 200
curl -s -o /dev/null -w "%{http_code}\n" http://127.0.0.1:6110/admin.html # 401
curl -s -o /dev/null -w "%{http_code}\n" http://127.0.0.1:6110/api/status # 401
```

---

### Task 3: 拆分 AdminApi 并对齐 `/api/invoke` 的匿名意图

**Files:**
- Create: `CoreProxy.Server.Orleans/Services/Admin/AdminApi.cs`
- Create: `CoreProxy.Server.Orleans/Services/Admin/AdminApi.Auth.cs`
- Create: `CoreProxy.Server.Orleans/Services/Admin/AdminApi.Status.cs`
- Create: `CoreProxy.Server.Orleans/Services/Admin/AdminApi.Devices.cs`
- Create: `CoreProxy.Server.Orleans/Services/Admin/AdminApi.Invoke.cs`
- Create: `CoreProxy.Server.Orleans/Services/Admin/AdminApi.ProxyLink.cs`
- Create: `CoreProxy.Server.Orleans/Services/Admin/AdminDtos.cs`
- Create: `CoreProxy.Server.Orleans/Services/Admin/AdminJsonContext.cs`
- Delete: `CoreProxy.Server.Orleans/Services/AdminApi.cs`
- Modify: `CoreProxy.Server.Orleans.Tests/AdminApiTests.cs`

**Interfaces:**
- Consumes: 现有 `AdminApi` 公开方法签名保持不变（`RequiresAuth`、`CheckBasicAuth`、`WriteUnauthorized`、`HandleStatus`、`HandleDevices`、`HandleInvokeAsync`、`HandleProxyLink`、`InvokeOnClient`）。
- Produces: `public static partial class AdminApi` 跨 6 个文件；DTO 与 `AdminJsonContext` 在独立文件。所有 partial 文件保持命名空间 `CoreProxy.Server.Orleans.Services`（测试的 using 不变）。

- [ ] **Step 1: 把 DTO 与 JSON context 搬到独立文件**

`StatusPayload`、`DevicePayload`、`DevicesPayload`、`InvokeRequest`、`InvokePayload`、`ProxyLinkRequest`、`ProxyLinkPayload` → `AdminDtos.cs`；`AdminJsonContext` → `AdminJsonContext.cs`。原注释保留，命名空间保持 `CoreProxy.Server.Orleans.Services`。

- [ ] **Step 2: 按职责拆分 partial class**

- `AdminApi.cs`：`public static partial class AdminApi`，只放 `BasicPrefix`、`DefaultInvokeTimeoutSeconds`、`DefaultInvokeTimeout`、`MaxTimerDelay`。
- `AdminApi.Auth.cs`：`RequiresAuth`、`CheckBasicAuth`、`WriteUnauthorized`、`RejectUnauthorized`。
- `AdminApi.Status.cs`：`HandleStatus`。
- `AdminApi.Devices.cs`：`HandleDevices`。
- `AdminApi.Invoke.cs`：`HandleInvokeAsync`、`FindReportedMethod`。
- `AdminApi.ProxyLink.cs`：`HandleProxyLink`、`TryNormalizeTarget`。

- [ ] **Step 3: 把 `/api/invoke` 的匿名意图写清楚**

在 `RequiresAuth` 中显式保留排除项并加注释：

```csharp
public static bool RequiresAuth(PathString path) =>
    path.StartsWithSegments("/admin")
    || path.StartsWithSegments("/admin.html")
    || path.StartsWithSegments("/api/status")
    || path.StartsWithSegments("/api/devices")
    // /api/invoke 有意匿名开放：外部设备/脚本需要直接调用，鉴权由调用方网络边界负责。
    || path.StartsWithSegments("/api/proxy-link");
```

在 `HandleInvokeAsync` 中删除注释掉的 `RejectUnauthorized` 代码块，改为一行说明：

```csharp
// /api/invoke 有意不做 Basic 鉴权，见 RequiresAuth 的说明。
```

- [ ] **Step 4: 修正测试期望**

在 `AdminApiTests.RequiresAuth_MatchesProtectedPathsOnly` 中，把：

```csharp
[DataRow("/api/invoke", true)]
[DataRow("/api/invoke/", true)]
```

改为：

```csharp
[DataRow("/api/invoke", false)]  // 有意匿名开放
[DataRow("/api/invoke/", false)]
```

- [ ] **Step 5: 验证**

Run:
```bash
dotnet test CoreProxy.Server.Orleans.Tests/CoreProxy.Server.Orleans.Tests.csproj -c Debug --filter "FullyQualifiedName~AdminApiTests|FullyQualifiedName~InvokeApiTests|FullyQualifiedName~ConnectionRepositoryTests"
```
Expected: 全部通过（此前失败的 2 条 invoke 路径用例现在应按新期望通过）。

---

### Task 4: 重命名 `GloableSessionsManager` 与 `MyGrpcService`

**Files:**
- Rename: `CoreProxy.Server.Orleans/Services/GloableSessionsManager.cs` → `GlobalSessionsManager.cs`
- Rename: `CoreProxy.Server.Orleans/Services/MyGrpcService.cs` → `Services/TunnelGrpcService.cs`
- Rename: `CoreProxy.Server.Orleans.Tests/MyGrpcServiceTeardownClassificationTests.cs` → `TunnelGrpcServiceTeardownClassificationTests.cs`
- Modify: `Program.cs`、`ChatHub.cs`、`AdminApi*.cs`、`Infrastructure/Tunnel.cs`、相关测试

**Interfaces:**
- Produces: `GloableSessionsManager` → `GlobalSessionsManager`（类名与文件）；`MyGrpcService` → `TunnelGrpcService`（类名与文件）。

- [ ] **Step 1: 重命名设备表**

```bash
git mv CoreProxy.Server.Orleans/Services/GloableSessionsManager.cs CoreProxy.Server.Orleans/Services/GlobalSessionsManager.cs
```

把类名与所有引用替换为 `GlobalSessionsManager`（`AdminApi*.cs`、`ChatHub.cs`、`AdminApiTests.cs`）。注释里的 "GloableSessionsManager" 同步更新。

- [ ] **Step 2: 重命名 gRPC 服务类**

```bash
git mv CoreProxy.Server.Orleans/Services/MyGrpcService.cs CoreProxy.Server.Orleans/Services/TunnelGrpcService.cs
```

替换：
- `MyGrpcService` → `TunnelGrpcService`
- `Program.cs` 中 `app.MapGrpcService<MyGrpcService>()`
- `Infrastructure/Tunnel.cs` 中 `new MyGrpcService(...)`、`NullLogger<MyGrpcService>`
- 测试类 `MyGrpcServiceTeardownClassificationTests` → `TunnelGrpcServiceTeardownClassificationTests`（文件同时改名）
- 日志/注释中的类名

- [ ] **Step 3: 验证无残留**

Run:
```bash
grep -rn "Gloable\|MyGrpcService" --include=*.cs CoreProxy.Server.Orleans CoreProxy.Server.Orleans.Tests | grep -v /obj/
dotnet build CoreProxy.sln -c Debug
dotnet test CoreProxy.Server.Orleans.Tests/CoreProxy.Server.Orleans.Tests.csproj -c Debug --filter "FullyQualifiedName!~TargetResolverTests"
```
Expected: grep 无输出；构建 0 错误；测试全通过。

---

### Task 5: 用 DI 单例替换静态全局状态

**Files:**
- Create: `CoreProxy.Server.Orleans/Internal/TunnelRegistry.cs`
- Create: `CoreProxy.Server.Orleans/Internal/TunnelRecord.cs`
- Create: `CoreProxy.Server.Orleans/Internal/DeviceRegistry.cs`
- Create: `CoreProxy.Server.Orleans/Models/DeviceInfo.cs`（搬入 `DeviceInfo` 与 `DeviceJsonContext`）
- Delete: `CoreProxy.Server.Orleans/Internal/GlobalState.cs`
- Delete: `CoreProxy.Server.Orleans/Internal/ConnectionRepository.cs`
- Delete: `CoreProxy.Server.Orleans/Services/GlobalSessionsManager.cs`（只剩管理器类，删除；`DeviceInfo`/`DeviceJsonContext` 已搬到 `Models/DeviceInfo.cs`）
- Create: `CoreProxy.Server.Orleans.Tests/TunnelRegistryTests.cs`（从 `AdminApiTests.cs` 拆出原 `ConnectionRepositoryTests`）
- Modify: `Program.cs`、`TunnelGrpcService.cs`、`ChatHub.cs`、`AdminApi*.cs`
- Modify: `CoreProxy.Server.Orleans.Tests/Infrastructure/Tunnel.cs`、`StreamHandlerTests.cs`、`AdminApiTests.cs`、`InvokeApiTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  namespace CoreProxy.Server.Orleans.Internal;

  public static class ConnectionKind { public const string Grpc = "grpc"; public const string Signalr = "signalr"; public const string Http = "http"; }

  public sealed class ConnectItem { /* 原属性不变 */ }

  public sealed record RuntimeTunnel(string ClientIp, string Target, DateTimeOffset StartedAt);

  public sealed class TunnelRegistry
  {
      public int Count { get; }
      public bool IsEmpty { get; }
      public bool TryAdd(string connectionId, ConnectItem item);
      public bool TryRemove(string connectionId, out ConnectItem? item);
      public IReadOnlyList<ConnectItem> Snapshot();
      public List<RuntimeTunnel> GetGrpcTunnels();
      internal void ClearForTests();
  }

  public sealed class DeviceRegistry
  {
      public int Count { get; }
      public bool TryAdd(string connectionId, SignalrOnlineClient client);
      public bool TryRemove(string connectionId, out SignalrOnlineClient? client);
      public IReadOnlyList<SignalrOnlineClient> Snapshot();
      internal void ClearForTests();
  }
  ```

- [ ] **Step 1: 新增注册表类型**

`Snapshot()` 必须返回 `_connections.Values.ToArray()`（或 `ToList()`）的快照，不得把实时集合暴露给调用方。`GetGrpcTunnels()` 保留原有过滤逻辑（`Kind == grpc && ClientIp != null && Target != null`）。

- [ ] **Step 2: 注册单例并注入**

`Program.cs`：
```csharp
builder.Services.AddSingleton<TunnelRegistry>();
builder.Services.AddSingleton<DeviceRegistry>();
```

端点映射改为由 DI 注入：
```csharp
app.MapGet("/api/status", (HttpContext ctx, TunnelRegistry tunnels) =>
    AdminApi.HandleStatus(ctx, certificatePassword.Password, tunnels));
app.MapGet("/api/devices", (HttpContext ctx, DeviceRegistry devices) =>
    AdminApi.HandleDevices(ctx, certificatePassword.Password, devices));
app.MapPost("/api/invoke", (InvokeRequest request, HttpContext ctx,
        IHubContext<ChatHub> hub, DeviceRegistry devices) =>
    AdminApi.HandleInvokeAsync(request, certificatePassword.Password, ctx, hub, devices,
        defaultInvokeTimeout: adminInvokeTimeout));
```

`TunnelGrpcService` 构造参数追加 `TunnelRegistry tunnelRegistry, DeviceRegistry deviceRegistry`；把 `GlobalState.Connections.*` 与 `GloableSessionsManager.SignalrOnlineClients.Values` 替换为注册表调用。

`ChatHub` 构造参数追加 `DeviceRegistry devices`，替换静态表。

`AdminApi` 方法签名：
```csharp
public static IResult HandleStatus(HttpContext ctx, string password, TunnelRegistry tunnels)
public static IResult HandleDevices(HttpContext ctx, string password, DeviceRegistry devices)
public static async Task<IResult> HandleInvokeAsync(InvokeRequest request, string password, HttpContext ctx,
    IHubContext<ChatHub> hubContext, DeviceRegistry devices,
    InvokeOnClient? invokeOnClient = null, TimeSpan? defaultInvokeTimeout = null)
```

- [ ] **Step 3: 更新测试装配**

`Infrastructure/Tunnel.cs` 增加：
```csharp
public TunnelRegistry Registry { get; }
public DeviceRegistry Devices { get; }
```
构造时创建并传给 `TunnelGrpcService`；`Tunnel.Start` 支持可选参数 `TunnelRegistry? registry = null, DeviceRegistry? devices = null`，默认新建。

测试替换：
- `GlobalState.Connections.Clear()` → 删除（每个隧道自带注册表）或 `registry.ClearForTests()`
- `GlobalState.Connections.Count` → `tunnel.Registry.Count`
- `GlobalState.Connections.IsEmpty` → `tunnel.Registry.IsEmpty`
- `GlobalState.Connections.Values.Single()` → `tunnel.Registry.Snapshot().Single()`
- `ConnectionRepository.GetGrpcTunnels()` → `registry.GetGrpcTunnels()`
- `GloableSessionsManager.SignalrOnlineClients.*` → `devices.*`

`AdminApiTests`、`InvokeApiTests`、`TunnelRegistryTests`（原 `ConnectionRepositoryTests`）每个测试内创建 `var tunnels = new TunnelRegistry(); var devices = new DeviceRegistry();`，并传给被测方法。`InvokeApiTests` 调用 `AdminApi.HandleInvokeAsync(...)` 时在 `hubContext` 之后补传 `devices`。`ConnectItemTests` 只改命名空间引用（若类型移动到 `Internal/TunnelRecord.cs` 则无改动）。

- [ ] **Step 4: 新增快照稳定性测试**

在 `TunnelRegistryTests` 增加：

```csharp
[TestMethod]
public void Snapshot_IsStableAfterMutation()
{
    var registry = new TunnelRegistry();
    registry.TryAdd("a", new ConnectItem { Kind = ConnectionKind.Grpc, ClientIp = "1.1.1.1", Target = "t:80", DateTime = DateTimeOffset.UtcNow });
    var snapshot = registry.Snapshot();
    registry.TryAdd("b", new ConnectItem { Kind = ConnectionKind.Grpc, ClientIp = "2.2.2.2", Target = "t:81", DateTime = DateTimeOffset.UtcNow });
    Assert.AreEqual(1, snapshot.Count);
    Assert.AreEqual(2, registry.Count);
}
```

- [ ] **Step 5: 验证**

Run:
```bash
dotnet build CoreProxy.sln -c Debug
dotnet test CoreProxy.Server.Orleans.Tests/CoreProxy.Server.Orleans.Tests.csproj -c Debug --filter "FullyQualifiedName!~TargetResolverTests"
grep -rn "GlobalState\|GloableSessionsManager\|ConnectionRepository" --include=*.cs CoreProxy.Server.Orleans CoreProxy.Server.Orleans.Tests | grep -v /obj/
```
Expected: 构建 0 错误；测试全通过；grep 无输出。

---

### Task 6: 类型化配置 `CoreProxyOptions`

**Files:**
- Create: `CoreProxy.Server.Orleans/Models/CoreProxyOptions.cs`
- Create: `CoreProxy.Server.Orleans.Tests/CoreProxyOptionsTests.cs`
- Modify: `Program.cs`、`TunnelGrpcService.cs`、`Infrastructure/Tunnel.cs`

**Interfaces:**
- Produces:
  ```csharp
  namespace CoreProxy.Server.Orleans.Models;

  public sealed record CoreProxyOptions(
      TimeSpan ConnectTimeout,
      TimeSpan DnsCacheTtl,
      TimeSpan AdminInvokeTimeout,
      int? Http2MaxStreamsPerConnection)
  {
      public static CoreProxyOptions FromConfiguration(IConfiguration configuration);
  }
  ```

- [ ] **Step 1: 写失败测试**

`CoreProxyOptionsTests` 覆盖：
- 空配置 → `20s`、`60s`、`900s`、`null`
- `ConnectTimeoutSeconds=5` → `5s`；`0` / 非数字 → `20s`
- `TargetDnsCacheTtlSeconds=0` → `0s`（表示禁用缓存）；`-1` → `60s`
- `AdminInvokeTimeoutSeconds=30` → `30s`；`0` → `900s`
- `Http2MaxStreamsPerConnection=100` → `100`；`0` → `null`

Run: `dotnet test ... --filter "FullyQualifiedName~CoreProxyOptionsTests"`
Expected: 编译失败（类型不存在）。

- [ ] **Step 2: 实现 `FromConfiguration`**

解析规则必须与现有代码逐条一致（`int.TryParse` + 边界判断），不得改成 `ConfigurationBinder`（AOT 不友好）。

- [ ] **Step 3: 接入**

`Program.cs`：
```csharp
var coreOptions = CoreProxyOptions.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(coreOptions);
```
`KestrelConfigurator.Configure(..., coreOptions.Http2MaxStreamsPerConnection)`；`TargetResolver` 使用 `coreOptions.DnsCacheTtl`；`AdminEndpoints.Map` 使用 `coreOptions.AdminInvokeTimeout`；`TunnelGrpcService` 构造参数把 `IConfiguration configuration` 换成 `CoreProxyOptions options`，`connectTimeout` 直接取 `options.ConnectTimeout`。

`Infrastructure/Tunnel.cs` 去掉 `ConfigurationBuilder`，改为：
```csharp
new CoreProxyOptions(connectTimeout ?? TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(900), null)
```

- [ ] **Step 4: 验证**

Run:
```bash
dotnet test CoreProxy.Server.Orleans.Tests/CoreProxy.Server.Orleans.Tests.csproj -c Debug --filter "FullyQualifiedName!~TargetResolverTests"
```
Expected: 全通过。

---

### Task 7: `CoreProxy.Shared` 共享隧道常量

**Files:**
- Create: `CoreProxy.Shared/CoreProxy.Shared.csproj`
- Create: `CoreProxy.Shared/TunnelDefaults.cs`
- Modify: `CoreProxy.Server.Orleans/CoreProxy.Server.Orleans.csproj`
- Modify: `CoreProxy.Exe/CoreProxy.csproj`
- Modify: `Services/TunnelGrpcService.cs`、`CoreProxy.Exe/Internal/GrpcTransportHandlerService.cs`
- Modify: `CoreProxy.sln`

**Interfaces:**
- Produces:
  ```csharp
  namespace CoreProxy.Shared;

  public static class TunnelDefaults
  {
      public static readonly TimeSpan TeardownWindow = TimeSpan.FromSeconds(5);
      public const string RequestedWithHeader = "X-Requested-With";
  }
  ```

- [ ] **Step 1: 创建项目**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net10.0;net11.0</TargetFrameworks>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2: 服务端与客户端引用并替换常量**

服务端与客户端 csproj 增加 `<ProjectReference Include="..\CoreProxy.Shared\CoreProxy.Shared.csproj" />`（客户端路径按实际相对路径）。

替换：
- `TimeSpan.FromSeconds(5)`（收尾窗口，服务端 `TunnelGrpcService`、客户端 `GrpcTransportHandlerService`）→ `TunnelDefaults.TeardownWindow`
- `HeaderNames.XRequestedWith`（两处隧道客户端/服务端的请求头读取与写入）→ `TunnelDefaults.RequestedWithHeader`

- [ ] **Step 3: 加入解决方案并验证两端构建**

```bash
dotnet sln CoreProxy.sln add CoreProxy.Shared/CoreProxy.Shared.csproj
dotnet build CoreProxy.sln -c Debug
dotnet test CoreProxy.Server.Orleans.Tests/CoreProxy.Server.Orleans.Tests.csproj -c Debug --filter "FullyQualifiedName!~TargetResolverTests"
dotnet test CoreProxy.Exe.Tests/CoreProxy.Exe.Tests.csproj -c Debug
```

---

### Task 8: 文档对齐、CI 测试任务、全量验证

**Files:**
- Modify: `CLAUDE.md`
- Modify: `.github/workflows/dotnet.yml`

**Interfaces:**
- Consumes: 前面所有任务产出的根 solution、注册表、配置类型、共享项目。

- [ ] **Step 1: 修正 CLAUDE.md 的漂移**

- 仓库布局：删除 `CoreProxy.Server.Lib`、`SignalrServerWebApp`、`tools/linktest`；增加 `CoreProxy.Shared`。
- 删除不存在的 `FileLogger`、`StreamHub`、`HttpMap`、`P2PStreamHandler` 描述；注明 `P2PStreamHandler` 仅存在于 proto，服务端未实现。
- `Http2MaxStreamsPerConnection` 改为"由 `Http2MaxStreamsPerConnection` 配置，默认 100"。
- 构建/测试命令统一走 `CoreProxy.sln`。
- 增加一条：`/api/invoke` 有意匿名开放。
- 注明 `TargetResolverTests` 依赖真实 DNS，CI 排除。

- [ ] **Step 2: CI 增加测试 job**

服务端在 Linux 跑，客户端含 Windows COM，单独放 `windows-latest`：

```yaml
  test-server:
    runs-on: ubuntu-22.04
    steps:
    - uses: actions/checkout@v4
    - uses: actions/setup-dotnet@v4
      with:
        dotnet-version: 11.0.x
    - run: dotnet build CoreProxy.Server.Orleans.Tests/CoreProxy.Server.Orleans.Tests.csproj -c Debug
    - run: dotnet test CoreProxy.Server.Orleans.Tests/CoreProxy.Server.Orleans.Tests.csproj -c Debug --filter "FullyQualifiedName!~TargetResolverTests"

  test-client:
    runs-on: windows-latest
    steps:
    - uses: actions/checkout@v4
    - uses: actions/setup-dotnet@v4
      with:
        dotnet-version: 10.0.x
    - run: dotnet build CoreProxy.Exe.Tests/CoreProxy.Exe.Tests.csproj -c Debug
    - run: dotnet test CoreProxy.Exe.Tests/CoreProxy.Exe.Tests.csproj -c Debug
```

不在 Linux 上构建整个 `CoreProxy.sln`：客户端使用 Windows COM 源生成器，跨平台构建未被验证过。

- [ ] **Step 3: 全量验证（Windows 本机）**

```bash
dotnet build CoreProxy.sln -c Debug
dotnet test CoreProxy.Server.Orleans.Tests/CoreProxy.Server.Orleans.Tests.csproj -c Debug
dotnet test CoreProxy.Exe.Tests/CoreProxy.Exe.Tests.csproj -c Debug
export PATH="$PATH:/c/Program Files (x86)/Microsoft Visual Studio/Installer"   # AOT 链接器需要 vswhere
dotnet publish CoreProxy.Server.Orleans/CoreProxy.Server.Orleans.csproj -c Release -r win-x64 -o /tmp/coreproxy-final
```

Expected: 构建 0 错误；服务端测试全通过（本机 DNS 可用）；客户端测试全通过；AOT 发布成功。

- [ ] **Step 4: 启动冒烟**

服务端明文 6110 + 客户端 Release/AOT 配置指向 `http://127.0.0.1:6110`，用 `tools/bench` 的 SOCKS5 模式跑一轮 `--steps 16 --payload 262144 --hold-ms 3000`，确认 0 失败、收尾 SocketCount 归零。

---

## 完成定义

- `CoreProxy.sln` 能一次构建全部 5 个项目（服务端、服务端测试、客户端、客户端测试、bench、共享项目）。
- 服务端与客户端测试全部通过（DNS 用例按环境单独处理）。
- 代码中不再有 `GlobalState`、`GloableSessionsManager`、`ConnectionRepository`、`MyGrpcService` 残留。
- `Program.cs` 不超过约 120 行，只做组合与装配。
- `AdminApi` 按端点拆分，`/api/invoke` 的匿名意图在代码与测试中一致。
- CLAUDE.md 与代码一致。
