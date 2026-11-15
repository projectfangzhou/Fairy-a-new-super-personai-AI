# MCP 后端（受控接入）

清单第五条第三项：把 Windows-MCP 作为 Windows 可选的感知/操作后端，经受控 MCP 适配接入，
统一接受审批、暂停/停止和审计。

## 一、先修掉的一个假协议

仓库里原来的 `Services/MCPClient.cs` **不是 MCP**：它自己发明了 `/tools`、`/call`、`/connect`
三个 HTTP 端点，而 MCP 是 JSON-RPC 2.0，走 stdio 或 SSE 传输。它没有任何调用方，只有一行 DI 注册，
所以这轮直接删了——留着它等于给后来人一个"我们已经支持 MCP"的错觉。

## 二、真的客户端

`Services/Mcp/`：

| 文件 | 作用 |
|---|---|
| `McpModels.cs` | 工具/服务器/结果模型、`IMcpStatus` 契约、两种异常、`McpSettings` |
| `McpTransport.cs` | `IMcpTransport` 与 `StdioMcpTransport`（起子进程、按行收发、stderr 环形缓冲） |
| `McpClient.cs` | 握手 `initialize` → `notifications/initialized`，`tools/list`（含分页游标），`tools/call` |
| `McpToolBackend.cs` | 受控适配层：会话状态 → 审批 → 审计 → 调用；`McpToolAdapter` 把工具交给 agent |
| `McpControl.cs` | 起停服务器、把工具发布进 `ToolRegistry`、停止时撤下 |

几个刻意的实现细节：

- **不走 shell**：命令用 `ProcessStartInfo.ArgumentList` 直接启动，路径里有空格或引号都不会变成
  额外命令；也不从被改过的 PATH 里去找解释器，命令就是用户填的那个绝对路径。
- **按 id 配平响应**：stdio 是单流，服务器可能在响应之间插日志通知；客户端逐行读、跳过通知和非 JSON
  噪音、只认 id 相等的响应。服务器反过来请求客户端时回 `-32601`，免得它一直等。
- **超时是超时**：服务器不吭声会报"等待响应超时"，不会挂住。
- **stderr 不丢**：留最近 40 行，进程异常退出时把它的最后几句话带进错误消息——这是排查唯一线索。

## 三、闸门和内置屏幕工具是同一道

MCP 工具被包装成普通的 `ITool`，但它的每次调用都走 `McpToolBackend`：**会话状态（暂停/停止）→
`ApprovalGate` 审批 → 审计 → 才发出去**。

- **没有白名单**。第三方 MCP 工具能做什么，只有它的作者知道；这个程序无从判断，所以每一次调用都是
  一个问题，不能默认放行。
- 风险类别按工具名归类：名字里带 click/type/key/scroll/drag/move/launch/start/open/close/app 的记为
  `OperateScreenAndInput`（操作桌面），其余记为 `ControlExternalDevice`（调用外部服务）。两者都要审批，
  归类只影响审计读起来清不清楚。
- 停止 MCP 服务器会把它的工具从工具表里**撤下**（`ToolRegistry.Unregister`），否则 agent 会继续看到
  一堆调用必然失败的功能。

## 四、配置

`config.json` 的 `Mcp`：

```json
"Mcp": {
  "Enabled": false,
  "ServerCommand": "D:\\tools\\python314\\python.exe",
  "ServerArguments": "[\"-m\",\"windows_mcp\"]",
  "StartupSeconds": 30
}
```

`ServerArguments` 写 JSON 数组就按数组传；写成一行用空格分隔也可以。两种情况都不经过 shell 展开。

默认关闭，且**没有配置命令时不会启动任何进程**。控制中心新增「MCP 后端」区块：状态、启动、停止并
撤下工具、工具清单；接口 `GET /api/mcp`、`POST /api/mcp/start`、`POST /api/mcp/stop`。

## 五、验证

`Tests/Mcp` 42 项，PC 端 28 套测试合计 1676 项断言全过；控制中心页面 16 个 `<section>` 配平、内嵌
JS 过 `node --check`。

覆盖的关键行为（用一个会分页、会插日志噪音、会反问客户端、会报错、会装死的假服务器）：

- 握手：`initialize` 带 clientInfo 与协议版本，随后是无 id 的 `notifications/initialized`。
- 分页：`nextCursor` 被回传，三页工具都读全；中间夹的非 JSON 日志行与通知被跳过。
- `tools/call`：参数对象原样透传，`isError` 会浮出来，文本内容会被拼接；非法 JSON 参数在发送前就被拒。
- JSON-RPC 错误 → 带 code 的协议异常；服务器装死 → 超时而不是挂起；服务器反问 → 回 `-32601`。
- 闸门：**未批准时一个 `tools/call` 都没发出**（断言服务器的收发记录）；暂停/停止同样拦下；没有审批器
  时按失败关闭；批准后恰好一次调用，会话在飞计数归零。
- 审计：名字像 UI 动作的记为 `OperateScreenAndInput`，名字像查询的记为 `ControlExternalDevice`。
- 适配器：工具名加 `mcp_` 前缀并清洗非法字符；JSON Schema 的 type/required/description 映射到本项目的
  参数模型；读不懂的 schema 退化成空参数而不是抛异常。

## 六、没做的 / 没验证的

1. **没有对真实的 Windows-MCP 服务器跑过**。测试用的是进程内的假服务器，所以 `StdioMcpTransport`
   起进程这一段没有端到端验证（命令不存在、进程崩溃、stderr 输出这些分支只做了代码级保证）。
   要真验，需要你装好固定版本的隔离 Python 与 Windows-MCP，然后在配置里填上绝对路径。
2. **没有设置"启动时自动启用"的界面开关**：现在靠 `config.json` 的 `Enabled`，控制中心只有启动/停止。
3. **没有做工具级白名单**，这是刻意的：不能判断的第三方工具一律逐个批准。
4. **嵌套 JSON Schema 会被压平**：本项目工具模型只有 name/type/description/required，深层结构丢给
   服务器自己的 description 去解释。
