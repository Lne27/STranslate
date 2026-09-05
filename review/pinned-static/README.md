# 静态贴图：渲染优化与验证

本次优化把原 WPF 阴影/辉光效果裁剪到四周可见区域，中央实色直接填充。颜色、10 DIP 外围、模糊半径、透明度、RenderingBias、独立鼠标穿透窗口均保持原值。绘制只在尺寸、DPI 和激活状态变化时重建，平移复用现有绘制。生产改动集中在现有 `PinnedImageTranslateChromeWindow.cs`。

原 `DropShadowEffect` 会创建覆盖整个矩形的中间渲染表面；Quality 模糊还使用两张浮点中间纹理，见 [WPF DropShadowEffect 实现](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/WpfGfx/core/resources/DropShadowEffect.cpp) 与 [WPF BlurEffect 实现](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/WpfGfx/core/resources/BlurEffect.cpp)。因此窗口和模糊的资源成本会超过单张原图。

## 复现

```powershell
dotnet run --project review/pinned-static/PinnedPerf.csproj -c Release -p:EnableSourceControlManagerQueries=false -- --chrome-pixels
dotnet run --project review/pinned-static/PinnedPerf.csproj -c Release -p:EnableSourceControlManagerQueries=false -- --components
dotnet run --project review/pinned-static/PinnedPerf.csproj -c Release -p:EnableSourceControlManagerQueries=false -- --pin-memory
dotnet run --project review/pinned-static/PinnedPerf.csproj -c Release -p:EnableSourceControlManagerQueries=false
```

`--components` 设置环境变量 `PIN_RENDER_VARIANT=legacy` 时，以原来的两个 Border 作为对照；默认使用优化后的生产 Chrome。每次测量启动独立进程。

`--pin-memory` 使用生成的完成快照，通过生产 PinnedWindowController 连续创建 30 个窗口，每窗等待 Dispatcher 空闲和 100 ms，使其经历激活渲染。对照版本是优化前提交 `7160b35a299e125d0ee097035e65d15feed818a9`，运行同一份 PinSequence 测量代码。

`--chrome-preview` 打开优化前后并排的真实 WPF 图窗供桌面外观检查；关闭窗口即可结束。`ChromeReference.cs` 保留原 Border 绘制作为独立对照。

## 外观与交互验证

- 自动回归 326 项通过；最终上游候选原有测试 274 项通过。另有 24 项实际 Compact 窗口绑定与命令断言通过，见 [快捷键验证](pin-hotkey-validation.md)。
- 回归中有 168 组原绘制与新绘制的 BGRA 字节对比，覆盖 100% / 125% / 150% / 175% / 200% / 250% / 300% DPI、32×32 / 63×47 / 800×400 / 855×529 图片尺寸、阴影开关、激活/失焦反复切换及同对象改变尺寸，全部相等。
- 独立测量程序另有 56 组逐像素比较，结果见 [chrome-pixels.jsonl](chrome-pixels.jsonl)。
- 96 DPI 的可见透明 WPF 图窗中，左右并排检查原绘制与新绘制。桌面工具返回 JPEG，图形按相同 JPEG 块位置排列；解码后两区域差异为 0，记录见 [chrome-desktop-pixels.json](chrome-desktop-pixels.json)。无损逐像素判断使用上面的 RenderTargetBitmap 数据。
- 窗口生命周期检查覆盖所有内容/Chrome 窗的截图 cloak、重复请求不排队、租约重复释放、截图期间创建/关闭窗口、完成后恢复及关闭后回收。1 / 10 / 30 窗的已关闭 WeakReference 存活数均为 0。
- 提交者已手动测试最终优化版本的截图、OCR、翻译、Pin 与核心交互链路，并确认外观与交互符合预期；渲染改动另有上述像素比较与窗口检查验证。

## 内存

环境：Windows 11 build 26200、x64、.NET 10.0.11、96 DPI、WPF Tier 2。每组为 30 对独立创建且冻结的 800×400 BGRA 原图与标注图，原文/译文各 73 字符，窗口在屏幕外显示。每个配置三次独立进程测量，先预热一窗。

| 测量项目（三次中位数） | 原绘制 | 优化后 | 降幅 |
| --- | ---: | ---: | ---: |
| 分阶段加入阴影/辉光的私有内存增量 | 176.23 MiB | 51.33 MiB | 70.9% |
| 分阶段加入完整组件的总增量 | 459.79 MiB | 355.79 MiB | 22.6% |
| 连续 Pin、每窗经历激活/失焦后的总增量 | 371.77 MiB | 354.36 MiB | 4.7% |

分阶段测量依次加入原图、标注图、文字/快照、内容窗口、阴影、一个激活辉光；每阶段等待 Dispatcher 空闲及 500 ms，执行完整 GC 后读取 PrivateMemorySize64 与 GC.GetTotalMemory。此组顺序最终是 29 个阴影与 1 个辉光。

连续 Pin 测量通过生产控制器创建窗口；最后等待两秒并 GC 后采样。这两种创建顺序触发的 WPF 资源分配和复用不同。原始数据见 [chrome-memory-comparison.jsonl](chrome-memory-comparison.jsonl) 和 [pin-sequence-memory.jsonl](pin-sequence-memory.jsonl)。

优化后三个分阶段总增量为 350.65 / 355.79 / 357.71 MiB。取中间一轮，按该轮总增量计算组件占比：

| 组件加入阶段 | 私有内存增量 | 占比 |
| --- | ---: | ---: |
| 原图 | 26.77 MiB | 7.52% |
| 标注图、译文覆盖层、文字快照 | 33.68 MiB | 9.47% |
| 内容窗口、ImageZoom 与 WPF 渲染 | 244.02 MiB | 68.59% |
| 阴影/辉光伴随窗口 | 51.33 MiB | 14.43% |
| 合计 | 355.79 MiB | 100% |

百分比经过四舍五入。上述数值是组件加入阶段的进程增量，包含相应渲染资源与运行时缓存。实际 Pin 共享 Compact 已生成的位图。内容窗口继续复用原 ImageZoom、透明图支持和硬件渲染。

## 窗口生命周期采样

以下为同一机器上优化前后的窗口级采样，窗口位于屏幕外，创建耗时包含同步的 WPF/原生窗口创建；空闲 CPU 按进程 CPU 时间采样三秒，隐藏/恢复五次取中位数。

| 窗口数 | 原创建耗时 | 优化后创建耗时 | 原空闲 CPU / 3s | 优化后空闲 CPU / 3s |
| --- | ---: | ---: | ---: | ---: |
| 1 | 139.02 ms | 152.02 ms | 93.75 ms | 109.38 ms |
| 10 | 756.60 ms | 883.64 ms | 78.13 ms | 62.50 ms |
| 30 | 2161.35 ms | 2848.53 ms | 62.50 ms | 46.88 ms |

快照复制 1000 次平均 8.58 μs、每次托管分配 12,032 B；30 窗隐藏加恢复中位数 33.09 ms，包括两次 DWM 同步。原始结果见 [legacy-runtime-benchmark.jsonl](legacy-runtime-benchmark.jsonl) 和 [optimized-runtime-benchmark.jsonl](optimized-runtime-benchmark.jsonl)。这次优化减少渲染表面内存，快速批量创建增加了裁剪绘制节点的初始化成本。

可设置 `PIN_IDLE_PROBE=1` 追加三次空闲采样及托管分配记录。定位过程中确认绘制次数、边界生成次数、DPI 和激活状态在空闲采样内保持不变；生产代码没有轮询或后台重算。

早期测量保存在 `component-memory.jsonl`、`runtime-benchmark.jsonl`，当时的顺序、渲染版本及缓存状态与上述优化前后对照不同。本页与 PR 使用新的对照结果。

[test-image-1.png](test-image-1.png) 为真实使用场景测试截图，原文件名“测试图片1.png”。
