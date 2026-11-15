# 第三方组件声明：Pi Agent 内置副本

## 组件

- 名称：`@earendil-works/pi-coding-agent`
- 版本：**0.84.3**
- 上游：<https://www.npmjs.com/package/@earendil-works/pi-coding-agent>
- 许可证：**MIT**（以该包 `package.json` 的 `license` 字段为准；该包未在发布物内附带 LICENSE 文件原文）
- 上游项目：<https://github.com/earendil-works/pi-mono>

## 分发方式

仙灵原样复制该包的运行时文件（`dist/` 的 bundle、主题与导出模板，以及 `package.json`
和它用于运行时加载扩展的 `node_modules/jiti`）到 `ThirdParty/PiAgent/`，随安装包分发，
不修改其内容。每个文件的 SHA-256 与字节数记录在 `manifest.json`；`Tests/FairyCode`
会校验副本与清单一致。

MIT 许可证允许再分发，前提是保留版权声明与许可声明。上游发布物内未包含 LICENSE
原文，因此本文件保留上述出处与许可标识，作为该项目条款的还原说明。

## 单独复制的依赖

- `jiti` 2.7.0（MIT）：pi 在运行时用 `require("jiti")` 加载 TypeScript 扩展，仙灵的
  审批扩展也走这条路径。这份副本取自 pi 自己的 `node_modules/jiti`，同样受上述清单校验。

## 内嵌的第三方模块（尚未逐项归档）

上游用 bun 把依赖打进了 `dist/bundle/chunks/*.js`，因此这份副本里**包含**它的传递依赖
（例如 `@anthropic-ai/sdk`、`openai`、`@google/genai`、`@aws-sdk/*`、`undici`、`ws`、
`marked`、`chalk` 等）的代码。这些模块各自的许可证见其上游包内的 `LICENSE` 文件，
本项目还有：

- 需要按包名/版本逐项导出为独立的 NOTICES 条目；
- 需要核对其中是否存在 MIT/Apache-2.0 之外的条款（例如依赖树中的可选/间接依赖）。

在上述清单补齐之前，**该副本只能用于内部 alpha 预览，不得进入面向公众的稳定版安装包**。
这是发布阻断项，不是"以后再说"的待办。

## 未被本目录包含的东西

- Node.js 运行时：不在此目录，也不随应用分发；Fairy Code 需要调用本机 Node（≥ 22.19）。
  随包携带固定版本的隔离运行时是后续里程碑，届时要单独记录 Node 的许可证。
- 模型密钥、用户配置、会话记录：不在此目录。
