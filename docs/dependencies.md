# 依赖与许可证清单（2026-11-21）

目标：能自己实现的就不引入第三方，减少需要登记与审计的许可证数量。下面是清点结果与处置。

## 本轮移除的

| 包 | 原用途 | 处置 |
|---|---|---|
| **QRCoder** | 生成配对二维码的 PNG | 唯一的消费方 `Services/QRCodeGenerator.cs` **没有任何调用方**，两者一起删除 |
| **BouncyCastle.Cryptography**（安卓） | 更新清单的 Ed25519 验签 | 换成 .NET 自带的 **ECDSA P-256**（`System.Security.Cryptography`） |
| **NSec.Cryptography**（PC） | 更新清单的 Ed25519 签名/验签 | 同上；签名工具改为读 PKCS#8 PEM |

更新签名协议随之变化：签名从 Ed25519（64 字节）改为 ECDSA P-256 + SHA-256、IEEE P1363（r‖s，64 字节），
公钥从 32 字节裸密钥改为 `0x04‖X‖Y`（65 字节）。**旧 Ed25519 密钥作废**，新密钥对：

- 私钥：由发布者离线保存，**不在仓库里**
- 公钥（已引脚到两端）：`BOC16NmPzF7hwqzYd/N3zuLxxZ73WgnXg9jRhXfNJaKJRgtoqbXnDn2Qsfd3UXB43UAXjrdra8fviS8VAV8LMG4=`

重新生成用 `PC/Tools/UpdateSigner genkey <目录>`；引脚位置是 `Android/Services/PinnedUpdateKey.cs` 与 PC 的同名常量，
换密钥必须同时改两处并重签所有清单。

## 保留的第三方（及原因）

| 包 | 许可 | 为什么留着 |
|---|---|---|
| Microsoft.Maui.* / Microsoft.Extensions.* | MIT | 应用框架本身 |
| Microsoft.ML.OnnxRuntime | MIT | 手机上/桌面上跑本地向量模型的推理引擎，无法自己实现 |
| CommunityToolkit.Mvvm | MIT | 源生成器（`[ObservableProperty]`/`[RelayCommand]`），34 处使用；换成手写 INPC 纯属体力活，许可负担已很小 |
| Microsoft.Data.Sqlite / SQLitePCLRaw | MIT / Apache-2.0 | 对话历史与已安装应用映射的存储 |
| Markdig / Markdig.Wpf | BSD-2-Clause | Markdown → WPF 渲染 |
| Microsoft.Web.WebView2 | 微软专有（允许再分发） | WebRTC 宿主与 Live2D |
| System.Speech | MIT | Windows SAPI 封装 |
| Microsoft.AspNetCore.SignalR.Client | MIT | 远程中转同步（要减掉它等于删功能，需要单独决定） |
| dev.rikka.shizuku:api/provider/aidl | Apache-2.0 | 手机端屏幕操作的前提 |
| bge-small-zh-v1.5（随包模型） | MIT / BAAI | 安卓端本地向量模型 |
| ONNX Qwen3-Embedding-0.6B（下载，不入库） | Apache-2.0 / Qwen | PC 端本地向量模型 |

## 还可以继续砍的（需要产品决定）

- **SignalR**：`SignalRSyncService` 只在 `ConnectivityManager` 里启动，删掉它等于放弃远程中转同步，减 5 个包。
- **CommunityToolkit.Mvvm**：可以改成自己写的 `ObservableObject` 基类 + `ICommand`，减 1 个包，代价是三个 ViewModel 的机械改写。
- **Markdig**：自己写 Markdown 渲染器工作量很大，不建议为许可证做。
- **SQLite**：换成 JSON 会动到对话历史，风险大于收益。
