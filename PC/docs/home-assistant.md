# Home Assistant 客户端（首版）

清单第十节要求把 Home Assistant 作为**可选客户端**集成，而不是把服务端塞进安装包。本文件记
首版做到哪、怎么拦、验证了什么、以及哪些还没验证。

## 一、代码落点

| 位置 | 作用 |
|---|---|
| `Services/HomeAssistant/HomeAssistantPkce.cs` | PKCE（S256）与 `/auth/token` 的换取/刷新，纯函数 + 两个 HTTP 调用 |
| `Services/HomeAssistant/HomeAssistantClient.cs` | REST：`/api/` 探测、`/api/states`、`/api/services/{domain}/{service}`；401 单独成异常 |
| `Services/HomeAssistant/HomeAssistantTokenStore.cs` | 令牌落盘，复用 `ApiKeyProtector`（AES-256-GCM + 完整性校验） |
| `Services/HomeAssistant/HomeAssistantModels.cs` | 实体/令牌/服务调用模型；`HomeAssistantRisk` 决定风险类别 |
| `Services/HomeAssistantControl.cs` | 编排：地址校验、授权、连接、读缓存、写走审批、登出 |
| `Services/HomeAssistantApprover.cs` | 把审批请求变成待审批队列里的真实问题（`AgentControlCenter`） |
| `Services/HomeAssistantSettingsModels.cs` | 纯值契约，供控制中心与测试引用，不拖 WPF/网络 |
| `Services/Dashboard/DashboardApi.cs` | `/api/homeassistant` 读，`/settings` `/authorize` `/complete` `/token` `/connect` `/signout` `/call` 写 |
| `Services/WebDashboardService.cs` | 控制中心页面的「Home Assistant」区块 |

## 二、两条授权路径

**主路径：OAuth 授权码 + PKCE。** 控制中心点「生成授权链接」→ 浏览器打开 → 在 Home Assistant
里同意 → 浏览器跳到一个打不开的回调地址 → 把地址栏里整条 URL 粘回页面完成授权。

之所以要粘贴，是因为本程序**没有为它再开一个本地监听端口**：回调地址只要能出现在地址栏里就够
了，少一个朝外或朝内的端口就少一类被别的本机进程利用的入口。`client_id` 默认取回调地址的
origin（`http://127.0.0.1:8137`），因为 IndieAuth 要求 client_id 与 redirect_uri 同源。

**备用路径：长期访问令牌。** 用户在 Home Assistant 个人资料里自己创建，粘进来。清单要求「不得
默认索取长期令牌」，所以它是备用项，不是默认项。

## 三、写操作怎么被拦

读（实体状态、场景、自动化列表）不打断你；写（服务调用）必须先回答一个问题。

- 新增 `RiskCategory.ControlExternalDevice`（追加在枚举末尾，历史审计记录的含义不变）。
- `homeassistant.restart` / `homeassistant.stop` / `shell_command` / `hassio` / `python_script`
  / `lock.unlock` / `mqtt.publish` / `recorder.purge` 记为 `PrivilegeEscalation`，在审计里和
  「开客厅灯」区分开。
- 问题走**同一张待审批表**（`AgentControlCenter`），和文件接收、Fairy Code 工具调用排队在一起。
- 没有审批界面、用户拒绝、审批超时：一律不执行，且**不发出任何服务调用请求**——这一条是断言，
  不是约定。
- 附加数据（如 `{"brightness":128}`）在问用户之前先校验形态；不是 JSON 对象就直接拒绝，不进审批。

## 四、验证

`Tests/HomeAssistant` 94 项，`Tests/Dashboard` 216 项（本轮 +18）。覆盖：

- PKCE 用 RFC 7636 附录 B 的公开向量校验 S256；verifier 长度、base64url 无填充、每次新生成。
- 授权链接带 `response_type=code`、`code_challenge_method=S256`、`state`，redirect 被转义。
- 回调解析支持整条 URL / 裸 query / `error` 回复；没有结果时返回 false。
- 地址规范化：裸主机补 `http://`，只接受 http/https，空白拒绝。
- 令牌交换送对 `grant_type` 与 `code_verifier`；刷新送 refresh_token 且**不再送 verifier**；
  `ToString` 不含令牌明文；失败时不把 verifier 回显到异常消息里。
- 令牌文件里**搜得到不到明文**令牌；损坏文件返回空并给出原因，不抛异常。
- `state` 不符时**不调用 token 端点**；同一回调不能重放；换服务器地址会丢弃旧实例的令牌。
- 拒绝 / 无审批时请求数不变；批准后请求路径与 body 正确。
- 视图序列化后不含任何令牌字段。

页面侧：内嵌 JS 抽出来过了 `node --check`（语法通过），13 个 `<section>` 配平，并通过真实 HTTP
取页面断言了区块与加载函数存在。

## 五、没验证的（重要，别当成已验收）

1. **没有对真实 Home Assistant 实例跑过。** OAuth 流程、`/api/states`、服务调用全部是对着进程
   内的假 HA 验的。真实实例的差异（版本字段、`location_name` 缺失、代理前缀）没碰过。
2. **client_id / redirect_uri 这一对能不能被真实 HA 接受，没有实测。** 按 IndieAuth 同源规则应当
   可以；如果被拒，页面上两个输入框都能改。
3. **局域网自动发现（mDNS）没做。** 清单里它是可选项，现在只能手填地址。
4. **撤销只在本机。** 删掉本地令牌文件不会作废服务端令牌；要在 Home Assistant 个人资料里删除这个
   授权。Home Assistant 没有公开的 token 撤销端点，所以「可撤销」目前是「你能撤销」，不是「程序
   替你撤销」。
5. **侧栏入口没做。** 清单要求 Plus/Ultimate 侧栏提供入口，现在入口在控制中心页面里。
6. **Android 经配对 PC 代控没做。**
7. 令牌存储用的是项目现有的加密存储（密钥文件与程序同目录），不是 Windows 凭据管理器；能防配置
   文件/备份/日志泄漏，挡不住已经能读程序目录的人。要换凭据管理器是独立的一步。
