# V2 测试目录

测试按层放置，便于单独运行：

- `VrcTranslate.Core.Tests`：语言策略、路由、不变量契约、浮窗布局和输入输出格式。
- `VrcTranslate.Application.Tests`：文字与语音共用翻译服务、双目标翻译、取消/重试、语音分段和采集会话。
- `VrcTranslate.Infrastructure.Tests`：JSON 配置、Provider 回显、Provider Catalog、音频转换和本地模型契约。

在 `v2` 目录执行：

```powershell
dotnet test VrcTranslate.sln -c Debug -p:Platform=x64
```

当前分层测试共 119 项（Core 46、Application 21、Infrastructure 52）。

只运行某一层时，直接把解决方案替换为对应的 `.csproj` 路径即可。

给别人手动测试前，执行完整验证脚本。它会先构建、运行全部测试，再检查品牌图标、唯一的翻译导航、服务卡片高亮切换、默认术语和按钮边界，最后逐页启动运行、输入、字幕、翻译、设置、指南各 5 秒，覆盖页面导航和 XAML 初始化崩溃：

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\tests\Invoke-V2Validation.ps1
```

发布手测包更新后，再执行发布包冒烟（包含资源、档案对话框、输入与字幕窗口、主窗口退出联动，以及系统原生普通 Windows 窗口行为检查）：

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\tests\Invoke-V2ReleaseSmoke.ps1
```

可单独执行浮窗运行时测试。输入和字幕使用系统原生普通 Windows 窗口，具备系统标题栏、拖动和八方向缩放。脚本使用独立的临时数据目录，验证两窗默认 90% 透明度、两个页面滑杆实时且分别生效、系统关闭仅隐藏窗口、快捷键复用同一 HWND、输入区跟随窗口缩放、主程序退出后销毁窗口，以及位置、尺寸和透明度跨进程重启恢复：

```powershell
.\tests\Invoke-V2OverlayWindowSmoke.ps1
```

Release 手测包发布后，以下入口会先检查发布资源，再对发布 EXE 执行同一套原生窗口验收：

```powershell
.\tests\Invoke-ReleaseOverlayVisualSmoke.ps1
```
