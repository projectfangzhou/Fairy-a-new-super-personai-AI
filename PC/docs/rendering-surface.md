# 渲染面：Vulkan / OpenGL 实测与结论

清单第十五节要求「Vulkan 优先用于 3D/Live2D/MMD/HUD 视觉表面，OpenGL 作为回退」，并规定
「Windows/Linux 先实测 Vulkan 与 OpenGL」。本文件记录 2026-10 在这台机器上的实测数据与由此
得出的结论。**只做实测，不引入任何渲染库**：所有调用都是手写 P/Invoke，没有新增第三方依赖，
第三方许可清单不受影响。

## 一、结论（先看这段）

1. **Vulkan 在这台机器上是可用的**：loader 1.4.309，两块 GPU 都有图形队列、窗口表面、
   Win32 外部内存/信号量、时间线信号量。
2. **但 Vulkan 窗口表面拿不到逐像素透明**：两块设备的 `supportedCompositeAlpha`
   都只有 `OPAQUE`（Intel 另有 `INHERIT`），没有 `PRE_MULTIPLIED`。
3. **OpenGL 更不行**：WGL 没有「合成 alpha」这个概念，实测帧缓冲 alpha 被直接忽略。
4. **透明表面（浮球、灵动岛、透明 Live2D 窗口）不能是 Vk/GL 交换链**。Windows 上逐像素透明
   只有两条路：分层窗口 `UpdateLayeredWindow`（像素由 CPU 提供）或 DirectComposition +
   D3D11 预乘 alpha 交换链（像素由 GPU 提供，Vulkan 需经外部内存互操作参与）。
5. 因此**现在不应该开始写 Vulkan 渲染器**。理由不是「做不到」，而是：现有 Live2D 走 WebView2，
   换原生 Vulkan 要先有模型加载/绘制/动画/物理，而 Cubism Core 的随包分发许可（清单第 603 条）
   还没定；在许可未定时投入一个渲染器，做完也可能不能发。

## 二、环境

| 项 | 值 |
|---|---|
| GPU 0 | Intel(R) UHD Graphics 620，集成，Vulkan 1.2.154，驱动 100.8984，显存 4146 MB |
| GPU 1 | NVIDIA GeForce MX250，独显，Vulkan 1.4.312，驱动 582.53.0.0，显存 2182 MB |
| Vulkan loader | 1.4.309 |
| OpenGL | 4.6.0（Intel 驱动 27.20.100.8984，WGL 默认落在集显） |
| 队列族 | 两块都有 GRAPHICS\|COMPUTE\|TRANSFER |
| 交换链图像数 | Intel 2..64，NVIDIA 2..8 |
| 呈现模式 | Intel: IMMEDIATE、FIFO；NVIDIA: 另有 MAILBOX、FIFO_RELAXED |
| 互操作 | 两块都支持 `VK_KHR_external_memory_win32`、`VK_KHR_external_semaphore_win32` |

## 三、透明合成实测（决定性数据）

做法：先铺一块纯绿背景窗，再用不同的合成路线把它盖住，最后从桌面 DC 直接读回那一个像素。
每段都带对照，避免把「窗口没画出来」误判成「alpha 不生效」。

| 路线 | 读回的像素 | 含义 |
|---|---|---|
| 背景（纯绿） | R=0 G=255 B=0 | 桌面可读，采样点确实在测试区域 |
| 普通窗口 + OpenGL 不透明红（对照） | R=255 G=0 B=0 | GPU 内容确实上屏 |
| 普通窗口 + OpenGL 50% alpha | R=128 G=0 B=0 | **alpha 被忽略**：0.5 红按不透明显示，没有和绿混合 |
| 分层窗口 + 整窗 alpha=128 + OpenGL 不透明红 | R=128 G=127 B=0 | 整窗**统一** alpha 有效，但不是逐像素 |
| 分层窗口 + `UpdateLayeredWindow`（CPU 预乘 50% 红） | R=128 G=127 B=0 | 逐像素 alpha 可行，代价是像素来自 CPU 位图 |

清屏 + 交换的开销（512×512，含 `glFinish`）：20 帧 0.625 ms/帧，120 帧 1.219 ms/帧。
也就是说呈现本身不贵，贵的是「谁来提供像素」。

## 四、对清单第 599 条的修正建议

原文「Vulkan 优先用于 3D/Live2D/MMD/HUD 视觉表面」需要拆开写，否则实现时一定会撞上第 2 条结论：

- **应用内不透明画布**（MMD 预览、3D 模型查看、游戏式渲染区）：可以用 Vulkan，拿不到透明也不影响。
- **HUD / 灵动岛 / 浮球 / 透明 Live2D 覆盖层**：不能是 Vulkan 或 OpenGL 交换链，只能分层窗口
  （CPU 像素）或 DComp + D3D11。这一条是平台约束，不是选型偏好。
- **文本与表单**：继续用 WPF/MAUI/Web 各自的技术，不做自绘。

## 五、代码落点

| 位置 | 作用 |
|---|---|
| `Services/Rendering/RenderSurfacePolicy.cs` | 纯策略：由探测出的事实决定后端，每次降级都写进 `Limitations`，不静默吸收 |
| `Tools/GpuProbe/` | 实测工具，输出人读报告与 `gpuprobe-report.json` |
| `Tests/Rendering/` | 29 项断言：设备筛选、优先独显、为透明改用次优设备要说明、失败不外抛、空输入不崩 |

复现：

```
cd Tools/GpuProbe
dotnet run -c Release -- --frames 120
```

`--no-alpha` 跳过会短暂弹窗的透明实测；`--frames 0` 跳过帧耗时测量。

## 六、这次没做的

- **没测 Vulkan 的呈现/渲染吞吐**。要跑起来需要渲染通道或 SPIR-V 着色器，机器上没有
  glslangValidator/dxc；而且「清屏吞吐」也不决定选型，决定选型的是上面那份合成 alpha 数据。
- **没实测 DirectComposition + D3D11 的透明路径**。那是 COM 互操作，是真正要启用 GPU 透明时
  的下一步；本次只证明了它存在（`dcomp.dll` 在），并用 `UpdateLayeredWindow` 对照证明了
  「逐像素 alpha 在 Windows 上确实需要 CPU 像素」。
- **没在 Linux / Android 上跑**。清单第 600 条要求平台分别实测，目前只有 Windows 有数据。
- **探测工具还没接进应用界面**，也没进能力表；现在它是个独立工具。

## 七、一个踩过的坑（留给下次写原生窗口的人）

消息泵里的 `PeekMessage` 必须配合 `TranslateMessage` + `DispatchMessage`。只取消息不派发时，
`WM_PAINT` 被丢弃、窗口永远处于无效状态，Windows 会不停重投，队列再也排不空——本机表现是
探测工具直接卡死（第一次跑挂了整整 2 分钟超时），加消息计数上限后才暴露出「队列持续非空 20000 条」。
