# Compact 结果贴图：本轮审阅说明

本轮按 [维护者在 PR #769 的回复](https://github.com/STranslate/STranslate/pull/769#issuecomment-5536176286) 重构。按提交者 2026-09-05 的最新要求，新增入口限于 **Compact 工具条上的 Pin 按钮**，不增加 Pin 快捷键或快捷键配置。真实用户调用链和桌面视觉验收由提交者自行完成。

## 基线与范围

- 原 PR 提交：`2f5d98df213c2ca4023da42a2a531b9e4fc42d56`。
- 已同步上游：`2a75118fe0fabc1135a619f4393eb3a8d936c5cd`（v2.0.10）。
- 删除第三种模式、Pinned ViewModel、执行协调器、选项/任务快照、工具条与自动重算。
- `MainWindowViewModel`、`HotkeySettings`、模式设置页面、`ServiceManager` 和 WeChat OCR 实现相对上述上游基线无本轮改动。
- Compact 完成 OCR/翻译后才可 Pin；先成功创建独立贴图，再释放 Compact。静态窗口只显示结果和处理选择、复制、窗口交互。
- 原图、红框标注图和译文矢量覆盖层不重新识别、不二次编码；选择数据逐项复制。
- 原 PR 的 Chrome 颜色、模糊、透明度、渲染偏好和边距保持原值。绘制改为四边裁剪加中央实色填充，减少中间表面，并修复延迟创建 HWND 时的截图 cloak 状态传递。

## 自动验证结果

- .NET SDK：10.0.400，Windows x64。
- Debug 和 Release 解决方案构建：通过，0 警告、0 错误。
- Release 测试：321 通过、0 失败、0 跳过；最终上游候选原有 274 项测试通过。
- `git diff --check`：通过。语言资源保留上游文件换行，Git 检查按 CRLF 文件设置 `cr-at-eol`。
- 新增/更新测试覆盖：旧配置单字段迁移、32/63/64 像素截图尺寸、快照与原选择数据隔离、当前显示层继承、无效快照拒绝、Unicode 选词、四种分段模式的完整段落、多栏隔离、软换行与裁剪文本、切层清除高亮、复制全文不改变选择，以及 100%/125%/150%/175%/200% DPI 几何、Chrome 状态参数与断屏后定位。

本地以 partial clone 建立旧版本工作树；项目的旧 SourceLink 依赖不能读取该 Git 仓库格式。构建命令仅在命令行关闭源码版本查询，不修改项目构建配置：

```powershell
dotnet build src/STranslate.slnx -c Debug -p:EnableSourceControlManagerQueries=false
dotnet build src/STranslate.slnx -c Release -p:EnableSourceControlManagerQueries=false
dotnet test src/Tests/STranslate.Tests/STranslate.Tests.csproj -c Release -p:EnableSourceControlManagerQueries=false
```

提交者已完成截图/OCR/翻译/Pin 的手动测试。本次绘制改动增加了 168 组无损逐像素回归，覆盖反复切换、尺寸变化及 100%～300% DPI；另完成桌面并排外观检查。完整条件与结果见窗口测量说明。

## 提交者验收清单

1. 设置图片翻译为 Compact，使用原有主窗口按钮或截图快捷键框选。执行中 Pin 禁用，结果生成后 Pin 可用；Standalone 不出现 Pin 入口。
2. 点击 Pin 后，Compact 关闭，原位置显示无工具条的独立置顶贴图；下一次截图仍能生成新的 Compact 结果。
3. 连续创建多个贴图；后续更换服务、语言、分段或关闭 Compact，不改变已固定的内容。
4. 在原文和译文两层分别验证拖选、双击选词、三击完整段落、Ctrl+A/C，以及选区内/外右键菜单。三击使用现有分析器的段落归属，跨软换行但不跨段/串栏；NoMerge 保持原始块边界。CJK 选词仍为字符分类，不额外调用语言分词服务。
5. 空白右键仅有四项基础操作；复制全文与当前层一致且不新增高亮。切换原图时红框保留、旧选择清除。
6. 对照旧 PR 检查激活蓝光与失焦黑影。阴影开关不取消激活蓝光；其他已有贴图不受开关影响，新贴图继承最新默认值。
7. 验证空白拖动、方向键 1 像素/Shift 10 像素、空白双击及 Esc 关闭。菜单打开时方向键和 Esc 优先操作菜单。
8. 已有多个贴图时再次截图：内容和 Chrome 均应避让；正常完成、取消后恢复，不残留窗口。测试跨屏、不同 DPI、屏幕边缘和小截图。
9. 通过原有外部图片调用入口生成 Compact 并 Pin；同时验证无文字、无坐标、服务失败、重执行取消等情况下按钮和错误提示。

## 文件职责与冲突审阅

- `PinnedImageTranslateWindow`：独立静态窗口，避免复用 Compact 时携带 VM、设置订阅和服务生命周期。
- `PinnedImageTranslateChromeWindow`：沿用旧 PR 的伴随窗口，提供原样外扩阴影/辉光和鼠标穿透。不是第二个业务窗口，不能直接改用普通窗口边框而保证原外观不变。
- `PinnedWindowController`：管理窗口集合、截图隐藏租约和共用复制操作，没有业务执行协调。配置转换器已合并到现有 `Settings.cs`，不再单独建文件。
- Pin 继承当前原图/译文层，之后每窗独立；服务删除、分段与语言设置变化不能触发贴图重算。菜单动态资源跟随界面语言，覆盖层保持快照时主题，以免暗中改变固定结果。
- 阴影开关仅改变当前窗口，同时保存新窗口的默认值；已有其他窗口不跟随。激活辉光不受阴影开关影响，仍与旧 PR 一致。
- 运行中的截图隐藏内容与 Chrome；截图期间创建的窗口不抢焦点且从 HWND 创建时即隐藏。窗口关闭可注销，租约结束仅恢复剩余窗口。
- DPI 改变按原始物理像素尺寸重排；显示器断开导致贴图完全不可见时移到最近工作区。跨 DPI 几何有自动验证，真实硬件热拔插/RDP 切换仍在手测范围。
- 开启剪贴板监听后，复制文字仍可触发现有剪贴板翻译行为；未偷偷禁用用户的监听设置。全局快捷键冲突也遵循上游已有配置，贴图未注册新的全局热键。
- 翻译部分失败时沿用上游“保留该块 OCR 原文”的策略；贴图冻结该结果，不增加重试或把原文当成新翻译。

## 性能与上游提交范围

窗口级检查和测量见 [测量说明](../../review/pinned-static/README.md)，包含可运行程序、原始数据及创建耗时。最终绘制优化在连续 30 次 Pin 测量中将总内存增量中位数从 371.77 MiB 降至 354.36 MiB；分阶段创建时，阴影/辉光阶段增量从 176.23 MiB 降至 51.33 MiB。快照共享冻结图片，绘制按尺寸、DPI 和激活状态更新。

`feature/compact-pin` 分支包含应用代码和运行所需语言资源。测试代码分支与其生产源码的差别仅为测试代码分支保留旧预览配置迁移。英文 PR 说明见 [PR 草稿](../../review/pinned-static/upstream-pr-draft.md)。
