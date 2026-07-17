# VRCTranslate

VRCTranslate 是面向 Windows 10/11、VRChat PC 桌面模式的文字翻译工具。自己的文字通过 OSC Chatbox 发送；其他玩家画面中的文字由本地 RapidOCR 识别，再显示到独立译文浮窗。

项目不支持 PCVR，不注入或 Hook VRChat，不读取游戏进程内存，不使用数据库，不保存截图或聊天历史。

## 当前功能

- 支持中文、English、日本語三种界面语言，设置页一键切换，即时生效。
- 独立单行快捷输入浮窗，按 Enter 自动翻译并发送。
- OSC typing 状态始终同步，无需开关。
- 快捷输入与 OCR 译文浮窗分别控制置顶；主窗口永不强制置顶。
- OCR 捕获帧只在内存处理，译文浮窗只显示译文。
- 默认使用 Windows Graphics Capture 获取所选窗口内容；MSS 只作为用户显式选择的屏幕坐标兼容模式。
- 设置页固定显示“翻译模型 / OSC / OCR / 便携数据与诊断”四个入口，保存按钮不会随内容滚走。
- 所有数值项只能直接输入，鼠标滚轮和方向键不会改变数值。
- OCR 翻译采用有界队列、时效检查、会话隔离、顺序展示和内存缓存。
- 自身消息与 OCR 使用独立翻译档案和语言方向。
- 支持 DeepL、Google Cloud Translation、Argos Translate、OpenAI 兼容接口及测试回显。
- 配置、日志、缓存和 Argos 模型全部位于软件目录的 `data`，不回退到 C 盘用户目录。

## 运行环境

- Windows 10/11 64 位。
- Python 3.11.4 64 位。
- VRChat PC 桌面模式。

不需要 Miniconda，使用 Python 自带的 `venv` 即可。

## 首次运行

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

PowerShell 拒绝执行激活脚本时，无需修改系统策略，后续直接使用 `\.venv\Scripts\python.exe`。

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

需要测试 Argos 本地翻译时，先把 pip 缓存和临时目录放到项目所在盘，再安装可选组件：

```powershell
New-Item -ItemType Directory -Force data\cache\pip, data\cache\temp | Out-Null
$env:PIP_CACHE_DIR = "$PWD\data\cache\pip"
$env:TEMP = "$PWD\data\cache\temp"
$env:TMP = "$PWD\data\cache\temp"
python -m pip install -e ".[dev,offline]"
```

Argos Python 组件进入 `.venv`；语言模型由应用安装到 `data\models\argos`，不要放进 `site-packages` 或 `src`。

### 4. 启动

```powershell
python -m vrctranslate
```

未激活环境时：

```powershell
.\.venv\Scripts\python.exe -m vrctranslate
```

日常启动无需重新创建环境或安装依赖。

## 便携数据目录

开发运行以项目根目录为软件目录；打包运行以 exe 所在目录为软件目录：

```text
VRCTranslate\
├── VRCTranslate.exe          # 打包后存在
├── _internal\                # 打包后存在
└── data\
    ├── config.json
    ├── logs\app.log
    ├── cache\
    │   ├── downloads\
    │   └── third_party\
    ├── models\argos\
    └── third_party\
```

首次加载旧版根目录 `config.json` 时，会复制并迁移到 `data\config.json`，同时生成 `config.json.v1-backup`。API 密钥按需求以明文 JSON 保存，请勿分享或提交配置文件。

如果 `data` 不可写，程序会阻止启动并要求把完整软件目录移动到 D/E 盘普通可写位置，不会静默使用 AppData。

## 运行测试

普通测试不要求 Argos 模型：

```powershell
python -m pytest
```

真实 Argos 集成测试：

```powershell
python -m pytest -m argos
```

未安装组件或 `en → zh` 模型时，Argos 集成测试会明确跳过。

无界面测试环境可先设置：

```powershell
$env:QT_QPA_PLATFORM = "offscreen"
python -m pytest
```

## 打包

正式发布采用 one-folder，避免 one-file 向系统 `%TEMP%` 解压。普通版与含 Argos 运行组件的离线版命令见 [packaging/README.md](packaging/README.md)。

当前阶段禁止执行打包：必须先完成自动化测试以及真实 VRChat 的窗口化、无边框、多屏、遮挡和连续运行测试。现有 `dist` 不代表本轮源码。

## 项目结构

```text
src/vrctranslate/
├── domain/                    # 纯业务规则
├── application/               # 用例和端口；OCR 队列、缓存、调度分别实现
├── infrastructure/            # WGC/MSS 捕获路由、JSON schema/迁移、翻译适配器
├── presentation/qt/           # 四分区设置页、控制器、浮窗、主题和 SVG 资源
└── bootstrap.py               # 具体依赖的唯一装配位置
```

架构测试保证 `domain` 和 `application` 不依赖 Qt、HTTP、OCR、OSC 或 Windows API，界面层也不直接选择具体翻译适配器。

## 文档

- 界面和功能操作：[使用说明.md](使用说明.md)
- UI 与 OCR 修复依据：[UI与OCR问题修复计划.md](UI与OCR问题修复计划.md)
