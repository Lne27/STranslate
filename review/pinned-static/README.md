# 静态贴图的窗口级检查与测量

本目录保留在提交者 fork，上游 PR 仅包含功能代码。

```powershell
dotnet run --project review/pinned-static/PinnedPerf.csproj -c Release -p:EnableSourceControlManagerQueries=false
dotnet run --project review/pinned-static/PinnedPerf.csproj -c Release -p:EnableSourceControlManagerQueries=false -- --components
```

`Program.cs` 实例化生产 WPF 窗口，使用固定生成的 800×400 图像和 73 字符段落。窗口在屏幕外显示，测量范围为窗口生命周期和性能。用户截图、OCR、翻译及交互链路由提交者手动验收。

窗口检查覆盖：已有窗口的内容与 Chrome 均 cloak、重复截图请求不排队、lease 重复释放、截图期间新增/关闭贴图、恢复后的 cloak 状态、关闭后 WeakReference 可回收。

生命周期测量先等待 WPF/JIT 初始化完成；测量 1、10、30 个窗口，空闲 CPU 每次采样 3 秒，隐藏/恢复每组 5 次取中位数。原始结果见 [runtime-benchmark.jsonl](runtime-benchmark.jsonl)。

- 快照复制：1000 次均值 7.64 μs，每次托管分配 12,032 B，原文/译文各 73 个字符，复用冻结位图。
- 创建窗口：1 / 10 / 30 窗共 134.3 / 419.9 / 1310.7 ms，包含 WPF/原生窗口创建。
- 空闲 CPU：本次基线及三组窗口的 3 秒进程 CPU 时间增量均记录为 0 ms。
- 内容与 Chrome 隐藏加恢复：中位数 33.1 / 33.4 / 33.4 ms，包括两次 DWM 同步。
- 三组关闭后的 WeakReference 存活数均为 0。
- 该程序从快照准备完成后开始计量窗口私有内存增量，分别为 33.5 / 67.3 / 161.1 MiB；标注图由原图 Clone，分组连续运行，共用同一进程的资源缓存。

`ComponentMemory.cs` 从创建图像开始分阶段记录组件内存，原始结果见 [component-memory.jsonl](component-memory.jsonl)。三次独立进程运行；每次先预热一套完整组件，再等待 3 秒开始测量。每阶段等待 Dispatcher 空闲及 500 ms，完成 GC 后读取 PrivateMemorySize64 和 GC.GetTotalMemory。使用实际生产内容窗口，先关闭阴影并禁止激活，之后通过测试程序中的字段反射取得实际 Chrome 窗口，依次显示阴影和一个激活辉光；生产代码无需测量专用分支。

环境：Windows 11 build 26200、x64、.NET 10.0.11、96 DPI、WPF Tier 2；30 组 800×400 BGRA 图像，原图与标注图分别独立创建，全部冻结，窗口位于屏幕外。最终为 29 个阴影、1 个激活辉光。各阶段保留前面阶段的对象，按加入顺序计量进程内存增量，包含相应的渲染资源与运行时缓存。文字阶段进程私有内存受 GC 释放影响，与标注图合并汇总；同时单独记录文字阶段托管保留量。

三次总增量为 467.66 / 487.98 / 465.50 MiB。取总增量居中的第一轮作组件占比，分母 467.66 MiB：

| 组件加入阶段 | 私有内存增量 | 占比 |
| --- | ---: | ---: |
| 原图 | 27.68 MiB | 5.92% |
| 标注图、译文覆盖层与文字快照 | 37.22 MiB | 7.96% |
| 内容窗口、ImageZoom 与 WPF 渲染 | 263.30 MiB | 56.30% |
| 阴影及激活辉光伴随窗口 | 139.46 MiB | 29.82% |
| 合计 | 467.66 MiB | 100.00% |

本轮文字覆盖层和快照的托管保留量增量为 505.53 KiB / 30 组；三轮该指标分别为 505.53 / 637.17 / 637.17 KiB。实际 Pin 复用 Compact 已生成的原图和标注图，本组完整组件测量从图像创建开始。

[test-image-1.png](test-image-1.png) 是提交者提供的原始“测试图片1.png”，用于 PR 展示 Compact、多贴图、阴影/辉光与菜单。

测试代码分支保留旧预览配置迁移及其回归测试；上游提交分支移除了该迁移。测试代码分支回归 315 项通过，上游最终代码原有测试 274 项通过。
