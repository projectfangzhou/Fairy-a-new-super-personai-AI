# 仙灵（FairyAI）

一个 AI 语音助手，两端在同一个仓库里：

| 目录 | 是什么 | 技术栈 |
|---|---|---|
| `PC/` | Windows 桌面端（悬浮球 + 灵动岛） | .NET 8 WPF，Inno Setup 打包 |
| `Android/` | 手机端 | .NET MAUI（net10.0-android） |

> 鸿蒙端不在这个仓库里。

两端通过**配对 + 局域网**协作：电脑下发一次性邀请码与每设备令牌，之后手机可以读任务摘要、回答待审批、与电脑互相同步笔记；手机还能用 Shizuku 做本机屏幕操作（点击/滑动/输入/读屏/截屏），每一步都要逐项授权并逐次审批。所有涉及屏幕、外部设备、文件传输的动作都过同一个审批闸门，拒绝时不发送任何请求。

## 构建

**桌面端**

```
cd PC
dotnet build MyAiAssistant.csproj -c Release
```

安装包用 `PC/Installer/` 下的 Inno Setup 脚本打包。

**手机端**

```
cd Android
$env:JAVA_HOME='<JDK 21>'
dotnet build -f net10.0-android -p:AndroidSdkDirectory=<Android SDK>
```

Release 签名需要环境变量 `ANDROID_KEYSTORE_PATH` / `ANDROID_KEYSTORE_PASSWORD` / `ANDROID_KEY_ALIAS` / `ANDROID_KEY_PASSWORD`，或直接跑 `Android/build-release.ps1`（它从仓库外的 `~/.fairyai-keys` 读取）。**签名材料不进仓库。**

## 测试

```
# 手机端（控制台断言程序）
dotnet run --project Android/Tests/Tools
dotnet run --project Android/Tests/Notes
dotnet run --project Android/Tests/Vision
dotnet run --project Android/Tests/Screen
dotnet run --project Android/Tests/SignedUpdates

# 桌面端
dotnet run --project PC/Tests/NoteSync
dotnet run --project PC/Tests/PairedDesk
dotnet run --project PC/Tests/SummarySync
dotnet run --project PC/Tests/Dashboard
```

手机端另有 `Android/Tools/check_xaml.py`（XAML 结构与代码后置一致性）和 `Tools/find_mojibake.py`（双重编码中文扫描）。

> 跑桌面端测试前把 `TEMP`/`TMP` 指向一个可写目录（本机默认为 `D:\cache\temp`，沙箱里不可写）。

## 许可

MIT，见 `LICENSE`。第三方组件清单在各端的 `THIRD-PARTY-NOTICES.md`。
