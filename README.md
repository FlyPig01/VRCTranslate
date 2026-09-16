# VRCTranslate

Windows 原生 VRChat 翻译工具，采用 C#、.NET 10 和 WinUI 3。不包含 OCR；OCR 只在指南中说明可使用 QQ `Ctrl+Alt+F`，不安装 OCR 模型、不捕获屏幕。

> **使用声明**：[MIT 许可证](LICENSE) · 免费开源、无付费功能 · **非官方**，与 VRChat 及各家翻译服务商无隶属关系 ·
> **不修改游戏**（他人语音经系统回环采集，字幕只在本地浮窗显示；自身消息可经官方 OSC Chatbox 发送）· 识别与翻译**都可能出错** ·
> 第三方服务**费用自理** · **不外发数据**（语音识别在本机完成）。全文见 [DISCLAIMER.md](DISCLAIMER.md)。

> `v1.0.0` 起采用原生 C#、.NET 与 WinUI 架构，旧 Python/Qt 实现已移除。

功能包含：

- 统一翻译路由：文字输入和语音识别文本共用同一翻译服务；字幕输出固定为简体中文；
- 翻译模型配置与连接测试（DeepSeek、腾讯云、阿里云等；另含本地回显档案用于离线自检）；
- 输入浮窗与 OSC Chatbox 契约。输入内容固定按简体中文处理；第一语言和可选第二语言在“输入”页面配置，并同时预览、发送；
- 使用系统标题栏、原生拖动和八方向缩放的输入/字幕窗口；两者默认宽度分别为 1240px 和 1400px，位置与大小会在退出后恢复；
- 输入与字幕透明度可分别在对应页面调节为 60%～100%，默认 90%，整窗（包括标题栏）实时生效并自动记忆；
- 内置的 SenseVoiceSmall INT8 本地识别模型（sherpa-onnx），覆盖中文、英语、日语、韩语并兼容粤语口音，随发布包自带、无需下载；识别会话在应用层托管，切换页面不会中断；
- 字幕可区分说话人：本地声纹识别给每句打上「说话人A/B/C」标签，用户命名一次（例如「小明」）后跨会话自动沿用；声纹库只保存在程序目录内、可随时删除。默认关闭，开启后每句约多 50 ms。
- WinUI 3 页面骨架。

页面职责保持单一：运行页负责查看状态和进入功能，翻译页负责服务档案、模型、地址、密钥和路由，语音页不提供进程选择，正式音频目标固定为 VRChat。翻译页保存的当前方案会同时供手动文字和语音识别文本使用。

代码按 `Core -> Application -> Infrastructure -> Desktop` 分层，测试按相同边界放在 `tests/` 下。

语音识别使用内置的 SenseVoiceSmall INT8 本地模型（sherpa-onnx + ONNX Runtime），随正式包放在 `程序文件\Models\sensevoice\` 下（`model.int8.onnx` 与 `tokens.txt`），运行时优先加载该副本，不会回退到系统识别；模型缺失时仍可回退为从语音页按需下载到 `data\models\sensevoice`。说话人区分（字幕说话人标签）所需的分割与声纹嵌入模型同样内置在 `程序文件\Models\speaker\`，开箱即用。模型文件本身不进入源码库，构建前用 `tests\Import-BundledSpeechModel.ps1` 一次性下载校验并导入到 `assets\models\speech\`。切句用的 Silero VAD 模型内置在 `程序文件\Models\vad\`。他人语音回环会在检测到 VRChat 进程时自动跟随采集该进程的音频，进程退出后切回系统输出混音（语音页不提供手动进程选择）。

## 数据目录（便携式）

正式包根目录只保留启动 EXE、使用说明、“程序文件”和“说明与许可”。所有可写数据都在根目录的 `data\` 下，不写入用户配置目录。

| 内容 | 位置 |
|---|---|
| 设置（翻译路由、快捷键、浮窗外观与位置、术语表等） | `data\v2-*.json` |
| 说话人声纹库（仅用户命名后有内容） | `data\v2-speakers.json` |
| 按需下载的模型（内置模型缺失时才使用） | `data\models\` |
| 启动崩溃日志 | `data\startup-error.log` |

- `VRC_TRANSLATE_DATA_DIR` 可整体改到别处；UI 冒烟测试用它隔离数据目录。
- 只有程序目录不可写时（例如装在 `Program Files`）才回退到 `%LOCALAPPDATA%\VRCTranslate`，此时设置页会明确写出实际位置。
- 新解压的软件不会自动读取或复制其它安装目录与 `%LOCALAPPDATA%\VRCTranslate` 中的配置、密钥或声纹。
- 发布产物会剔除 `data\`，避免把本机设置与密钥打进分发包。

## 构建

```powershell
dotnet restore VrcTranslate.sln
dotnet build VrcTranslate.sln -c Debug -p:Platform=x64
dotnet test VrcTranslate.sln -c Debug -p:Platform=x64
```

构建后可直接双击下面的程序进行手动测试（必须保留同目录运行库和资源文件）：

`src\VrcTranslate.Desktop\bin\x64\Debug\net10.0-windows10.0.19041.0\VrcTranslate.exe`

也可以打开上述输出目录进行桌面端调试；WinUI 3 应用需要在 Windows 环境中启动。

手动测试前执行 `tests\Invoke-V2Validation.ps1`。该脚本会验证构建、分层测试、翻译页 UI 交互、两个普通 Windows 浮窗的原生拖动/缩放、透明度、关闭/重开、布局恢复，以及六个桌面页面启动；内置模型在运行时直接探测，无需联网下载。

重新发布 `artifacts\manual-test` 后执行 `tests\Invoke-V2ReleaseSmoke.ps1`；它会检查发布资源、版本、翻译档案对话框、两个浮窗的系统窗框、透明度与主窗口退出联动，再把通过的包交给人工测试。发布前若 `assets\models\speech\sensevoice\model.int8.onnx` 不存在，构建会告警且该包将回退为运行时下载。发布产物会自动剔除未使用的 Windows AI 组件、PDB 与异架构原生库；当前自包含目录约 452 MB，其中模型约 258 MB。

Release 手测包目录：`artifacts\manual-test\`。可直接双击：

`artifacts\manual-test\VrcTranslate.exe`

WinUI 3 项目需要 Windows App SDK NuGet 包。API 密钥和语音模型文件都不会写入源码：密钥只存在于用户配置，模型由 `tests\Import-BundledSpeechModel.ps1` 下载校验后放入 `assets\`（已被 git 忽略），构建时打入包内。

正式分发包由项目根目录执行 `pwsh -NoProfile -ExecutionPolicy Bypass -File scripts\Build-Distribution.ps1` 生成到 `dist\`。脚本会先运行全量验证，再从空目录发布并执行目录版与 ZIP 解压版冒烟测试；正式包不会包含 `data\`、API 密钥或本机设置。
