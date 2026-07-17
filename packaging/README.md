# one-folder 打包说明

VRCTranslate 正式发布只使用 PyInstaller one-folder，不制作 one-file。one-file 会在运行时解压依赖到系统临时目录，通常会占用 C 盘。

## 安装打包依赖

```powershell
python -m pip install -e ".[build]"
```

## 构建

发布版包含在线翻译、测试回显和本地 OCR：

```powershell
$env:PYINSTALLER_CONFIG_DIR = "$PWD\data\cache\pyinstaller"
python -m PyInstaller --clean --noconfirm VRCTranslate.spec
Remove-Item Env:PYINSTALLER_CONFIG_DIR
```

输出目录为 `dist\VRCTranslate\`。

发布前应在 D/E 盘的干净目录中解压并验证：

- exe 同级存在 `_internal`、`data` 和使用说明。
- 配置、日志和缓存只写到 exe 同级 `data`。
- 不从压缩包内直接运行，也不放到 `Program Files` 等不可写目录。
