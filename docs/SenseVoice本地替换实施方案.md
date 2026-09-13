# SenseVoiceSmall INT8 本地替换实施方案

日期：2026-09-13。前置档案：《语音识别方案选型》《多语言全链路质量与性能测试报告》。

## 0. 决策记录

- **全面替换**：V2 语音识别从 Whisper Base Q5_1 整体切换为 SenseVoiceSmall INT8（本地）。
- **云 API 暂缓**：硅基流动等云识别在本地主线完成并验证前不实施。
- **GPU 分阶段**：先 CPU（INT8 已实测 174~207 ms/句），DirectML 作为后续增强（VRChat 占 CPU，GPU 卸载最终要做）。
- **实现路线已定：A（Sherpa.Onnx C# 绑定）**。两条路线最终产品功能效果相同（同模型、同引擎），A 另附说话人分离等现成组件且工程量与风险更低；原路线 B（onnxruntime 自研管线）**已否决**，不再作为备选——此前保留 B 的唯一理由是 NuGet 不可达，网络恢复即无存在价值。
- Whisper 相关代码与依赖随替换移除（whisper.net 运行时、ggml 模型、多平台 runtimes 剔除规则）。

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
3. **换人切分**：能量分段（现有 SpeechSegmenter）只按停顿断句，同一句里两个人先后说话会粘连；引入分段模型（pyannote segmentation ONNX，约 6 MB）在音频上标出"换人点"，把一句话切成两句再分别识别。

**组件来源**（路线 A 内置）：sherpa-onnx 的 `OfflineSpeakerDiarization` = pyannote 分割 ONNX + 3D-Speaker 声纹嵌入 ONNX（合计约 30~40 MB）+ 聚类；也可只用其嵌入模型 + 自研在线登记（更贴合实时流式，避免整段离线处理）。

**产品呈现**：字幕行带说话人标签（"说话人A：…"）；检测到换人自动换行/换色。局限与对策：
- 不是 VRChat 玩家名——游戏不向外部暴露"谁在说话"；后续可做"标签↔玩家名"手动映射。
- 同性别相近声线偶有混淆：阈值调优 + 声纹表时效衰减（长时间不说话的说话人淡出）。
- 嵌入提取耗时可忽略（每句几十 ms，CPU）。

## 5. 阶段计划

| 阶段 | 内容 | 交付 |
|---|---|---|
| **1. CPU 全量替换**（本次范围） | SenseVoice INT8 CPU 识别器、模型内置与导入脚本、删除 Whisper、UI/文案/测试、发包回归 | 新手测包 + 验收数据复测 |
| 2. 说话人区分 | 声纹嵌入 + 在线登记 + 换人切分 + 字幕标签 | 字幕带说话人标签 |
| 3. GPU 卸载（可选） | DirectML EP（onnxruntime-directml，N/A 卡通用，约 +35 MB），CPU 自动回退 | VRChat 同开的占用对比数据 |
| 暂缓 | 硅基流动云识别、Whisper small+Vulkan 扩展语言 | 等阶段 1~2 验收后再评估 |

## 6. 风险与对策

| 风险 | 对策 |
|---|---|
| NuGet/模型下载网络不通（当前 DNS 故障） | 模型走 hf-mirror（已验证可达）；`Sherpa.Onnx` 包等 NuGet 恢复，期间先实施模型下载/打包/UI 等路线无关部分 |
| 包体积增至约 430 MB | 发布仍走压缩分发（zip/安装器）减半下载体积；体积换质量为既定决策 |
| 内存增量 ~330 MiB | 与 V1 实测一致，现代游戏机可接受；识别会话关闭即释放 |
| SenseVoice 输出带/不带标点差异 | 使用 ITN 开关产出带标点文本，便于阅读与翻译断句 |
| 说话人标签跨会话不稳定 | 标签仅会话内有效，不做持久化（声纹随人/设备变化） |
