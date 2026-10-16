# 活跃隧道管理页 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 给 CoreProxy.Server.Orleans 加一个受密码保护的 `/admin` 静态管理页 + `/api/status` JSON 端点，只展示当前活动的 gRPC 隧道（客户端 IP / 目标 / 运行时长）。

**Architecture:** 复用 `GlobalState.Connections`。把 `ConnectItem` 从单一拼接字符串 `ClientIpAddress` 拆成 `Kind`/`ClientIp`/`Target` 字段，同时把 `ClientIpAddress` 保留为按原样拼回的**派生只读属性**（保证 proto、gRPC `GetStatus` 输出、现有测试、Exe 日志一行不改）。新增一个只筛 gRPC 的查询 + 一个返回 source-gen JSON 的端点，靠一段 Basic 鉴权中间件把 `/admin` 静态文件和 `/api/status` 都挡在密码后面。

**Tech Stack:** .NET 11（AOT）、ASP.NET Core Minimal API + `UseStaticFiles`、source-generated JSON、MSTest（用 `DefaultHttpContext` 直测，不起 host）。

**Spec:** [docs/superpowers/specs/2026-10-16-active-tunnel-admin-page-design.md](../specs/2026-10-16-active-tunnel-admin-page-design.md)

## Global Constraints

- **AOT 发布**：`PublishAot=true`、`IsAotCompatible=true`。JSON 序列化必须用 source-generated（扩充现有 `AppJsonSerializerContext`），不得用反射式序列化。鉴权/端点不得依赖动态代码。
- **HTTP/2 only + HTTPS**：Kestrel 端点是 `Http2`；浏览器无法明文 h2c，页面必须经 HTTPS 访问。Debug 配置不启 HTTPS，本地验证要么切 Release、要么手工起 https 端点（已知约束，不阻塞代码，只影响手动验证方式）。
- **口令既存**：`CertificatePassword.Password`（来自 `ssl/client-password.txt`，DI 单例已注册）就是 gRPC `CheckPassword` 用的那把。页面复用同一口令，**不另造**。
- **保持 gRPC 现状**：proto `TextFile.proto`、`GetStatus` 返回的 `StatusDetail.IpAddress`（应仍是 `"{peer} {host}:{port}"`）、`StreamHandlerTests.cs:407` 的 `StringAssert.Contains(item.ClientIpAddress, ...)`、Exe 日志**都不允许改变**。实现手段：`ClientIpAddress` 保留为派生属性，拼回原串。
- **根路径 `/` 不动**：假 nginx 页面保留。
- **只列 gRPC 隧道**：`/api/status` 的 items 只含 `Kind == ConnectionKind.Grpc` 的条目；SignalR/HTTP 的记录不上榜。`GetStatus.SocketCount` 仍数整个字典，不能作页面行数基准。
- **鉴权一律要求密码（含回环/本机）**：与 gRPC `CheckPassword` 的回环放行**不同**，页面/端点对本机访问同样要求凭据。
- **前端手写、无构建**：`wwwroot/admin.html` 手写 HTML/JS/CSS，无框架无 npm 无构建步骤。

## Review Focus

1. **Basic 凭据带上非密码字符**（`Authorization: Basic` 解码后形如 `:密码` 或 `用户:密码`，或 base64 非法）——合理预期：绝不抛异常、安全地取出冒号后的密码部分去比对；基64 解码失败应当作"未通过"。
2. **密码比对不谈泄漏**（时序）——合理预期：用 `CryptographicOperations.FixedTimeEquals` 常数时间比较，而不是 `==`。（时序属性本身无法用测试断言，靠 Task 3 Step 3 的实现审查把关；Task 3 Step 1 的"错口令被拒"测试覆盖其功能面。）
3. **SignalR/HTTP 记录是否漏进 /api/status**——合理预期：`Kind != Grpc` 的一律不出现在 items；`Total` 只数 gRPC 条数。
4. **未携带凭据访问受保护路径**（含回环 IP 上）——合理预期：返回 401 + `WWW-Authenticate: Basic`，且**绝不**放行。
5. **`ClientIpAddress` 派生后与原始拼接逐字节一致**（三种写入方）——合理预期：gRPC=`"{peer} {host}:{port}"`、SignalR=`"{ip}"`（无尾空格）、HttpMap=`"{host}:{port}"`。

---
## 文件结构

- `Internal/GlobalState.cs` — 改 `ConnectItem`：加 `Kind`、`ClientIp`、`Target`；把 `ClientIpAddress` 改成派生只读属性；加 `ConnectionKind` 常量类。
- `Internal/ConnectionRepository.cs`（新）— `GetGrpcTunnels()` 查询 + `RuntimeTunnel` DTO。
- `Services/AdminApi.cs`（新）— `CheckBasicAuth`（静态、可单测）+ `HandleStatusAsync`（静态、可单测）。
- `Program.cs` — 接线：Basic 鉴权中间件 + `UseStaticFiles()` + `MapGet("/api/status", ...)`；扩充 `AppJsonSerializerContext`。
- `CoreProxy.Server.Orleans.csproj` — 确认 wwwroot 作为 Content 进入发布产物。
- `wwwroot/admin.html` — 手写静态页（前端，无自动测试，列手动验证步骤）。
- `Services/MyGrpcService.cs:92-96`、`Services/StreamHub.cs:39-43`、`Services/HttpMap.cs:61-65` — 三处写入方按 `Kind` 填新字段。
- `CoreProxy.Server.Orleans.Tests/AdminApiTests.cs`（新）、`CoreProxy.Server.Orleans.Tests/ConnectItemTests.cs`（新）。

## Task 1: 拆分 ConnectItem 模型（保留派生属性）

**Files:**
- Modify: `CoreProxy.Server.Orleans/Internal/GlobalState.cs`
- Modify: `CoreProxy.Server.Orleans/Services/MyGrpcService.cs:92-96`
- Modify: `CoreProxy.Server.Orleans/Services/StreamHub.cs:39-43`
- Modify: `CoreProxy.Server.Orleans/Services/HttpMap.cs:61-65`
- Test: `CoreProxy.Server.Orleans.Tests/ConnectItemTests.cs`

**Interfaces:**
- Consumes: 现有 `ConnectItem` 与三处写入位置。
- Produces: `ConnectionKind`（`public static class ConnectionKind { public const string Grpc = "grpc"; public const string Signalr = "signalr"; public const string Http = "http"; }`）；`ConnectItem` 增加 `public const` 不可用——`Kind` 为 `string` 属性，`ClientIp`/`Target` 为 `string?`，`ClientIpAddress` 改为 `public string ClientIpAddress => (ClientIp, Target) switch { (not null, not null) => $"{ClientIp} {Target}", _ => ClientIp ?? Target ?? "" }`。`DateTime` 保留。

- [ ] **Step 1: 写失败测试 `ConnectItemTests.cs`**

测试派生串在三种组合下与原串逐字节一致：

```csharp
[TestClass]
public class ConnectItemTests
{
    [TestMethod]
    public void DerivedClientIpAddress_CombinesBoth_WhenBothPresent() // gRPC: "{peer} {host}:{port}"
        => Assert.AreEqual("1.2.3.4 10.0.0.5:8080", new ConnectItem { Kind = ConnectionKind.Grpc, ClientIp = "1.2.3.4", Target = "10.0.0.5:8080", DateTime = DateTimeOffset.UtcNow }.ClientIpAddress);

    [TestMethod]
    public void DerivedClientIpAddress_OnlyIp_WhenTargetNull() // SignalR: "{ip}" 无尾空格
        => Assert.AreEqual("5.6.7.8", new ConnectItem { Kind = ConnectionKind.Signalr, ClientIp = "5.6.7.8", Target = null, DateTime = DateTimeOffset.UtcNow }.ClientIpAddress);

    [TestMethod]
    public void DerivedClientIpAddress_OnlyTarget_WhenClientIpNull() // HttpMap: "{host}:{port}"
        => Assert.AreEqual("10.0.0.9:9000", new ConnectItem { Kind = ConnectionKind.Http, ClientIp = null, Target = "10.0.0.9:9000", DateTime = DateTimeOffset.UtcNow }.ClientIpAddress);
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test CoreProxy.Server.Orleans.Tests/CoreProxy.Server.Orleans.Tests.csproj -c Debug --filter "FullyQualifiedName~ConnectItemTests"`
Expected: FAIL（编译错：`ConnectItem` 还没有 `Kind`/`ClientIp`/`Target`）

- [ ] **Step 3: 改 `GlobalState.cs`**

按 Interfaces 块加 `ConnectionKind` 类、给 `ConnectItem` 加 `Kind`/`ClientIp`/`Target` 并把 `ClientIpAddress` 改为派生表达式体属性（`DateTime` 保持 `required` 不动）。`ClientIp`/`Target` 非 `required`（可空）。

- [ ] **Step 4: 改三处写入方**

- `MyGrpcService.cs:92-96`：`Kind = ConnectionKind.Grpc`、`ClientIp =` 从 `context.Peer` 剥端口（`context.Peer` 形如 `1.2.3.4:54321`，注意 IPv6 `[::1]:x` 的方括号——用 `IPEndPoint.TryParse` 或剥最后一个 `:` 前段）`、`Target = $"{host}:{port}"`；删掉 `ClientIpAddress =` 赋值。
- `StreamHub.cs:39-43`：`Kind = ConnectionKind.Signalr`、`ClientIp = Context.GetHttpContext()?.Connection?.RemoteIpAddress?.ToString() ?? string.Empty`；删 `ClientIpAddress` 赋值。
- `HttpMap.cs:61-65`：`Kind = ConnectionKind.Http`、`Target = $"{host}:{port}"`；删 `ClientIpAddress` 赋值。

注意：删掉的 `ClientIpAddress =` 赋值后，派生属性自动接管，`StreamHandlerTests.cs:407`（含目标地址断言）不受影响。

- [ ] **Step 5: 跑 ConnectItemTests 确认通过**

Run: `dotnet test CoreProxy.Server.Orleans.Tests/CoreProxy.Server.Orleans.Tests.csproj -c Debug --filter "FullyQualifiedName~ConnectItemTests"`
Expected: PASS(3)

- [ ] **Step 6: 跑全量现有服务端测试确认无回归**

Run: `dotnet test CoreProxy.Server.Orleans.Tests/CoreProxy.Server.Orleans.Tests.csproj -c Debug`
Expected: 全绿（尤其 `StreamHandlerTests` 的 `ConnectionIsTrackedWhileActiveThenRemoved` 断言仍成立）

- [ ] **Step 7: Commit**

```bash
git add CoreProxy.Server.Orleans/Internal/GlobalState.cs CoreProxy.Server.Orleans/Services/MyGrpcService.cs CoreProxy.Server.Orleans/Services/StreamHub.cs CoreProxy.Server.Orleans/Services/HttpMap.cs CoreProxy.Server.Orleans.Tests/ConnectItemTests.cs
git commit -m "refactor: split ConnectItem into Kind/ClientIp/Target with derived ClientIpAddress"
```

## Task 2: gRPC 隧道查询（ConnectionRepository）

**Files:**
- Create: `CoreProxy.Server.Orleans/Internal/ConnectionRepository.cs`
- Test: `CoreProxy.Server.Orleans.Tests/AdminApiTests.cs`（本任务用到此文件的第一个测试）

**Interfaces:**
- Consumes: `ConnectItem.Kind`、`ConnectionKind.Grpc`、`ConnectItem.DateTime`、`Generate` 三字段。
- Produces: `public sealed record RuntimeTunnel(string ClientIp, string Target, DateTimeOffset StartedAt);`（`ClientIp`/`Target` 非空，只含 gRPC）；`public static class ConnectionRepository { public static List<RuntimeTunnel> GetGrpcTunnels(); }`。

- [ ] **Step 1: 写失败测试**

```csharp
[TestMethod]
public void GetGrpcTunnels_OnlyReturnsGrpcWithAllFields()
{
    GlobalState.Connections.Clear();
    GlobalState.Connections.TryAdd("g1", new ConnectItem { Kind = ConnectionKind.Grpc, ClientIp = "1.1.1.1", Target = "10.0.0.1:80", DateTime = DateTimeOffset.UtcNow });
    GlobalState.Connections.TryAdd("s1", new ConnectItem { Kind = ConnectionKind.Signalr, ClientIp = "2.2.2.2", DateTime = DateTimeOffset.UtcNow });
    GlobalState.Connections.TryAdd("h1", new ConnectItem { Kind = ConnectionKind.Http, Target = "10.0.0.2:90", DateTime = DateTimeOffset.UtcNow });

    var items = ConnectionRepository.GetGrpcTunnels();

    Assert.AreEqual(1, items.Count);
    Assert.AreEqual("1.1.1.1", items[0].ClientIp);
    Assert.AreEqual("10.0.0.1:80", items[0].Target);
    // StartedAt == 那条 gRPC 的 DateTime
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test CoreProxy.Server.Orleans.Tests/CoreProxy.Server.Orleans.Tests.csproj -c Debug --filter "Name~GetGrpcTunnels"`
Expected: FAIL（`ConnectionRepository` 未定义）

- [ ] **Step 3: 实现 `ConnectionRepository.GetGrpcTunnels()`**

筛 `Kind == ConnectionKind.Grpc`，映射到 `RuntimeTunnel`，`ClientIp!`/`Target!`。

- [ ] **Step 4: 跑测试确认通过**

Expected: PASS(1)

- [ ] **Step 5: Commit**

```bash
git add CoreProxy.Server.Orleans/Internal/ConnectionRepository.cs CoreProxy.Server.Orleans.Tests/AdminApiTests.cs
git commit -m "feat: add gRPC-tunnel-only repository query"
```

## Task 3: /api/status 端点 + Basic 鉴权

**Files:**
- Create: `CoreProxy.Server.Orleans/Services/AdminApi.cs`
- Modify: `CoreProxy.Server.Orleans/Program.cs`（鉴权中间件 + `MapGet` + 扩充 `AppJsonSerializerContext`）
- Test: `CoreProxy.Server.Orleans.Tests/AdminApiTests.cs`

**Interfaces:**
- Consumes: `ConnectionRepository.GetGrpcTunnels()`、`RuntimeTunnel`、`CertificatePassword.Password`、现有 `AppJsonSerializerContext`。
- Produces: `public static class AdminApi`：
  - `public static bool CheckBasicAuth(HttpContext ctx, string password)` — 解析 `Authorization: Basic <base64>`，解码后取 `:` 之后那段作为口令，`CryptographicOperations.FixedTimeEquals` 常数时间比对；base64 非法/缺前缀一律 false；**不抛异常**。
  - `public static Task HandleStatusAsync(HttpContext ctx)` — 若 `!CheckBasicAuth(...)` 返回 401 + `WWW-Authenticate: Basic realm="admin"`；否则写响应 JSON（见下）。

JSON 响应（包含总数）：`{ "total": N, "items": [ { "clientIp": "...", "target": "...", "startedAt": "ISO8601" } ] }`。HTTP status 恒 200；鉴权失败走 401。

- [ ] **Step 1: 写失败测试 `CheckBasicAuth`**

用 `DefaultHttpContext`（`Microsoft.AspNetCore.Http`）：

```csharp
private static DefaultHttpContext CtxWith(string header) {
    var ctx = new DefaultHttpContext();
    if (header != null) ctx.Request.Headers.Authorization = header;
    return ctx;
}

[TestMethod]
public void CheckBasicAuth_AcceptsCorrectPassword() {
    var pw = "abc123";
    var ok = AdminApi.CheckBasicAuth(CtxWith("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + pw))), pw);
    Assert.IsTrue(ok);
}

[TestMethod]
public void CheckBasicAuth_RejectsWrongPassword() {
    Assert.IsFalse(AdminApi.CheckBasicAuth(CtxWith("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("user:wrong"))), "right"));
}

[TestMethod] // Review Focus 1: 解码健壮
public void CheckBasicAuth_InvalidBase64_ReturnsFalseWithoutThrow() {
    Assert.IsFalse(AdminApi.CheckBasicAuth(CtxWith("Basic not-base64!"), "any"));
}

[TestMethod]
public void CheckBasicAuth_MissingHeader_ReturnsFalse() {
    Assert.IsFalse(AdminApi.CheckBasicAuth(CtxWith(null), "any"));
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test ... --filter "Name~CheckBasicAuth"`
Expected: FAIL（`AdminApi` 未定义）

- [ ] **Step 3: 实现 `CheckBasicAuth`**

按 Review Focus 1/2：`StartsWith("Basic ", Ordinal)` → base64 解码（`try`，失败 false）→ 取 `:` 后段（无 `:` 则整段）→ `FixedTimeEquals` 常数时间比对。用 `HttpRequest` 的 `Authorization` header。

- [ ] **Step 4: 跑测试确认通过**

Expected: PASS(4)

- [ ] **Step 5: 写失败测试 `HandleStatusAsync`（端点 + 鉴权 + 只回 gRPC）**

```csharp
[TestMethod]
public async Task HandleStatus_WithoutAuth_Returns401WithWwwAuthenticate() {
    GlobalState.Connections.Clear();
    var ctx = CtxWith(null);
    await AdminApi.HandleStatusAsync(ctx, "pw");
    Assert.AreEqual(401, ctx.Response.StatusCode);
    Assert.AreEqual("Basic realm=\"admin\"", ctx.Response.Headers.WWWAuthenticate.ToString());
}

[TestMethod]
public async Task HandleStatus_WithAuth_ReturnsOnlyGrpcTunnels() {
    GlobalState.Connections.Clear();
    GlobalState.Connections.TryAdd("g", new ConnectItem { Kind = ConnectionKind.Grpc, ClientIp = "1.1.1.1", Target = "10.0.0.1:80", DateTime = DateTimeOffset.UtcNow });
    GlobalState.Connections.TryAdd("s", new ConnectItem { Kind = ConnectionKind.Signalr, ClientIp = "2.2.2.2", DateTime = DateTimeOffset.UtcNow });
    var pw = "pw";
    var ctx = CtxWith("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(":" + pw)));
    ctx.Response.Body = new MemoryStream();
    await AdminApi.HandleStatusAsync(ctx, pw);
    ctx.Response.Body.Position = 0;
    var json = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
    Assert.AreEqual(200, ctx.Response.StatusCode);
    Assert.IsTrue(json.Contains("\"clientIp\":\"1.1.1.1\""));   // gRPC 在
    Assert.IsFalse(json.Contains("2.2.2.2"));                    // SignalR 不在
    Assert.IsTrue(json.Contains("\"total\":1"));                // total 只数 gRPC
}
```

> 注：`HandleStatusAsync` 需要拿到 password。签名定 `Task HandleStatusAsync(HttpContext ctx, string password)`，测试直接传；接线处由 DI 注入。
> `Convert`/`Encoding` 需要 `using System.Text;`；测试项目已有 implicit usings。若 `Using System.Text` 缺失在测试顶部补。

- [ ] **Step 6: 跑测试确认失败**

Run: `dotnet test ... --filter "Name~HandleStatus"`
Expected: FAIL（`HandleStatusAsync` 未定义）

- [ ] **Step 7: 实现 `HandleStatusAsync` + AOT JSON**

用 source-gen：在 `Program.cs` 的 `AppJsonSerializerContext` 增加 `JsonSerializable(typeof(StatusPayload))` 等。`AdminApi.cs` 内定义响应类型（或放 Models）。序列化用 `JsonSerializer.Serialize(ctx.Response.Body, payload, AppJsonSerializerContext.Default.StatusPayload)`。响应对象 + `RuntimeTunnel` 需加入 source-gen 上下文（RuntimeTunnel 已在 Task 2，须一起注册）。`startedAt` 序列化为 ISO8601（`DateTimeOffset` 默认即可，AOT 下 source-gen 支持 `DateTimeOffset`）。

- [ ] **Step 8: 跑测试确认通过**

Run: `dotnet test ... --filter "Name~StatusOrHandleStatus"`
Expected: PASS

- [ ] **Step 9: 接线 Program.cs（静态中间件 + Route）**

`app.MapGet("/api/status", (HttpContext ctx) => AdminApi.HandleStatusAsync(ctx, certificatePassword.Password));` 保护 `/admin` 与 `/api/status` 的 Basic 中间件放在 `UseStaticFiles` 之前：

```csharp
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/admin") || ctx.Request.Path == "/api/status")
    {
        if (!AdminApi.CheckBasicAuth(ctx, certificatePassword.Password))
        {
            ctx.Response.StatusCode = 401;
            ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"admin\"";
            return;
        }
    }
    await next();
});
app.UseStaticFiles();
```

`certificatePassword` 已在顶层可用（`Program.cs`）。静态文件中间件默认只服务已知扩展名 html，`wwwroot` 需存在（Task 4 建）。

- [ ] **Step 10: 编译 + 跑测试**

Run: `dotnet build CoreProxy.Server.Orleans/CoreProxy.Server.Orleans.csproj -c Debug` 然后 `dotnet test ... -c Debug`
Expected: build 绿、测试全绿

- [ ] **Step 11: Commit**

```bash
git add CoreProxy.Server.Orleans/Services/AdminApi.cs CoreProxy.Server.Orleans/Program.cs CoreProxy.Server.Orleans.Tests/AdminApiTests.cs
git commit -m "feat: add password-protected /api/status and Basic auth middleware"
```

## Task 4: wwwroot 静态页 + 静态文件伺服

**Files:**
- Create: `CoreProxy.Server.Orleans/wwwroot/admin.html`
- Modify: `CoreProxy.Server.Orleans/CoreProxy.Server.Orleans.csproj`（如需，确认 wwwroot 进发布产物）
- Modify: `CoreProxy.Server.Orleans/Program.cs`（若 Task 3 未含 `UseStaticFiles`——已含，无需再改）

> 无自动测试（前端手写）。本任务以手动验证步骤收尾。

**Interfaces:**
- Consumes: `/api/status`（JSON 结构、Basic 已在同源 cookie/凭据内存中）、鉴权已由服务端完成。
- Produces: 无（纯静态交付物）。

- [ ] **Step 1: 写 `wwwroot/admin.html`**

手写单文件 HTML（内联 CSS/JS，无外部依赖、无构建）。功能：
- 顶部"活跃连接总数" + "刷新"按钮；另加 5s 自动轮询（初始后可见倒计时不清零即可，属留给我的装饰，不要求）。
- 用 `fetch('/api/status', { credentials: 'same-origin' })` 拉数据；`credentials: 'same-origin'` 让浏览器自动带上 Basic 缓存凭据（前端 JS 不存密码明文）。
- 渲染为表格：列 = 客户端 IP / 目标 / 已运行时长。运行时长用 JS 从 `startedAt` 每秒刷新为 `1h 23m` / `4m 12s` 格式。
- 空态：无 gRPC 隧道时显示"暂无活动隧道"。
- 中文界面、干净极简样式。
- 收到 401 时提示"请刷新页面输入密码"（正常情况下浏览器已弹 Basic 框，JS 侧仅作兜底提示）。

- [ ] **Step 2: 确认 csproj 把 wwwroot 纳入内容**

`Microsoft.NET.Sdk.Web` 默认把 `wwwroot/**` 作为 Content 复制进输出与 AOT 发布。仅当构建产物里未见 `wwwroot/admin.html` 时，才在 csproj 显式加 `<Content Include="wwwroot/**" CopyToOutputDirectory="PreserveNewest" />`。

- [ ] **Step 3: 构建确认静态文件入产物**

Run: `dotnet build CoreProxy.Server.Orleans/CoreProxy.Server.Orleans.csproj -c Debug`
Expected: 构建绿；`bin/Debug/net11.0/wwwroot/admin.html` 存在

- [ ] **Step 4: 手动验证（浏览器）**

1. 以能启 https 的方式跑起服务端（Release 或手工 https 端点），`tools/bench` 起几条隧道。
2. 未带凭据访问 `https://.../api/status` → 401；访问 `/admin` → Basic 弹框，取消/错口令被拒。
3. 输入正确密码（`ssl/client-password.txt` 内容）→ 进入 `/admin`，看到"活跃连接：N"即 bench 起的隧道数；每行客户端 IP / 目标 / 时长正确、时长每秒递增。
4. 停掉 bench → 行数归零。
5. 回环地址（本机 IP 访问）同样要求密码（要点：鉴权不加 `IsLoopback` 豁免，与 gRPC `CheckPassword` 行为不同）。
6. F12 检查前端源码：无真密码、无服务端校验可绕过。

- [ ] **Step 5: Commit**

```bash
git add CoreProxy.Server.Orleans/wwwroot/admin.html CoreProxy.Server.Orleans/CoreProxy.Server.Orleans.csproj
git commit -m "feat: add password-protected /admin static management page"
```

## 执行

计划完成后按执行方式选择走 `superpowers:executing-plans`（Native）或 `superpowers:subagent-driven-development`（Subagent-driven）。
