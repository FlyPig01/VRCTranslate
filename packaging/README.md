# one-folder 打包说明

VRCTranslate 正式发布只使用 PyInstaller one-folder，不制作 one-file。one-file 会在运行时解压依赖到系统临时目录，通常会占用 C 盘。

## 安装打包依赖

```powershell
python -m pip install -e ".[build]"
```

## 构建

发布版包含在线翻译、测试回显、本地 OCR、本地语音组件管理及默认关闭的麦克风采集能力：

```powershell
$env:PYINSTALLER_CONFIG_DIR = "$PWD\data\cache\pyinstaller"
python -m PyInstaller --clean --noconfirm VRCTranslate.spec
Remove-Item Env:PYINSTALLER_CONFIG_DIR
```

输出目录为 `dist\VRCTranslate\`。

构建时不会把开发机 `data` 中的 OCR 或 SenseVoice 模型复制进发布目录。本地语音用户需在软件内主动下载约 246.3 MiB 的 SenseVoiceSmall INT8 与 sherpa-onnx 运行库；组件只写入 exe 同级 `data`。模型安装器会在内存中并行小样本测速 Hugging Face 官方源与国内镜像，自动选择较快来源并在失败时换源。

PyInstaller 的 `sounddevice` 标准 hook 会收集 Windows PortAudio DLL。构建后应在干净机器上确认 `_internal\_sounddevice_data\portaudio-binaries` 存在，并实际启用一次“自身语音自动翻译”验证麦克风能打开和释放；发布包仍不预置 SenseVoice 模型。

发布前应在 D/E 盘的干净目录中解压并验证：

- exe 同级存在 `_internal`、`data`、README、使用说明和 `THIRD_PARTY_NOTICES.md`；完整基准测试报告只保留在 GitHub 仓库。
- 配置、日志和缓存只写到 exe 同级 `data`。
- 发布目录没有预置 `data\models\speech`、`data\components\local-asr` 或开发机测试语料。
- 不从压缩包内直接运行，也不放到 `Program Files` 等不可写目录。
