# PC 端文档索引

每篇都是"为什么这么做、验证到哪一步、哪些没做"的记录，不是 API 手册。想改某个子系统之前先读对应的那篇，能省掉重新踩坑的时间。

| 文档 | 什么时候读 |
| --- | --- |
| [webchat-protocol.md](webchat-protocol.md) | 要写 WebChat 客户端（安卓端、脚本、第三方 UI），或改 WebSocket 服务的行为 |
| [system-notifications.md](system-notifications.md) | 动系统通知、免打扰、任务摘要同步、配对令牌 |
| [android-bridge.md](android-bridge.md) | 动手机端任务与待审批、PC 配对调用 |
| [screen-control.md](screen-control.md) | 动屏幕操作的暂停/继续/停止与平台门控 |
| [home-assistant.md](home-assistant.md) | 动 Home Assistant 客户端、授权与令牌存储 |
| [mcp-backend.md](mcp-backend.md) | 动 MCP 后端的启动、工具发布与审批 |
| [rendering-surface.md](rendering-surface.md) | 动 Vulkan/OpenGL 渲染面、DirectComposition 与透明合成 |
| [fairy-code-max.md](fairy-code-max.md) | 动 Fairy Code 的界面与交互约定；附图见 [fairy-code-ui-preview.png](fairy-code-ui-preview.png) |

约定：

- 每篇记录的是**实测结论**，与实测不符的地方以实测为准，并把文档改掉。
- "没做/没验证"要写在文档里，不要只留在提交信息里——提交信息会被埋掉。
- 新增文档后要加进本表：`PC/Tests/Docs` 会检查孤儿文档和失效链接，漏加会红。
