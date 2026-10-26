# Bark Tool for Windows

C# / WPF .NET 10 桌面工具。悬浮圆盘、加密 Bark 推送、剪贴板直发、图片预览确认及 EasyImage 上传。

## 1.6 使用与安装

本次完整包：`artifacts/package-win-x64-1.6/`。可从该目录运行 `BarkTool.exe`，**不要仅复制 EXE**：这是包含 .NET 运行时的完整自包含目录包。

推荐固定目录安装：先从托盘退出所有旧版，在 PowerShell 中运行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\ai\bark-tool\artifacts\package-win-x64-1.6\scripts\install-update.ps1"
```

安装到 `%LOCALAPPDATA%\Programs\BarkTool`，生成开始菜单快捷方式。原配置与加密密钥仍在 `%LOCALAPPDATA%\BarkTool`；安装脚本不删除旧便携版、不强杀运行程序。仅原本启用的开机自启会迁移到固定目录，没有启用则不擅自开启。

以后下载/构建新完整包，退出旧程序后运行新包的同一安装脚本即可更新。保留上一版供手动回退：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "D:\ai\bark-tool\artifacts\package-win-x64-1.6\scripts\install-update.ps1" -Rollback
```

SHA-256 清单用于包完整性检查，**不是发布者身份认证**。本版不包含联网自动下载更新。

## 快捷键

设置中可启用全局快捷键，并分别启用/修改四个动作：

- 编写文字：建议 `Ctrl+Alt+B`
- 直接发送剪贴板：建议 `Ctrl+Alt+V`
- 选图并预览：建议 `Ctrl+Alt+P`
- 框选截图并预览：建议 `Ctrl+Alt+S`（该动作默认关闭）

托盘可一键暂停/恢复全局快捷键，不修改已保存组合；恢复时若被其他软件占用会报冲突。

默认全部不注册（总开关关闭）。点击输入框按下所需组合键；冲突或重复会显示错误，旧的有效注册不会因新冲突被抢占。修改后点击“应用快捷键”立即生效，不必同时保存 Bark 配置；“恢复默认组合”不会擅自启用，需要再次应用。

## 1.3 截图、图片与通知

- 圆盘、托盘、悬浮右键或截图快捷键启动框选，松开鼠标进入预览，Esc/右键取消。只在用户主动触发时捕获；确认前不上传、不改剪贴板。
- 设置可开启图片缩放/压缩（默认关闭），最长边320–4096、JPEG质量50–95。不会放大小图，透明图保留PNG，GIF/多帧保留原文件。预览显示尺寸和处理前后大小，最终上传不超过10MiB。
- 游戏名单输入实际运行的进程名或选择EXE（不是启动器）。名单游戏处于前台时隐藏，窗口模式也适用；进程保护导致无法查询时可能识别不到。
- 通知支持普通、静默（passive）和时效性（timeSensitive）；可关闭请求自动复制。选项同正文一起加密。实际提示和时效性权限由iPhone系统控制，不能绕过专注模式策略。

## 无记录发送

1.2 已移除发送记录和手动重试功能，不保存新历史或重试缓存。每次点击执行一次发送；失败保留当前窗口草稿，但不提供历史恢复或上传链接复用。网络超时后若再次主动发送，可能产生重复消息。“成功”仅表示 Bark API 请求成功，不保证 iPhone 已收到。

旧版 `%LOCALAPPDATA%\BarkTool\History` 如已存在，不会被自动读取或删除；不需要时可在退出旧版后自行清理该目录，不要删除整个配置目录。

## 悬浮交互

- 设置支持32–56 DIP大小、40%–100%正常透明度。
- 默认靠近所在显示器工作区边缘16 DIP内自动吸附，保持整个按钮可见。
- 默认闲置3秒后淡至30%，移入立即恢复；拖动、菜单打开时不淡出。
- 开启“贴边半隐藏”（默认开启）后，悬浮按钮贴住左右屏幕边缘时只留约 6 DIP 窄条，鼠标移近滑出完整按钮；拖动、菜单、圆盘打开时保持展开。纵向不半隐藏。
- 发送面板支持 `Ctrl+V` 直接粘贴剪贴板截图到预览（剪贴板只有文字时仍按文本粘贴）；也可从其他程序拖入图片。
- “选择图片”支持多选；发送时按顺序逐张上传和推送，状态栏显示当前进度。每张图片独立推送，某张失败时会停止后续发送并提示。

## 快捷窗口体验

- 快捷窗口会记住上次位置和大小。
- 正文和图片区域会为简洁模式预留固定空间，避免控件互相覆盖。
- 发送状态依次显示准备、上传图片、加密并推送等阶段，不保存正文或敏感信息。
- 全屏隐藏和手动隐藏优先，闲置淡出不会让已隐藏按钮自动弹出。
- 资源管理器拖入单张图片进入独立预览，确认前不上传，不影响文字草稿。
- 右键可打开文字、设置，或隐藏、退出；托盘可恢复隐藏按钮。

## 加密与隐私边界

iPhone Bark 推送加密配置 AES-CBC / PKCS7，密钥与 Windows 设置一致（UTF-8 16/24/32字节）；每次请求使用随机 IV。所有产品发送入口均要求有效加密密钥，不自动回退明文。图片链接放入密文，但 EasyImage 图片文件本身不做端到端加密。Bark autoCopy 尝试复制到 iPhone 系统剪贴板，不能承诺第三方输入法历史或自动粘贴输入框。

## 构建与针对性验证

SDK 在本项目 `.tools/dotnet/dotnet.exe`，不需要修改系统 PATH。

```powershell
.tools/dotnet/dotnet.exe run --project tests/BarkTool.Core.Tests/BarkTool.Core.Tests.csproj
.tools/dotnet/dotnet.exe run --project tests/BarkTool.Desktop.Tests/BarkTool.Desktop.Tests.csproj
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/package.ps1 -Version 1.6.0.0 -Output artifacts/package-new
powershell -NoProfile -ExecutionPolicy Bypass -File tests/update-tests.ps1 -Fixture artifacts/package-new
```

安装测试只使用临时目录和隔离注册表键，不修改真实安装、自启或系统快捷键。桌面测试使用假HTTP与注入剪贴板快照，不读取真实剪贴板/密钥。真实游戏、Explorer拖放、跨屏DPI、全局快捷键与其他软件冲突，以及手机接收仍需实际桌面/设备验证。
