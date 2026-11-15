# Pi Agent（内置副本）

仙灵 3.0 的 Fairy Code 普通版以 [@earendil-works/pi-coding-agent](https://www.npmjs.com/package/@earendil-works/pi-coding-agent)
作为主代理循环。这个目录是该包的**运行时副本**，随应用一起分发，不再依赖开发机上的全局安装。

| 项 | 值 |
|---|---|
| 包名 | `@earendil-works/pi-coding-agent` |
| 版本 | **0.84.3**（锁定，不自动升级） |
| 许可证 | MIT（见 `THIRD-PARTY-NOTICES.md`） |
| 入口 | `dist/bundle/cli.js` |
| 复制来源 | 上游 npm 包的运行时文件，逐字节复制 |
| 额外复制 | `package.json`（pi 靠它定位包根）、`node_modules/jiti`（pi 用它在运行时加载扩展） |
| 排除项 | `*.map`、`*.d.ts`（运行不需要） |
| 完整性 | `manifest.json` 记录每个文件的 SHA-256 与字节数 |

目录结构保持上游 `dist/` 的样子（`dist/bundle/`、`dist/modes/interactive/theme/`、
`dist/modes/interactive/assets/`、`dist/core/export-html/`），因为 pi 会按相对路径读取主题和
导出模板——把 bundle 平铺到根目录会让它启动即报 `ENOENT`。

## 为什么放在仓库里

清单要求「统一安装包为所选代理引擎携带固定版本、校验过的运行时，不修改系统 Node/PATH」。
依赖开发机上的 `pi` 全局安装会让「能不能用」取决于这台机器装过什么，也无法固定版本。

## 怎么找到它

`Services/FairyCode/PiAgentLocator` 的查找顺序：

1. 环境变量 `FAIRY_PI_CLI`（显式指定，用于调试或替换）
2. 应用输出目录下的 `ThirdParty/PiAgent/dist/bundle/cli.js`（随包分发的那一份）
3. 从输出目录向上找到仓库里的同一路径（开发期 `dotnet run` 场景）
4. 本机全局安装（`%APPDATA%\npm`、`D:\npm-global`、PATH 上的 `pi.cmd` 旁边）

找到哪一份会在界面上写明（内置副本 / 环境变量指定 / 本机全局安装），不会混为一谈。
内置副本启动前会用 `manifest.json` 校验每个文件的 SHA-256。

## 怎么更新

```powershell
pwsh -File Tools/fetch-pi-agent.ps1 -PackageRoot 'D:\npm-global\node_modules\@earendil-works\pi-coding-agent'
```

脚本会按上游布局重新复制运行时文件、重写 `manifest.json`，并提示你同步本文件的版本号。
升级前必须重跑 `Tests/FairyCode`，因为 RPC 协议与 `tool_call` 事件是外部契约。

## 不要做的事

- 不要手工修改这里的文件：副本一旦与 `manifest.json` 不一致，完整性校验会失败。
- 不要把它当成可以随意升级的普通依赖：RPC 事件格式变化会直接影响审批链路是否还拦得住工具调用。
- 不要在里面放模型密钥或任何用户数据。
