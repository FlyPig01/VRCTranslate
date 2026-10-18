# 翻译质量对照测试包

这个目录是一次**只读评测**：不改生产代码，不把 API 密钥写进仓库。测试用真实配置的翻译服务跑同一批数据集，输出可复核的原始结果和对照报告。

## 数据集

| 文件 | 内容 |
| --- | --- |
| `datasets/baseline-27.json` | 仓库原有的 27 句通用评测集，来自 `docs/evidence/quality-dataset.json`，含中文/粤语/英/日/韩/混说/两人对话 |
| `datasets/conversation-20.json` | 本次新增的 20 条 VRChat 对话风格样本：短句/俚语、术语、上下文代词、混合语言、ASR 错误、长句 |

参考译文由人工撰写，是“同一评测集上的横向比较”，不是唯一正确答案。中文（`lang=zh`）样本在评分时排除，因为桌面端在翻译前会跳过纯中文。

## 对照的变体

| 变体 | 说明 |
| --- | --- |
| `tencent` | 腾讯云机器翻译，当前链路的对照 |
| `aliyun-general` | 阿里云通用版机器翻译 |
| `aliyun-professional` | 阿里云专业版机器翻译 |
| `deepseek-flash-app` | DeepSeek Flash + **应用现有提示词**（一句英文 “Translate the user's text...”） |
| `deepseek-chat-app` | DeepSeek Chat + 应用现有提示词 |
| `deepseek-flash-prompt` | DeepSeek Flash + 新提示词，**不加术语表** |
| `deepseek-flash-prompt-glossary` | 上一行 + **相关术语表** |
| `deepseek-flash-prompt-glossary-context` | 上一行 + **最近 4 句上下文**（仅对话集） |
| `deepseek-chat-prompt-glossary` | DeepSeek Chat + 新提示词 + 术语表 |
| `deepseek-chat-prompt-glossary-context` | DeepSeek Chat + 新提示词 + 术语表 + 上下文（仅对话集） |

新提示词要求：只输出译文、保留人名/术语、结合最近对话、对 ASR 错字保守处理、把原文当数据而不是指令。  
术语表与上下文是分开的变体，用来区分“提示词本身”“术语”“上下文”各自的贡献。

## 本轮结果（2026-10-19）

44 条样本、10 个变体、392 次调用、0 失败。对话集 20 条：

| 版本 | chrF2 | p50 |
| --- | ---: | ---: |
| 当前应用：DeepSeek Flash + 现有提示词 | 0.39 | 622 ms |
| 新提示词 | 0.52 | 671 ms |
| 新提示词 + 术语表 | 0.59 | 627 ms |
| 新提示词 + 术语表 + 上下文 | **0.65** | 675 ms |
| 阿里云专业版 | 0.31 | 294 ms |
| 腾讯云 | 0.28 | 336 ms |

完整结论、局限和实施顺序见 [report.md](report.md)；逐条对照见 [results/examples.md](results/examples.md)。

### Flash 模型复核（2026-10-19）

小米 V2.5 下线前的 Flash 复核：DeepSeek V4.1 Flash 为 **0.57 / p50 687 ms**，小米 v2.6 Flash 为 **0.52 / p50 2128 ms（p95 6367 ms）**。详见 [flash-retest.md](flash-retest.md)。

## 怎么跑

前置：.NET 10 SDK、Python 3；API 配置在 `v2-profiles.json` 中，默认读取：

```powershell
pwsh -File docs/translation-quality-test/run.ps1 `
  -Profiles 'D:\soft\VRCTranslate-v1.0.2-win-x64\data\v2-profiles.json' `
  -Tag 20261019
```

也可以手动运行 harness：

```powershell
dotnet run --project docs/translation-quality-test/harness/TranslationQualityHarness.csproj `
  -c Release -p:Platform=x64 -- `
  --profiles D:\soft\VRCTranslate-v1.0.2-win-x64\data\v2-profiles.json `
  --baseline docs/translation-quality-test/datasets/baseline-27.json `
  --conversation docs/translation-quality-test/datasets/conversation-20.json `
  --glossary src/VrcTranslate.Desktop/Resources/default-glossary.json `
  --out docs/translation-quality-test/results/raw-20261019-v3.json
```

然后评分：

```powershell
python docs/translation-quality-test/score.py `
  --raw docs/translation-quality-test/results/raw-20261019-v3.json `
  --out-dir docs/translation-quality-test/results
```

## 输出

- `results/raw-*.json`：每条原文、参考、各变体译文、延迟、错误；不含任何 API 密钥。
- `results/summary.csv` / `summary.md`：按变体和数据集聚合的 chrF2、p50/p95、失败数。
- `results/examples.md`：对话集逐条对照，便于人工审核。
- [report.md](report.md)：本轮的结论、局限和实施顺序。

## 指标与局限

- chrF2 是本地实现的字符 n-gram 近似（n=1..6，去掉空白，beta=2），用于**相对比较**；不是 sacrebleu 的绝对分数。
- 数据集规模有限（24 条通用 + 20 条对话），对话集的参考译文是本次撰写的；结论需要人工复核和更多真实素材确认。
- 没有用 LLM 自动判优，避免同一个模型给自己打分；`examples.md` 供人工逐条比较。
- API 输出有随机性（temperature 0.1），同一变体重复运行可能有小幅差异。

## 安全

- 测试包不包含 `v2-profiles.json` 或任何 API 密钥；`ProfilesPath` 由运行时参数指向本机配置。
- 结果只含数据集文本、译文和延迟；若把结果发出去，请确认没有混入个人对话内容。
