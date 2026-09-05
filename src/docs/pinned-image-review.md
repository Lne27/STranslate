# Compact 结果贴图：本轮审阅说明

本轮按 [维护者在 PR #769 的回复](https://github.com/STranslate/STranslate/pull/769#issuecomment-5536176286) 重构。按提交者 2026-09-05 的最新要求，新增入口限于 **Compact 工具条上的 Pin 按钮**，不增加 Pin 快捷键或快捷键配置。真实用户调用链和桌面视觉验收由提交者自行完成。

## 基线与范围

- 原 PR 提交：`2f5d98df213c2ca4023da42a2a531b9e4fc42d56`。
- 已同步上游：`2a75118fe0fabc1135a619f4393eb3a8d936c5cd`（v2.0.10）。
- 删除第三种模式、Pinned ViewModel、执行协调器、选项/任务快照、工具条与自动重算。
- `MainWindowViewModel`、`HotkeySettings`、模式设置页面、`ServiceManager` 和 WeChat OCR 实现相对上述上游基线无本轮改动。
- Compact 完成 OCR/翻译后才可 Pin；先成功创建独立贴图，再释放 Compact。静态窗口只显示结果和处理选择、复制、窗口交互。
- 原图、红框标注图和译文矢量覆盖层不重新识别、不二次编码；选择数据逐项复制。
- 原 PR 的 Chrome 绘制、颜色、模糊、透明度、渲染偏好和边距保持原值，只修复延迟创建 HWND 时的截图 cloak 状态传递。

## 自动验证结果

- .NET SDK：10.0.400，Windows x64。
- Debug 和 Release 解决方案构建：通过，0 警告、0 错误。
- Release 测试：305 通过、0 失败、0 跳过。
- `git diff --check`：通过。语言资源保留上游文件换行，Git 检查按 CRLF 文件设置 `cr-at-eol`。
- 新增/更新测试覆盖：旧配置单字段迁移、32/63/64 像素截图尺寸、快照与原选择数据隔离、无效快照拒绝、Unicode 选词、独立译文块选择边界、切层清除高亮、复制全文不改变选择，以及 100%/125%/150%/175%/200% DPI 几何与 Chrome 状态参数。

本地以 partial clone 建立旧版本工作树；项目的旧 SourceLink 依赖不能读取该 Git 仓库格式。构建命令仅在命令行关闭源码版本查询，不修改项目构建配置：

```powershell
dotnet build src/STranslate.slnx -c Debug -p:EnableSourceControlManagerQueries=false
dotnet build src/STranslate.slnx -c Release -p:EnableSourceControlManagerQueries=false
dotnet test src/Tests/STranslate.Tests/STranslate.Tests.csproj -c Release -p:EnableSourceControlManagerQueries=false
```

这些结果是构建与自动回归证据，不代表真实截图链路已达到 100% 可靠，也不代表已经通过桌面逐像素视觉比较。

## 提交者验收清单

1. 设置图片翻译为 Compact，使用原有主窗口按钮或截图快捷键框选。执行中 Pin 禁用，结果生成后 Pin 可用；Standalone 不出现 Pin 入口。
2. 点击 Pin 后，Compact 关闭，原位置显示无工具条的独立置顶贴图；下一次截图仍能生成新的 Compact 结果。
3. 连续创建多个贴图；后续更换服务、语言、分段或关闭 Compact，不改变已固定的内容。
4. 在原文和译文两层分别验证拖选、双击选词、三击连续段、Ctrl+A/C，以及选区内/外右键菜单。三击边界使用现有视觉行标识，不跨独立覆盖块；CJK 选词为字符分类，不额外调用语言分词服务。
5. 空白右键仅有四项基础操作；复制全文与当前层一致且不新增高亮。切换原图时红框保留、旧选择清除。
6. 对照旧 PR 检查激活蓝光与失焦黑影。阴影开关不取消激活蓝光；其他已有贴图不受开关影响，新贴图继承最新默认值。
7. 验证空白拖动、方向键 1 像素/Shift 10 像素、空白双击及 Esc 关闭。菜单打开时方向键和 Esc 优先操作菜单。
8. 已有多个贴图时再次截图：内容和 Chrome 均应避让；正常完成、取消后恢复，不残留窗口。测试跨屏、不同 DPI、屏幕边缘和小截图。
9. 通过原有外部图片调用入口生成 Compact 并 Pin；同时验证无文字、无坐标、服务失败、重执行取消等情况下按钮和错误提示。

本轮只提交到个人 fork 的审阅分支，不新建或更新上游 PR。
