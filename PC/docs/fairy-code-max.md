# Fairy Code Max · 参考与待办

记录 Fairy Code Max 阶段要引入的两个外部参考，以及界面后续还要改的地方。
本文件只记决定和位置，不记实现细节。

## 一、要引入的参考仓库

两个仓库已克隆到本机 `D:\Dev\refs\`（浅克隆，只作阅读参考，不复制代码进仓库）。

| 仓库 | 本地位置 | 用途 | 何时做 |
|---|---|---|---|
| [VoltAgent/awesome-design-md](https://github.com/VoltAgent/awesome-design-md) | `D:\Dev\refs\awesome-design-md` | 设计文档写法与结构参考：把「一套设计系统怎么写成可执行文档」拆成可复用的片段，供 Max 的多引擎编排与技能说明借鉴 | Max 阶段 |
| [deepseek-ai/dsh-superagent-prototype](https://github.com/deepseek-ai/dsh-superagent-prototype) | `D:\Dev\refs\dsh-superagent-prototype` | Superagent 原型：多代理编排与长任务的参考结构 | 与界面改造一起做（用户明确：以后和 UI 一起做） |

引入原则不变：**只参考思路，不复制代码**；若要复用任何片段，先核对该仓库许可，并同步登记到第三方许可清单。

## 二、界面后续要改的

1. **侧边栏更窄**：现在是 248px，后续按档位收窄（目标 200px 以内），并同步调整会话条目的字号与内边距。
2. **换图标**：侧边栏的「新建任务 / 待审批 / 停止 / 清屏」目前用内联矢量路径，后续统一换成正式图标集；与桌面坞 `Assets/Dock/` 的风格保持一致。
3. 排版尚未经人眼验收：三栏布局在最小宽度（1000px）与高 DPI 下的实际观感需要真机确认。

## 三、这次没做的

- 传输中断恢复（`TransferReceiver` 已能报告缺哪几块，发送端还没有被要求重发）。
- 传输链路未在两台真机上跑过端到端。
