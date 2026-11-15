# 普通系统通知与任务摘要同步

清单第九节剩下的两条：普通系统通知（Windows/Linux/Android），以及 Android 与已配对 PC 的
任务摘要同步。本文件记做到哪、怎么拦、验证了什么、以及两处必须说明的缺口。

## 一、UniDesktop SDK 已接入

清单要求「Windows/Linux 集成 UniDesktop SDK」。用户给出了来源后，这部分是真的接上了：

- 仓库 `https://github.com/UniDesktop/SDK`（UniDesktop API / UDA，**MIT OR Apache-2.0**，Rust 实现，提供稳定 C ABI `include/uda.h` 与 Python/Node 绑定）。
- 用的是官方 release 里的预编译库 `uda-ffi-v0.2.1-x86_64-pc-windows-gnu.zip`，下载后先核对发布方 `SHA256SUMS.txt`（`3e1655e7…`，一致），再解出 `uda_ffi.dll` 放到 `Dependencies/uda/`，未做任何改动。许可已登记进 `THIRD-PARTY-NOTICES.md`。
- 接口签名按 `uda.h` 原文实现，不凭印象：
  `int32_t uda_notify(const char *app_name, const char *title, const char *body, const char *icon, const char *actions, uint32_t *out_id)`

`UdaSystemNotificationChannel` 用 `NativeLibrary` + 导出函数指针调用，字符串一律按 **UTF-8** 传（Rust 侧是 `char*`，走 ANSI 代码页会把中文变成乱码）。查找顺序：显式路径 → `UDA_LIBRARY` 环境变量 → 程序目录 → `Dependencies\uda\`，找不到就把「查过哪里」写进错误里。

失败原因不是我们自己编的，而是取库的 `uda_last_error_message()`（按线程记录）。第一次跑就靠它拿到真相：

```
UDA 内部错误（Internal error: CreateToastNotifierWithId failed: 该进程没有程序包标识符。(0x80073D54)）
```

也就是说：**UDA 调用链是通的，Windows 侧被系统拒绝了**。Windows 11 的 `CreateToastNotifierWithId` 要求进程有程序包标识（MSIX），或者该 AUMID 已通过「带 `System.AppUserModel.ID` 属性的开始菜单快捷方式」注册。我们是个未打包的 WPF 进程，两者都没有。

处理方式：通道保留、按实情报告，**运行时回退到托盘气泡**（`App.CreateSystemNotificationChannel` 启动时打一行日志说明选了哪条、为什么）。

「加个 AUMID 快捷方式就能好」这条路我试过了，**不成立**，两次都拿到同一个错误：

1. 用 `IShellLink` + `IPropertyStore` 写了一个带 `System.AppUserModel.ID` 的快捷方式（先写到别处再拷进开始菜单，因为这台机器上直接在该目录创建 `.lnk` 会 E_ACCESSDENIED——同目录写入普通文件却正常，所以拦的是"链接创建"这个动作）。快捷方式就位后再调 `uda_notify`：**仍是 `0x80073D54`**。
2. 再让进程自己声明 `SetCurrentProcessExplicitAppUserModelID` 后重试：**仍是 `0x80073D54`**。

结论：这版 Windows 11 只认包标识（MSIX / 稀疏包），未打包进程无论如何都发不出 WinRT toast。所以：

- 那个临时快捷方式与写快捷方式的工具**都已删除**（工具达不成目的，留着就是误导）。
- 要真正上 toast，只有 **MSIX（或稀疏包）打包**一条路，这是发行环节的活。在那之前系统通知走托盘气泡，`uda_notify` 的失败原因会被如实记录。

## 二、通道与回退

| 平台 | 实现 | 说明 |
|---|---|---|
| Windows / Linux（首选） | `UdaSystemNotificationChannel` → `uda_notify` | Windows 走 WinRT Toast，Linux 走 `org.freedesktop.Notifications` |
| Windows（回退） | `DelegateSystemNotificationChannel` → 托盘 `ShowBalloonTip` | UDA 库缺失或被系统拒绝时使用 |
| Linux（回退） | `LibnotifySystemNotificationChannel` → `notify-send` | 参数用列表传、加 `--` 分隔，标题以 `-` 开头也不会变成选项 |
| 其他 | `UnavailableSystemNotificationChannel` | 明确报告「没有通道」，不假装成功 |

## 三、投递策略（谁可以离开本机）

`SystemNotificationPolicy` 决定哪类通知可以交给操作系统：

| 通知类型 | 默认 | 说明 |
|---|---|---|
| `System`（更新可用等） | 投递 | 文案由我们自己写，不含任务内容 |
| `TaskCompleted` / `TaskFailed` / `TransferIncoming` | 不投递 | 勾「关键任务也推系统」才投递 |
| `ApprovalPending` / `QuestionPending` | **永不投递** | 气泡装不下回答；而且答完之后 OS 历史里留一条「需要你确认」比不提醒更糟 |

另外三条：

- **免打扰同样管系统通知**。只在 HUD 里静音、却让系统通知照响，等于没静音。
- **同一事项 10 分钟内只提醒一次**（按 `DedupeKey`），和历史合并规则一致。
- **系统通知默认不带具体内容**：正文换成固定短句；打开「带具体内容」后仍会抹掉路径和长令牌
  （`SystemNotificationPolicy.Scrub`：Windows 盘符路径、UNC、POSIX 路径、32 字符以上的 token 串）。

每一次拦下都会记原因（`Delivered` / `Suppressed` / `LastSkipReason`），控制中心的通知区块会显示
「已投递 N，已拦下 M，最近一次：免打扰时段」。这样「我怎么没看到 Windows 通知」是可查的，不是靠猜。

## 四、给系统通知一个真实来源

原来只有 6 类事件，普通系统通知没有任何生产者，通道就成了死代码。这轮把「发现新版本」接了过来：
`SignedUpdateService.UpdateAvailable` 不再直接弹托盘，而是写入通知契约（`NotificationKind.System`），
于是它同时进入仙灵自己的历史、灵动岛和系统通知通道。签名验证仍在前一步，不会自动下载或安装。

## 五、Android 侧

### 4.1 原生通知

`Platforms/Android/AndroidNotificationSink`：`NotificationManagerCompat` + 通知渠道 `xianling_summary`。
**权限不装作有**：Android 13 起没授权时 `Notify` 会被静默丢弃，所以先查
`AreNotificationsEnabled()`，没开就把 `LastError` 设成「系统里的通知权限没有开启」并返回失败。

### 4.2 任务摘要同步

两端都要显式打开：手机在设置页打开「电脑任务摘要」，电脑在 `SyncConfig.SummarySyncEnabled` 打开。
任何一端没开都不通。

- **PC**：`GET /summary`，要求 `X-From-Device` + `X-Device-Token`，返回
  `{generatedAt, tasks:[{taskId, title, state, updatedAt}]}`，最多 20 条，标题在 PC 侧就抹掉路径并截到
  40 字。`SummarySyncAccess` 把「开了吗 / 令牌对吗」做成纯函数，先判开关再判令牌，所以返回码不会
  泄露令牌是否有效。
- **手机**：`SummarySyncService` 每 60 秒拉一次，只读四个字段，其它字段**一律忽略**——将来 PC 多送
  字段也不会自动漏到手机通知里。首次看到某个任务只在它已经结束时才提醒（否则一开同步就把电脑上
  所有正在跑的任务重播一遍），每次最多推 5 条。

## 六、顺手修掉的认证弱点，并补上电脑侧开关

原来 `/send` 和 `/command` 的判断是「请求头 `X-From-Device` 的值在已配对字典里」——设备名不是秘密，猜到型号名就能传文件、下发指令。

现在的规则（两端一起改，否则手机会失效）：

- **PC**：`IsDevicePaired` 要求 `X-From-Device` **和** `X-Device-Token` 同时有效，令牌由 `/pair` 成功时下发，PC 只存 SHA256，可按设备撤销。
- **手机**：`AddPairedHeaders` 在传文件、发文本时带上令牌；令牌在配对成功时从响应里取出并用 `ProtectKey` 加密存进配置。
- 影响：**已经配对过的手机需要重新配对一次**（旧 PC 构建从没下发过令牌，手上也没有可用的）。

电脑侧现在也有界面开关，不用再改配置文件：

- 控制中心新增「手机任务摘要」区块：允许/关闭、列出持有令牌的设备、逐台「撤销令牌」。
- `ISummarySyncControl` 与 LAN 服务**共用同一个 `PairedDeviceTokens.Shared` 实例**，所以撤销立刻生效（下一个请求就 403），不需要重启。
- 撤销是删掉那一行哈希，不是打标记；该设备下次必须重新配对。

## 七、验证

- `Tests/SystemNotifications` 45 项、`Tests/SummarySync` 58 项、`Tests/UdaNotification` 12 项，PC 端 26 套测试合计 1547 项断言全过。
- `Tests/UdaNotification` 是真的把 `uda_ffi.dll` 加载起来并调用 `uda_notify`：库能加载、导出能找到、中文/空正文/超长文本都不会被本地判成「参数不合法」，Windows 拒绝时带的是系统原文而不是一句 `false`。
- Android `dotnet build -f net10.0-android` 0 错误，新增文件 0 警告。

覆盖的关键行为：批准类通知永不投递；免打扰拦住系统通知；同一事项不重复投递；关掉的类型根本进不了历史；路径/令牌被抹掉、长文本截断；通道不可用或抛异常时只记账不崩；`notify-send` 的参数顺序与 `--` 分隔；令牌只存哈希、重新配对让旧令牌失效、撤销后失效、重启后仍有效；**没有令牌只有设备名时被拒**；摘要载荷只有四个字段、超量截断、同 id 取最新、空列表合法。

页面侧：控制中心通知区块新增三个开关与通道状态行，内嵌 JS 过 `node --check`，13 个 `<section>` 配平。

## 八、没验证的

- **真正的系统通知没有肉眼看到过。** UDA 的调用链通了，但 Windows 拒绝未打包进程发 toast（上文的 `0x80073D54`，AUMID 快捷方式与进程自声明都试过，均无效），当前跑起来看到的是托盘气泡回退。要看到真 toast，只能把应用打成 MSIX / 稀疏包。
- **没有在真手机上跑过**。安卓端只做到编译通过；通知是否真的弹出、令牌是否真的存进加密配置、60 秒轮询在息屏后是否被系统限流，都需要真机。
- **`/send`、`/command` 的新认证没有做端到端联调**：单元测试覆盖了「只有设备名被拒」，但「手机带令牌能传文件」这条要真手机 + 真 PC 才验得出来。
- **Linux 只验了参数构造**，没有在 Linux 上真的发一条通知。
