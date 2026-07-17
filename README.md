# VRCTranslate

VRCTranslate 是面向 Windows 10/11、VRChat PC 桌面模式的文字翻译工具。自己的文字通过 OSC Chatbox 发送；其他玩家画面中的文字由本地 RapidOCR 识别，再显示到独立译文浮窗。

项目不支持 PCVR，不注入或 Hook VRChat，不读取游戏进程内存，不使用数据库，不保存截图或聊天历史。

## 当前功能

- 支持中文、English、日本語三种界面语言，设置页一键切换，即时生效。
- 独立单行快捷输入浮窗，按 Enter 自动翻译并发送。
- 快捷输入页直接调整输入浮窗置顶和宽度，修改后自动保存。
- OSC typing 状态始终同步，无需开关。
- 快捷输入与 OCR 译文浮窗分别控制置顶；主窗口永不强制置顶。
- OCR 捕获帧只在内存处理；译文浮窗可选择只显示译文，或同时显示识别原文。
- OCR 悬浮球负责目标选择、框选、启停和模式切换；透明识别框可拖动、缩放并直接关闭。
- OCR 页可直接选择目标程序，并显示最近一次 OCR 原文和译文。
- OCR 提供单次和持续两种模式；持续模式合并周期取帧与变化检测，并按文字位置过滤静止区域。
- 译文和拖动条使用独立高对比度深色卡片，在白色、深色和彩色画面上保持清晰。
- 默认使用 Windows Graphics Capture 获取所选窗口内容；MSS 只作为用户显式选择的屏幕坐标兼容模式。
- 设置页固定显示“翻译模型 / OSC / OCR / 便携数据与诊断”四个入口，保存按钮不会随内容滚走。
- 所有数值项只能直接输入，鼠标滚轮和方向键不会改变数值。
- 设置表单在窄窗口下自动换行，复选框使用带明确勾选标记的系统样式。
- OCR 翻译采用有界队列、时效检查、会话隔离、顺序展示和内存缓存。
- 自身消息与 OCR 使用独立翻译档案和语言方向。
- 支持 DeepL、Google Cloud Translation、Google 免费接口、腾讯翻译、OpenAI 兼容接口及测试回显。
- 配置、日志和缓存全部位于软件目录的 `data`，不回退到 C 盘用户目录。

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
    └── cache\
```

首次加载旧版根目录 `config.json` 时，会复制并迁移到 `data\config.json`，同时生成 `config.json.v1-backup`。API 密钥按需求以明文 JSON 保存，请勿分享或提交配置文件。

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

当前阶段禁止执行打包：必须先完成自动化测试以及真实 VRChat 的窗口化、无边框、多屏、遮挡和连续运行测试。现有 `dist` 不代表本轮源码。

## 项目结构

```text
src/vrctranslate/
├── domain/                    # 纯业务规则
├── application/               # 用例和端口
│   └── use_cases/ocr/         # OCR 调度、空间文字跟踪、队列、缓存、顺序缓冲
├── infrastructure/            # WGC/MSS、JSON 设置和在线翻译适配器
├── presentation/qt/
│   ├── controllers/settings/  # 翻译服务测试
│   ├── pages/settings/translation/
│   │   ├── profile_editor.py  # 翻译服务档案
│   │   └── routes_tab.py      # OSC/OCR 独立路由
│   ├── windows/ocr_overlay/   # 浮窗表面、内容、条目和原生交互
│   ├── windows/ocr_region/    # 可拖动缩放的 OCR 识别区域框
│   ├── windows/ocr_orb/       # OCR 悬浮球和操作菜单
│   └── resources/styles/      # 基础、主窗、表单、设置和悬浮工具样式
└── bootstrap.py               # 具体依赖的唯一装配位置
```

架构测试保证 `domain` 和 `application` 不依赖 Qt、HTTP、OCR、OSC 或 Windows API，界面层也不直接选择具体翻译适配器。

## 文档

- 界面和功能操作：[使用说明.md](使用说明.md)
- 本轮 UI 与 OCR 悬浮交互方案：[UI与OCR悬浮交互重构方案.md](UI与OCR悬浮交互重构方案.md)
- 界面与架构深化记录：[VRCTranslate界面与架构深化方案.md](VRCTranslate界面与架构深化方案.md)
