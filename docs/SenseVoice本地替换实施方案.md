# SenseVoiceSmall INT8 本地替换实施方案

日期：2026-09-13。前置档案：《语音识别方案选型》《多语言全链路质量与性能测试报告》。

> **状态更新（阶段 1 已实施）**：路线 A 落地——新增 `SenseVoiceSpeechRecognizer`（sherpa-onnx 1.13.8 + win-x64 原生运行库，CPU）；模型契约由单 ggml 文件改为多文件目录，内置路径 `Models\sensevoice\`（`model.int8.onnx` + `tokens.txt`），按需下载回退到 `%LOCALAPPDATA%\VRCTranslate\v2\models\sensevoice`；`Import-BundledSpeechModel.ps1` 改为从 hf-mirror 下载并按固定 SHA-256 校验；Whisper.net 依赖、ggml 模型与多平台 runtimes 剔除规则已删除；语音页文案、分层测试与验证脚本同步更新。会话启动即预热模型，首次加载约 1.5 s，其后单句约 90~200 ms（脚本自带短句样本，与 V1 实测一致）。发布包实测 422.5 MB。说话人区分（阶段 2）与 DirectML（阶段 3）尚未开始。

## 0. 决策记录

- **全面替换**：V2 语音识别从 Whisper Base Q5_1 整体切换为 SenseVoiceSmall INT8（本地）。
- **云 API 暂缓**：硅基流动等云识别在本地主线完成并验证前不实施。
- **GPU 分阶段**：先 CPU（INT8 已实测 174~207 ms/句），DirectML 作为后续增强（VRChat 占 CPU，GPU 卸载最终要做）。
- **实现路线已定：A（Sherpa.Onnx C# 绑定）**。两条路线最终产品功能效果相同（同模型、同引擎），A 另附说话人分离等现成组件且工程量与风险更低；原路线 B（onnxruntime 自研管线）**已否决**，不再作为备选——此前保留 B 的唯一理由是 NuGet 不可达，网络恢复即无存在价值。
- Whisper 相关代码与依赖随替换移除（whisper.net 运行时、ggml 模型、多平台 runtimes 剔除规则）。
- **便携式数据目录**：设置、声纹库与按需下载的模型全部落在程序目录的 `data/` 下，不写用户配置目录；仅当安装位置不可写（Program Files、只读共享）时回退到 `%LOCALAPPDATA%\VRCTranslate`，并在界面说明原因。

## 1. 目标与验收

| 项 | 指标 |
|---|---|
| 识别质量（干净语音） | 中 CER ≤6%、英 WER ≤12%、日 CER ≤7%、韩 CER ≤8%（对标 V1 实测） |
| 单句时延 | P50 ≤250 ms（本机 CPU，含分段后推理） |
| 实时性 | RTF ≤0.05（INT8 实测 0.021~0.028） |
| 内存增量 | ≤400 MiB（实测 324~356 MiB） |
| 包体积 | 234 MB → 约 430 MB（模型 +255 MB、净增约 +196 MB） |
| 语言 | 中（含粤语口音鲁棒）、英、日、韩；自动语种检测（LID）与指定语言两种模式 |
| 回归 | 六页面 UI、验证脚本、发布冒烟全绿；自身语音（固定中文）与字幕（auto）两条链路 |

## 2. 架构改动（按分层）

| 层 | 改动 |
|---|---|
| Core | `LocalSpeechLanguages` 保持 zh/en/ja/ko（+内部支持 yue）；模型契约从"单 ggml 文件"改为"多文件模型目录"（model.int8.onnx + tokens.txt）；状态机复用 |
| Application | 会话/分段接口不变；`LocalSpeechService` 组合新识别器 |
| Infrastructure | 新增 `SenseVoiceSpeechRecognizer : ILocalSpeechRecognizer`；`LocalSpeechModelManager` 改管 ONNX 模型目录（内置探测/在线下载回退逻辑复用）；删除 Whisper.net 依赖 |
| Desktop | 语音页文案、管理模型对话框、内置状态展示更新；会话宿主（VoiceSessionHost）不动 |

**便携式数据目录**：`PortableStorage`（Infrastructure）统一解析数据目录——默认是程序目录下的 `data\`，不可写时回退 `%LOCALAPPDATA%\VRCTranslate`，`VRC_TRANSLATE_DATA_DIR` 仍可覆盖（UI 冒烟测试用它隔离数据）。设置、启动崩溃日志、按需下载的模型与声纹库都走这一处；发布时剔除 `data/`，避免把本机设置与密钥打进分发包。

## 3. 实现路线（已确定：A）

**A：Sherpa.Onnx C# 绑定**
- NuGet：`Sherpa.Onnx`（托管 API）+ 对应 win-x64 原生运行库；自带 fbank 特征提取、CTC 解码、ITN/标点开关、**离线说话人分离组件**（阶段 2 直接用）。
- CPU/GPU 切换：识别器配置的 `Provider` 字段（`cpu` / `directml` / `cuda`），GPU 不可用自动回退 CPU；注意 NuGet 默认包为 CPU 原生构建，DirectML 增强需替换为官方 DirectML 构建产物（阶段 3 处理）。
- 前置条件：NuGet 网络恢复（当前 api.nuget.org DNS 不通）；模型从 hf-mirror/ModelScope 下载 `sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17`（int8 约 230 MB + tokens）。

（原路线 B——Microsoft.ML.OnnxRuntime 自研特征提取与解码——已否决，理由见决策记录。）

**模型分发**：沿用内置模式——`assets/models/speech/`（gitignore）放 ONNX 模型，`Import-BundledSpeechModel.ps1` 扩展为下载+校验（hf-mirror 优先），csproj 打包进 `Models/`，发布剔除规则同步调整（移除 whisper 的 runtimes 剔除，保留其余瘦身）。

## 4. 人声分离（说话人区分）设计

**问题本质**：VRChat 把所有玩家语音混音后经系统输出，外部程序只能拿到单声道混音，拿不到分轨。想知道"这句是另一个人说的"，只能在混音上做**说话人区分（diarization）**。

**原理**（三步，全部本地、无云服务）：
1. **声纹嵌入**：对每个已分段的句子提取"说话人嵌入向量"（几十维声纹特征，与说话内容无关、与声道绑定）。
2. **在线聚类登记**：维护"已登记说话人表"；新句嵌入与表内各说话人算余弦相似度，高于阈值 → 归为同一人（沿用标签）；全部低于 → 登记"说话人B/C…"。
3. **换人切分**：能量分段（现有 SpeechSegmenter）只按停顿断句，同一句里两个人先后说话会粘连；引入分段模型（pyannote segmentation ONNX，int8 实测 1.5 MB）在音频上标出"换人点"，把一句话切成两句再分别识别。

**组件来源**（路线 A 自带，已核对 1.13.8 托管绑定）：`SpeakerEmbeddingExtractor`（`CreateStream` → `AcceptWaveform` → `Compute`）负责声纹嵌入；`SpeakerEmbeddingManager` 自带 `Add` / `Search` / `Verify` 登记表；`OfflineSpeakerDiarization`（pyannote 分割 + 嵌入 + 聚类）负责换人切分；原生 dll 内相关符号齐全，不需要换包。登记表因需要质心、多样本与持久化而自研，引擎只用嵌入与分割两部分。模型实测均可经 hf-mirror 获取，且比原估更小；两者随主包发布，开箱即用：

| 模型 | 文件 | 体积 | 用途 |
|---|---|---|---|
| pyannote segmentation 3.0 | `model.int8.onnx` | 1.5 MB | 换人点检测 |
| 3D-Speaker CAM++（zh+en） | `3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx` | 28.3 MB | 声纹嵌入 |

执行细节（数据格式、阈值、界面、标定与验收）见《说话人区分实施方案》。

**产品呈现**：字幕行带说话人标签（"说话人A：…"）；检测到换人自动换行/换色。局限与对策：
- 不是 VRChat 玩家名——游戏不向外部暴露"谁在说话"；由用户手动命名一次，之后靠声纹自动沿用（见 §4.1）。
- 同性别相近声线偶有混淆：阈值调优 + 声纹表时效衰减（长时间不说话的说话人淡出）。
- 嵌入提取耗时可忽略（每句几十 ms，CPU）；换人切分只对「疑似换人」的句子按需执行，不逐句跑分割。

### 4.1 标签持久化与人工命名

**结论：标签不止于「说话人A/B/C」——声纹库落盘、由用户命名，并跨会话沿用。** 早期「标签仅会话内有效、不做持久化」的结论作废。要点：

- **命名即入库**：未命名的说话人只在当前会话有效、绝不落盘，也不设「自动记住」开关；用户把它命名成玩家名（「小明」）时才写入程序目录下的 `data\v2-speakers.json`——库里的每一条都是用户亲自命名过的，且随时可删。
- **双阈值**：会话内聚类阈值宽松（同一次会话的声学条件一致），跨会话重认阈值从严——声音经 VRChat 空间音频、距离衰减、设备差异后已经变了，**宁可漏认不可错认**：错认会把别人的话挂到「小明」名下。
- **质心 + 多样本**：每个说话人保存归一化质心与最多 8 条样本向量，匹配取最大余弦相似度；命中后用 EMA 缓慢更新质心，避免一次误匹配污染声纹。
- **可纠正**：重命名、合并（A 并入 B）、忘记（删单条）、清空声纹库，以及字幕行的「这不是小明」逆向纠错。
- **隐私**：声纹是生物特征，只存在软件目录内（便携式，不写用户配置目录）、不上传、可一键清空。

## 5. 阶段计划

| 阶段 | 内容 | 交付 |
|---|---|---|
| **1. CPU 全量替换**（本次范围） | SenseVoice INT8 CPU 识别器、模型内置与导入脚本、删除 Whisper、UI/文案/测试、发包回归 | 新手测包 + 验收数据复测 |
| 2. 说话人区分 | 声纹嵌入 + 在线登记 + 换人切分 + 字幕标签 + 声纹库与手动命名 | 字幕带说话人标签，标签可命名并跨会话沿用 |
| 3. GPU 卸载（可选） | DirectML EP（onnxruntime-directml，N/A 卡通用，约 +35 MB），CPU 自动回退 | VRChat 同开的占用对比数据 |
| 暂缓 | 硅基流动云识别、Whisper small+Vulkan 扩展语言 | 等阶段 1~2 验收后再评估 |

## 6. 风险与对策

| 风险 | 对策 |
|---|---|
| NuGet/模型下载网络不通（当前 DNS 故障） | 模型走 hf-mirror（已验证可达）；`Sherpa.Onnx` 包等 NuGet 恢复，期间先实施模型下载/打包/UI 等路线无关部分 |
| 包体积增至约 430 MB | 发布仍走压缩分发（zip/安装器）减半下载体积；体积换质量为既定决策 |
| 内存增量 ~330 MiB | 与 V1 实测一致，现代游戏机可接受；识别会话关闭即释放 |
| SenseVoice 输出带/不带标点差异 | 使用 ITN 开关产出带标点文本，便于阅读与翻译断句 |
| 说话人标签跨会话不稳定（VRChat 空间音频/距离/设备导致声纹漂移） | 双阈值：会话内聚类宽松、跨会话重认从严，宁漏认不错认；认不出退回「说话人X」由用户再命名；误匹配用重命名/合并/「这不是小明」纠正 |
| 同一人因距离或位置变化被判成两人 | 每句多样本入库覆盖不同距离；UI 提供合并；声纹库按 `lastSeenAt` 标记久未出现并在界面淡出 |
| 声纹库属生物特征数据 | 仅存软件目录 `data\v2-speakers.json`（便携式）；未命名说话人不落盘；提供「清空声纹库」；不上传任何服务器 |
