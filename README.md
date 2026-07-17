# VRCTranslate

VRCTranslate 是面向 Windows 10/11、VRChat PC 桌面模式的文字翻译工具。自己的文字通过 OSC Chatbox 发送；其他玩家画面中的文字由本地 RapidOCR 识别，可显示到独立译文浮窗、覆盖到识别区域，或同时使用两种方式。

项目不支持 PCVR，不注入或 Hook VRChat，不读取游戏进程内存，不使用数据库，不保存截图或聊天历史。

## 当前版本

当前版本为 `v0.5.0`。本版升级了中文、日文 OCR 识别模型，加入识别区域嵌字，并完善了 OCR 悬浮球、识别框和译文浮窗之间的联动。

首次启动生成的是可公开分发的安全默认配置：

| 项目 | 默认值 |
| --- | --- |
| 翻译服务档案 | 仅有“测试回显”，接口地址、API 密钥和模型名称均为空 |
| 自身消息（OSC） | 测试回显，自动识别 → 中文，仅发送译文 |
| 他人消息（OCR） | 测试回显，日文 → 中文 |
| OCR | 持续识别、目标标题 `VRChat`、空选区、独立译文浮窗 |

“测试回显”只用于验证界面和 OSC，不执行真实翻译。开发机已有的 `config.json` 或 `data\config.json` 不属于发布默认配置，也不会被 Git 跟踪或打进发布包。

## 当前功能

- 支持中文、English、日本語三种界面语言，设置页一键切换，即时生效。
- 独立单行快捷输入浮窗，按 Enter 自动翻译并发送。
- 快捷输入页直接调整输入浮窗置顶和宽度，修改后自动保存。
- OSC typing 状态始终同步，无需开关。
- 快捷输入与 OCR 译文浮窗分别控制置顶；主窗口永不强制置顶。
- OCR 捕获帧只在内存处理；译文浮窗可选择只显示译文，或同时显示识别原文。
- OCR 目标程序只在 OCR 主页面选择；悬浮球负责框选、启停和模式切换，透明识别框可拖动、缩放并直接关闭。
- OCR 页可直接选择目标程序，并显示最近一次 OCR 原文和译文。
- 中文使用 PP-OCRv5 Server Rec，日文使用 PP-OCRv6 Medium Rec；两个模型包可独立下载和删除。
- OCR 译文支持独立浮窗、识别区域嵌字和两者同时显示；OCR 页面、悬浮球菜单和识别框控制条保持同步。
- 嵌字按原始文字行精准遮盖，并按译文实际行宽绘制底板，不再遮挡整个识别区域。
- 标题、正文段落和项目符号按版面拆分为独立嵌字块；单次嵌字持续保留到下一次识别或选区发生变化。
- OCR 提供单次和持续两种模式；持续模式合并周期取帧与变化检测，并按文字位置过滤静止区域。
- 译文和拖动条使用独立高对比度深色卡片，在白色、深色和彩色画面上保持清晰。
- 默认使用 Windows Graphics Capture 获取所选窗口内容；MSS 只作为用户显式选择的屏幕坐标兼容模式。
- 设置页固定显示“翻译模型 / OSC / OCR / 便携数据与诊断”四个入口，保存按钮不会随内容滚走。
- 所有数值项只能直接输入，鼠标滚轮和方向键不会改变数值。
- 设置表单在窄窗口下自动换行，复选框使用带明确勾选标记的系统样式。
- OCR 翻译采用有界队列、时效检查、会话隔离、顺序展示和内存缓存。
- 自身消息与 OCR 使用独立翻译档案和语言方向。
- 支持 DeepL、Google Cloud Translation、Google 免费接口、腾讯翻译、OpenAI 兼容接口及测试回显。
- Argos Translate 及其离线语言包已经移除；本地只执行 OCR，不提供离线机器翻译。
- 配置、日志和缓存全部位于软件目录的 `data`，不回退到 C 盘用户目录。

## 运行环境

- Windows 10/11 64 位。
- Python 3.11.4 64 位。
- VRChat PC 桌面模式。

不需要 Miniconda，使用 Python 自带的 `venv` 即可。

## 运行发布包

发布版采用 one-folder 目录形式。请完整解压整个 `VRCTranslate` 文件夹，再运行其中的 `VRCTranslate.exe`；不要单独复制 exe，也不要直接在压缩包内启动。

建议把完整目录放到 D/E 盘的普通可写位置，例如：

```text
E:\Tools\VRCTranslate\
├── VRCTranslate.exe
├── _internal\
├── data\
└── 使用说明.md
```

不要安装到 `Program Files` 等普通用户不可写目录。程序不会把配置、日志、缓存或模型转存到 C 盘 AppData。

## 从源码运行

以下命令均在项目根目录执行。

### 1. 创建虚拟环境

```powershell
python -m venv .venv
```

### 2. 激活虚拟环境

PowerShell：

```powershell
& .\.venv\Scripts\Activate.ps1
```

CMD：

```bat
.venv\Scripts\activate.bat
```

激活脚本通常不会输出提示。执行下列命令，路径指向项目 `.venv\Scripts\python.exe` 即表示成功：

```powershell
python -c "import sys; print(sys.executable)"
```

PowerShell 拒绝执行激活脚本时，无需修改系统策略，后续直接使用 `.\.venv\Scripts\python.exe`。

### 3. 安装依赖

普通运行：

```powershell
python -m pip install -e .
```

普通依赖会同时安装 `windows-capture`，用于 Windows 10/11 的窗口内容捕获。依赖安装在当前项目的 `.venv`；捕获帧只在内存中处理。

开发和测试：

```powershell
python -m pip install -e ".[dev]"
```

### 4. 启动

```powershell
python -m vrctranslate
```

未激活环境时：

```powershell
.\.venv\Scripts\python.exe -m vrctranslate
```

日常启动无需重新创建环境或安装依赖。

## 安装 OCR 模型

首次使用 OCR 前，进入“设置 → OCR → 本地 OCR 模型”，按识别语言点击“下载并安装”：

- 中文高精度包约 90MB（十进制）。
- 日文高精度包约 82MB（十进制）。

模型不会静默下载，也不随源码或发布包附带。下载、校验和正式文件全部位于软件目录的 `data`；只使用 OSC 自身消息翻译时无需安装 OCR 模型。

## 便携数据目录

开发运行以项目根目录为软件目录；打包运行以 exe 所在目录为软件目录：

```text
VRCTranslate\
├── VRCTranslate.exe          # 打包后存在
├── _internal\                # 打包后存在
└── data\
    ├── config.json
    ├── logs\app.log
    ├── models\ocr\             # 按需下载的中文/日文 OCR 模型
    └── cache\ocr-models\       # 下载临时文件，成功后清除
```

首次加载旧版根目录 `config.json` 时，会复制并迁移到 `data\config.json`，同时生成 `config.json.v1-backup`。API 密钥按需求以明文 JSON 保存，请勿分享、上传或提交配置文件。

如果 `data` 不可写，程序会阻止启动并要求把完整软件目录移动到 D/E 盘普通可写位置，不会静默使用 AppData。

## 运行测试

```powershell
python -m pytest
```

无界面测试环境可先设置：

```powershell
$env:QT_QPA_PLATFORM = "offscreen"
python -m pytest
```

## 打包

正式发布采用 one-folder，避免 one-file 向系统 `%TEMP%` 解压。构建命令见 [packaging/README.md](packaging/README.md)。

`v0.5.0` 已完成 one-folder 构建，输出目录为 `dist\VRCTranslate\`。当前开发机构建约 316 MiB，实际大小会随依赖版本略有变化。构建脚本不会预置 OCR 模型、用户配置、日志或 API 密钥；启动构建后的 exe 才会在 `data` 中生成默认配置和日志。

向外分发前应再次检查发布目录，确保测试启动产生的 `data\config.json`、日志、缓存和模型没有被放进压缩包。

重新构建前仍应先运行自动化测试，并完成真实 VRChat 的窗口化、无边框、多屏、遮挡及连续运行测试。打包是发布流程的最后一步，不应使用旧的 `dist` 代替当前源码构建。

## 项目结构

```text
src/vrctranslate/
├── domain/                    # 纯业务规则
├── application/               # 用例和端口
│   └── use_cases/ocr/         # OCR 调度、空间文字跟踪、队列、缓存、顺序缓冲
├── infrastructure/            # WGC/MSS、JSON 设置和在线翻译适配器
│   └── ocr/                   # RapidOCR 引擎、内置模型清单和便携模型管理器
├── presentation/qt/
│   ├── controllers/settings/  # 翻译服务测试
│   ├── pages/settings/translation/
│   │   ├── profile_editor.py  # 翻译服务档案
│   │   └── routes_tab.py      # OSC/OCR 独立路由
│   ├── windows/ocr_overlay/   # 浮窗表面、内容、条目和原生交互
│   ├── windows/ocr_inline/    # 与识别区域对齐的鼠标穿透嵌字层
│   ├── windows/ocr_region/    # 可拖动缩放的 OCR 识别区域框
│   ├── windows/ocr_orb/       # OCR 悬浮球和操作菜单
│   └── resources/styles/      # 基础、主窗、表单、设置和悬浮工具样式
└── bootstrap.py               # 具体依赖的唯一装配位置
```

架构测试保证 `domain` 和 `application` 不依赖 Qt、HTTP、OCR、OSC 或 Windows API，界面层也不直接选择具体翻译适配器。

## 文档

- 界面和功能操作：[使用说明.md](使用说明.md)
- OCR 模型升级与嵌字设计：[OCR模型升级方案.md](OCR模型升级方案.md)
