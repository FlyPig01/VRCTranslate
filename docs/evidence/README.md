# 评测原始数据（evidence）

这里是《[实验记录-识别与翻译.md](../实验记录-识别与翻译.md)》用到的**原始记录**，入库以便复核与 diff。
生成脚本在 `artifacts/`（该目录不入库，含密钥与 450 MB 手测包）。

| 文件 | 内容 | 行数 | 字段 |
| --- | --- | ---: | --- |
| `quality-dataset.json` | 27 条评测集：原文 + 人工中文参考译文 + 分类 | 27 条 | `id / lang / source / gold / category` |
| `xlate-auto.jsonl` | 实验 A：中文照常送翻译 | 540 | `round / provider / item / ms / output / error` |
| `xlate-skipzh.jsonl` | 实验 B：中文跳过（`skipped=true` 表示未调用服务，直接复用原文） | 540 | 同上 + `skipped` |
| `xlate-deepseek-v4pro.jsonl` | `deepseek-v4-pro` 模型（对照 `deepseek-flash`） | 108 | 同上 |
| `xlate-mimo-v25pro.jsonl` | `mimo-v2.5-pro` 模型（对照 `mimo-v2.5`） | 108 | 同上 |
| `asr-matrix.jsonl` | 识别层：10 段音频 × 5 语言模式 × 10 次 | 50 | `file / mode / detected / text / ms_min / ms_median / ms_p90 / ms_max` |

## 复现步骤

```powershell
# 1. 识别层（约 6 分钟，需要 artifacts\manual-test 手测包里的模型）
pwsh -NoProfile -File artifacts\bench-asr.ps1 `
  -Files zh,ja,ko,en,mix_zh_ja_seq,mix_en_ko_seq,mix_zh_ja_over,mix_en_ko_over,mix_zh_ja_half,mix_multi3 `
  -Modes auto,zh,ja,ko,en -Samples 10 -Json artifacts\asr-matrix.json

# 2. 合成混合音频（需要 artifacts\probe\{zh,ja,ko,en}.wav 四段真人语音）
python artifacts\mix_audio.py

# 3. 翻译层 A / B（各约 8 分钟，需要 data\v2-profiles.json 里有五家真实密钥）
pwsh -NoProfile -File artifacts\bench-translate.ps1 -Modes auto -Rounds 4 -Out artifacts\xlate-auto.json
pwsh -NoProfile -File artifacts\bench-translate.ps1 -Modes auto -Rounds 4 -SkipChinese true -Out artifacts\xlate-skipzh.json

# 3b. 两个 pro 模型（同一评测集、同一轮数，可与标准版逐条对比）
pwsh -NoProfile -File artifacts\bench-translate.ps1 -Modes auto -Rounds 4 -Provider deepseek -Model deepseek-v4-pro -Out artifacts\xlate-v4pro.json
pwsh -NoProfile -File artifacts\bench-translate.ps1 -Modes auto -Rounds 4 -Provider xiaomi -Model mimo-v2.5-pro -Out artifacts\xlate-mimopro.json

# 3c. 计费换算所需的 token 实测（10 个采样点）
pwsh -NoProfile -File artifacts\measure-tokens.ps1 -Profile deepseek -Model deepseek-flash   # 或 xiaomi / mimo-v2.5-pro

# 4. 打分（chrF2 / BLEU-4，与 sacrebleu 2.6 交叉验证）与显著性检验
python artifacts\score-translation.py   # 生成 artifacts\quality-report.md
python artifacts\significance.py       # Wilcoxon 配对符号秩
```

## 注意事项

- 翻译层的密钥来自 `artifacts/manual-test/data/v2-profiles.json`，**不要把该文件复制进仓库**。
- `ms` 是单次 HTTP 调用墙钟，**不含** VAD 静音等待（600 ms）与 OSC 发送；端到端估算见报告 §3。
- 识别层用的是内置的 SenseVoice INT8 模型（228 MB），需要手测包存在才能复现。

