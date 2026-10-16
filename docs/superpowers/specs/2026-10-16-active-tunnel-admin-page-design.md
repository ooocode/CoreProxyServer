# 活跃隧道管理页 设计文档

日期：2026-10-16
目标项目：`CoreProxy.Server.Orleans`

## 意图与由来

服务端现在把每条 gRPC 隧道连到哪个目标、从什么时候开始跑，全都只存在于 `GlobalState.Connections` 这个进程内静态字典里（[GlobalState.cs](../Internal/GlobalState.cs)），唯一的出口是 gRPC 的 `GetStatus(IncludeDetail=true)`——要看它得写代码调 gRPC，运维时等于看不见。

想要的是一块网页：打开就能看到**当前正在跑的每一条隧道**——是谁连来的、连去哪、跑了多久。不涉及历史记录，只看当下活着的连接。

## 目标（Goals）

1. 提供一个网页 `/admin`，一屏展示当前所有活跃隧道的清单。
2. 每行三列核心信息：**客户端 IP**、**目标地址（host:port）**、**已运行时长**（实时递增）。
3. 顶部显示**活跃连接总数**，并提供**手动刷新**按钮。
4. 访问必须**输入密码**才能进入；页面和数据端点都受保护，公网可直接部署。

## 非目标（Non-goals）

- 不做历史连接记录、趋势图、流量统计。页面只反映"当前活着"的连接。
- 不做连接管理动作（不断开、不限速、不封 IP）。这是只读页面。
- 不显示设备名 / 机器名 / 用户名（客户端建流时未发送这些信息，见下）。
- 不引入前端框架、构建流程、npm 依赖。手写 HTML/JS/CSS。
- 不把 SignalR 在线设备表并进来。
- **只列 gRPC `StreamHandler` 隧道**；SignalR `EchoStream` 与 HTTP `/stream` 产生的连接记录不上榜（它们缺目标/IP，见"榜单范围"节）。

## 反目标（Anti-goals）——技术上能跑起来也算失败的情况

- **页面裸奔**：任何人访问 `/admin` 都能看到全站流量的客户端与目标去向。哪怕页面能正常显示，只要鉴权没真正挡住，就算失败。
- **鉴权只做在页面里**：把密码校验写在 HTML/JS 中，用户 F12 即可绕过。凡是"浏览器侧校验"一律视为失败。
- **为这页改坏隧道转发逻辑**：为腾字段而动了 `StreamHandler` 的半关闭/收尾路径，导致丢尾字节。转发正确性优先级高于本页。
- **页面解析靠猜字符串**：让前端去 `Split(' ')` 那个拼接字段。脆弱且是历史遗留写法。（gRPC 侧 `GetStatus` 仍拼串**不是**违反本条——那是既有协议形态，保持不动；页面数据则走拆分后的字段，绝不解析拼接串。）

## 约束（Constraints）

- **HTTP/2 only**：Kestrel 端点默认 `Protocols = HttpProtocols.Http2`（[Program.cs:58](../Program.cs#L58)），且 `app.UseWebSockets()` 已在。浏览器**不支持明文 h2c**，所以页面必须经 **HTTPS** 访问。Debug 配置下 HTTPS 证书默认不挂（`#if !DEBUG`，[Program.cs:60-62](../Program.cs#L60-L62)），本地调试要么切 Release、要么自己起 https 端点。
- **证书是自签的**（[Program.cs:177-216](../Program.cs#L177-L216)），浏览器访问会弹安全警告。已知、可接受，不阻塞需求。
- **口令已存在**：`BaseDirectory/ssl/client-password.txt`，内容是证书原始字节的 SHA-256 十六进制串；gRPC 侧以 `Authorization: Password <hash>` 携带（[MyGrpcService.cs:37-51](../Services/MyGrpcService.cs#L37-L51)）。本页复用同一口令，不另造。
- **根路径 `/` 已被占用**：现在是伪装成 nginx 欢迎页的静态 HTML（[Program.cs:139-167](../Program.cs#L139-L167)），**保持不动**。
- **AOT 发布**：生产走 AOT 编译（Linux）。新增的服务端代码必须 AOT 友好——JSON 序列化要用 source-generated（现有 `AppJsonSerializerContext` 已是这个模式），避免反射式序列化。
- 上线实例在 `Documents\0rdp\`，与仓库构建产物是两套，本设计只针对仓库代码。

## 已决定的"怎么做"

### 路径与命名（用户已拍板）

- 页面：`wwwroot/admin.html`，访问路径 `/admin`。
- 数据端点：`/api/status`。
- 根路径 `/` 的假 nginx 页面保留不动。

### 界面（用户已拍板）

- **纯清单**形态：一屏一张表，顶部一行"活跃连接：N" + 刷新按钮。
- 三列：客户端 IP、目标地址、已运行时长。运行时长显示为相对时长（`1h 23m`、`4m 12s`），不是时间戳。
- 中文界面。
- 手写 HTML/JS/CSS，无框架、无构建步骤。

### 鉴权（用户已拍板：必须输密码，公网可开）

- 服务端中间件拦截，**不依赖浏览器侧校验**。
- 复用 `client-password.txt` 的同一口令。
- **不做回环豁免**：与 gRPC 的 `CheckPassword` 不同（后者对 `IPAddress.IsLoopback` 直接放行），页面鉴权对**本机访问也一律要求密码**（用户已拍板）。目的是本地自测也能观察到真实的 401、能验证鉴权路径确实生效。
- 未通过时返回 401 或一个登录页；通过后放行静态文件；`/api/status` 同样受保护。
- 具体形态（HTTP Basic / Bearer token / 表单换 cookie）见下节"留给构建者的决定"。

### 榜单范围（用户已拍板：只列 gRPC 隧道）

`ConnectItem` 有三处写入方，每处存的字段不同（这是设计复查发现的）：

- gRPC 隧道 `StreamHandler`：`"{peer} {host}:{port}"`，客户端 + 目标都有（[MyGrpcService.cs:92-96](../Services/MyGrpcService.cs#L92-L96)）。
- SignalR `EchoStream`：只有远程 IP，没有目标（[StreamHub.cs:39-43](../Services/StreamHub.cs#L39-L43)）。
- HTTP `/stream`：只有 `host:port`，没有客户端 IP（[HttpMap.cs:61-65](../Services/HttpMap.cs#L61-L65)）。

**决定**：页面与 `/api/status` **只列 gRPC 隧道**那一种，SignalR/HTTP 的不上榜。由于三处写入的字段语义不同，`ConnectItem` 增加一个 `Kind` 字段（隧道来源类型）区分写入方，页面端点据 `Kind` 过滤只取 gRPC。gRPC 写入同时带出 `ClientIp` 与 `Target`。

（注：`Kind` 的引入是我的默认判断——三写入方字段不同，不加来源类型就无法筛"只列 gRPC"。属于构建者可自行定夺的范畴。）

因为只列 gRPC，**验收标准里"页面行数 == `SocketCount`"作废**——`GetStatus.SocketCount` 数整个字典（含 SignalR/HTTP），基准改为"gRPC 隧道条数单独计数比对"，见验收标准。

### 数据来源与服务端改动

**现状问题**：`ConnectItem.ClientIpAddress` 实际存的是拼接字符串（见上），属于历史遗留的偷懒写法。拆字段只是**页面内部的便利**，以便前端不用 `Split(' ')`。

**决定**：

- `ConnectItem` 拆分为 `ClientIp`（来自 `context.Peer`，剥掉端口只留 IP，注意 IPv6 方括号）与 `Target`（`host:port`），保留 `DateTime`（建连时间），新增 `Kind`。
- gRPC 侧 `GetStatus` 仍把两项**拼回原样** `"{peer} {host}:{port}"` 填进 `StatusDetail.IpAddress`（[MyGrpcService.cs:261-269](../Services/MyGrpcService.cs#L261-L269)）。**proto、StreamHandlerTests 断言、Exe 的 StatBackgroundService 日志一行都不动**——拆分纯粹是页面内部的事（用户已拍板：保留现字符串）。
- 页面运行时长 = `DateTimeOffset.UtcNow - DateTime`。

**JSON 端点**：新增 `/api/status`，返回活跃连接数组（含 `clientIp`、`target`、`startedAt`，以及总数）。走 source-generated JSON 序列化（AOT 要求）。

**静态文件**：新增 `UseStaticFiles()`，并把 `wwwroot/admin.html` 及配套资源纳入发布产物（确认 csproj 中 wwwroot 的 copy 行为）。

## 留给构建者的决定（我可以自行定夺的部分）

以下是实现细节，一个有经验的构建者可以在不改变"页面长什么样/怎么被保护"的前提下自行定：

- **鉴权的具体承载形式**：HTTP Basic（浏览器原生弹框，实现最省）／Bearer + 前端存 token／表单页面换 cookie。建议按"最省心且不裸奔"选 Basic 或表单换 cookie。
- **中间件是否放行 `/` 与 gRPC/SignalR 路径**：只保护 `/admin*` 与 `/api/status`，其余路径行为不变。
- **前端刷新方式**：手动按钮之外是否顺带轻量自动轮询（如 5s），不改变核心行为即可。
- **运行时长的具体格式化规则**与表格样式细节。
- **JSON 端点是否复用 `GetStatus` 的内部查询逻辑**（建议抽一个共享方法，避免两处维护）。
- **`wwwroot` 尚未存在时的新建与 csproj 配置**。

## 验收标准

1. 用 https 打开 `/admin`，未带凭据时**看不到任何数据**（401 或登录页）。
2. 输入正确密码后，页面显示"活跃连接：N"与清单，每行客户端 IP / 目标 / 时长正确。
3. 用 `tools/bench` 发起一批隧道后，页面行数 = 该批 gRPC 隧道条数（`GetStatus.SocketCount` 数的是整个字典、含 SignalR/HTTP，**不能**直接当基准；以那一批 gRPC 建连数为准）。bench 结束后行数归零。
4. F12 查看，页面侧无任何"真密码/真校验逻辑"可绕过——鉴权完全在服务端。
5. `StreamHandlerTests` 全部通过，且 gRPC `GetStatus` 返回的 `IpAddress` 仍是原拼接串（证明拆字段没碰坏转发/半关闭、也没改 gRPC 协议形态）。
6. 本机（回环）访问 `/admin` 未带凭据时同样返回 401——鉴权不做回环豁免。
7. Release AOT 构建通过。
