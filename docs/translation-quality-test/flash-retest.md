# 2026-10-19 Flash 模型复核

小米 MiMo V2.5 系列将于 **2026-10-21 10:00（UTC+8）** 下线；DeepSeek 的 `deepseek-flash` 当前由 **DeepSeek-V4.1-Flash-1017** 提供服务。按“只测 Flash”的要求，用同一套 44 条样本复核；生产提示词和术语表打开，**不启用上下文**。

| 模型 | n | chrF2 | p50 | p95 |
| --- | ---: | ---: | ---: | ---: |
| `deepseek-flash`（V4.1-Flash-1017） | 44 | **0.57** | **687 ms** | **914 ms** |
| `mimo-v2.6-flash` | 44 | 0.52 | 2128 ms | 6367 ms |

分语种 chrF2：

| 模型 | 英语 | 日语 | 韩语 | 粤语 | 混说 |
| --- | ---: | ---: | ---: | ---: | ---: |
| `deepseek-flash` | **0.55** | 0.43 | **0.55** | **0.76** | **0.64** |
| `mimo-v2.6-flash` | 0.49 | **0.46** | 0.43 | 0.66 | 0.61 |

## 结论

- 两者都能正常调用、0 失败。
- V2.6 Flash 质量低于 DeepSeek V4.1 Flash，p50 约 3 倍、p95 约 6.4 秒；日语只在 5 条样本上高 0.03，不足以抵消整体差距。
- 引导页已改为“小米 v2.6 Flash：日语略好；整体不如 DeepSeek，延迟高；不建议默认”。
- 代码默认、旧档案和旧路由迁到 `mimo-v2.6-flash`；旧 Pro 迁到 `mimo-v2.6-pro`。

## 口径

- 44 条 = 24 条通用（排除纯中文）+ 20 条 VRChat 对话。
- 指标是本地 chrF2 近似；延迟只测 provider 调用，不含 VAD/ASR/UI。
- 原始结果：[flash-retest-20261019.json](results/flash-retest-20261019.json)
- 评分：[summary.md](results/flash-retest/summary.md) / [examples.md](results/flash-retest/examples.md)
- 与[主报告](report.md)的旧矩阵口径不同：主报告是原型提示词/多模型矩阵，本页是生产提示词+术语表的 Flash 单轮复核。
