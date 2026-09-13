# VRCTranslate V2

V2 是新的 Windows 原生产品入口，采用 C#、.NET 10 和 WinUI 3。它不包含 OCR；OCR 只在指南中说明可使用 QQ `Ctrl+Alt+F`，不安装 OCR 模型、不捕获屏幕。

当前垂直切片包含：

- 统一翻译路由：文字输入和语音识别文本共用同一翻译服务；字幕输出固定为简体中文；
- 翻译模型配置与连接测试；
- 输入浮窗与 OSC Chatbox 契约。输入内容固定按简体中文处理；第一语言和可选第二语言在“输入”页面配置，并同时预览、发送；
- 使用系统标题栏、原生拖动和八方向缩放的输入/字幕窗口；两者默认宽度分别为 1240px 和 1400px，位置与大小会在退出后恢复；
- 输入与字幕透明度可分别在对应页面调节为 60%～100%，默认 90%，整窗（包括标题栏）实时生效并自动记忆；
- 内置的 Whisper Base Q5_1 本地识别模型，覆盖英语、日语、韩语，随发布包自带、无需下载；
- WinUI 3 页面骨架和内部一次性迁移接口（不在用户界面提供旧版导入）。

页面职责保持单一：运行页负责查看状态和进入功能，翻译页负责服务档案、模型、地址、密钥和路由，语音页不提供进程选择，正式音频目标固定为 VRChat。翻译页保存的当前方案会同时供手动文字和语音识别文本使用。

代码按 `Core -> Application -> Infrastructure -> Desktop` 分层，测试按相同边界放在 `tests/` 下。旧 Python/Qt 项目暂时保留，审核 V2 通过后再替换。

正式运行端不依赖 Python、PySide6 或 PyInstaller。OCR、屏幕捕获和多模态图片翻译不属于 V2 初始发布范围；指南只保留 QQ `Ctrl+Alt+F` 外部屏幕翻译说明。当前语音识别使用内置的 Whisper Base Q5_1 本地模型，随发布包放在程序目录 `Models\` 下，运行时优先加载该副本，不会回退到系统识别；模型缺失时仍可回退为从语音页按需下载到 `%LOCALAPPDATA%\VRCTranslate\v2\models\speech`。模型文件本身不进入源码库，构建前用 `tests\Import-BundledSpeechModel.ps1` 一次性导入到 `v2\assets\models\speech\`。音频采集端口和 Windows 麦克风/回环适配器已经分层，桌面端已经接通麦克风和系统回环；回环目前读取系统输出混音，VRChat 进程级过滤仍待后续完善。

## 构建

```powershell
dotnet restore v2\VrcTranslate.sln
dotnet build v2\VrcTranslate.sln -c Debug -p:Platform=x64
dotnet test v2\VrcTranslate.sln -c Debug -p:Platform=x64
```

构建后可直接双击下面的程序进行手动测试（必须保留同目录运行库和资源文件）：

`v2\src\VrcTranslate.Desktop\bin\x64\Debug\net10.0-windows10.0.19041.0\VrcTranslate.exe`

也可以打开上述输出目录进行桌面端调试；WinUI 3 应用需要在 Windows 环境中启动。

手动测试前执行 `tests\Invoke-V2Validation.ps1`。该脚本会验证构建、分层测试、翻译页 UI 交互、两个普通 Windows 浮窗的原生拖动/缩放、透明度、关闭/重开、布局恢复，以及六个桌面页面启动；内置模型在运行时直接探测，无需联网下载。

重新发布 `artifacts\manual-test` 后执行 `tests\Invoke-V2ReleaseSmoke.ps1`；它会检查发布资源、翻译档案对话框、两个浮窗的系统窗框、透明度与主窗口退出联动，再把通过的包交给人工测试。发布前若 `v2\assets\models\speech\ggml-base-q5_1.bin` 不存在，构建会告警且该包将回退为运行时下载。

Release 手测包目录：`v2\artifacts\manual-test\`。可直接双击：

`v2\artifacts\manual-test\VrcTranslate.exe`

WinUI 3 项目需要 Windows App SDK NuGet 包。API 密钥和语音模型文件都不会写入源码：密钥只存在于用户配置，模型由 `tests\Import-BundledSpeechModel.ps1` 放入 `v2\assets\`（已被 git 忽略）后在构建时打入包内。
