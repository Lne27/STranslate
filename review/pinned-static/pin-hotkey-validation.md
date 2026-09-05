# Compact Pin 快捷键验证

默认 Ctrl+Shift+P，仅 Compact 生效。复用原 HotkeySettings、HotkeyControl、配置存储和冲突检查；WPF KeyBinding 直接绑定现有 Pin 按钮命令。正式代码新增 23 行，无新弹窗、引导提示、全局按键钩子或轮询。

## 自动测试

- Release 回归：326 项通过，包括 5 项新增配置测试；上游候选原有测试 274 项通过。
- 编译后的实际 Compact 窗口：20 项断言通过。覆盖默认键、实时改成 F8、清空、重置、按钮与按键共享命令、未完成和执行中拒绝 Pin、完成后创建唯一贴图、关闭后释放内容和输入绑定。
- 窗口测试使用预置结果，生成的贴图放在屏幕外。未调用截图、OCR 或网络服务，也不发送键盘鼠标输入。
- Release 构建零警告、零错误；Debug 构建成功，有上游空 Fody 配置导致的 Costura 提示。

```powershell
dotnet test src/Tests/STranslate.Tests/STranslate.Tests.csproj -c Release -p:EnableSourceControlManagerQueries=false
dotnet run --project review/pinned-static/PinnedPerf.csproj -c Release -p:EnableSourceControlManagerQueries=false -- --hotkeys
```

原始窗口断言结果见 [pin-hotkey-bindings.log](pin-hotkey-bindings.log)。测试源文件为 `CompactHotkeys.cs` 和 `src/Tests/STranslate.Tests/PinHotkeyTests.cs`。

## 桌面手测

设置页已检查默认显示、Ctrl+R 冲突提示及改成 F8 后保存。后续桌面操作由提交者验证：

1. 普通截图生成 Compact 翻译结果后，按 Ctrl+Shift+P，确认生成无工具条贴图且 Compact 关闭。
2. 在设置中改为 F8，确认 F8 生效、旧键不再触发；清空后键盘入口失效但 Pin 按钮仍可使用；重置并重启后恢复默认键。
3. 执行中、主窗口、Standalone 和已生成的贴图中，快捷键不会另行生成贴图。

这次快捷键改动未修改图像、选择或阴影绘制代码。PR 中的渲染和内存数据来自同一渲染实现的既有测量。
