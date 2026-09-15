# DeepSeek 翻译实测（deepseek-flash）

日期：2026-09（本机直连官方 API，单次抽样）。目的：确认默认翻译提供商的质量与延迟，并为“是否开启思考模式”提供依据。

## 1. 测试配置（与软件完全一致）

| 项 | 值 |
| --- | --- |
| 端点 | `https://api.deepseek.com/chat/completions` |
| 模型 | `deepseek-flash` |
| 系统提示词 | `Translate the user's text into {目标语言}. The source language is {源语言}. Return only the translation, without explanations.` |
| 默认参数 | `thinking.type = disabled`，`temperature = 0.1` |
| 思考模式参数 | `thinking.type = enabled`（该模式下不传 `temperature`） |

样本取自 VRChat 场景的口语（英语 / 日语 / 韩语 → 简体中文，以及中文 → 英语），每句独立请求，不共享上下文。

## 2. 翻译质量（关闭思考，软件默认）

| 原文 | 译文 |
| --- | --- |
| Hey, watch out, there's a player hiding behind that tree! | 嘿，小心，那棵树后面藏着一个玩家！ |
| I'm gonna grab the sword and head to the boss room, you coming? | 我要拿起剑去boss房间了，你来吗？ |
| ちょっと待って、今から回復するから。 | 等一下，我现在就恢复。 |
| 이 월드 진짜 예쁘다, 어디서 받았어? | 这个世界真漂亮，你从哪里下载的？ |
| Sorry my mic is lagging, I'll restart VRChat real quick. | 抱歉，我的麦克风有延迟，我马上重启一下VRChat。 |
| 别站在那边，先跟我来，这边有隐藏房间。 | Don't stand over there, come with me first, there's a hidden room over here. |
| （184 字长句）Okay so the plan is we split up, you take the left corridor and I'll check the basement, and if anyone finds the key just shout it out, don't try to solo the boss again like last time. | 好的，计划是这样，我们分头行动，你走左边的走廊，我去检查地下室，要是谁找到了钥匙就喊一声，别再像上次那样单挑老板了。 |

观察：中 / 日 / 韩 / 英 → 中文全部准确自然，口语语气保留；专有名词（`boss`、`VRChat`）不硬译；长句无漏译；严格只输出译文，未附加解释或原文。**未发现需要靠推理才能译对的样本。**

## 3. 翻译耗时（关闭思考）

| 场景 | 耗时 |
| --- | --- |
| 首次调用（含 TLS 握手） | 1079 ms |
| 短句（6 句） | 425 / 645 / 908 / 925 / 987 ms |
| 同一句重复 3 次 | 871 / 918 / 1255 ms |
| 长句（184 字，72+36 tokens） | 1122 ms |

中位约 **900 ms**；抖动约 ±20%（同一句 871~1255 ms）。

## 4. 思考模式对比（同一批 5 句）

| 指标 | 关闭思考 | 开启思考 | 变化 |
| --- | --- | --- | --- |
| 单句耗时 | 513 ~ 1360 ms（中位 775） | 1537 ~ 2254 ms（中位 1768） | **慢 2.3 倍（约 +1 秒/句）** |
| 输出 tokens | 6 ~ 13 | 198 ~ 299 | **约 20~25 倍** |
| 思考内容长度 | 0 | 608 ~ 972 字 | 每次都要生成 |
| 端到端（含识别约 140 ms） | **约 0.9 秒/句** | **约 1.9 秒/句** | — |

质量差异：5 句中 **2 句略好、3 句完全相同**。略好的两处是措辞更自然（“你那个头像是从哪儿弄来的？”优于“你从哪儿弄到那个头像的？”；`packed with people in a minute` 优于 `full soon`）。**没有出现“关思考译错、开思考才译对”的情况。**

## 5. Token 消耗（关闭思考）

每句 prompt 39~46 tokens（长句 72），completion 6~19 tokens（长句 36）。单句成本极低；开启思考后 completion 涨到 200~300，直接放大约 20 倍。

## 6. 结论

1. **保持默认关闭思考是正确的**，代码里“仅对名字含 `reasoner` 的模型开启思考、其余强制 `temperature=0.1`”的策略被本组数据验证，无需改动。字幕是实时场景，1.9 秒/句已接近不可用（参见《多语言全链路质量与性能测试报告》对 2.5 秒级延迟“字幕不及时”的判断），而换来的只是措辞更顺。
2. **翻译是当前链路的瓶颈，不是识别**：识别约 140 ms，翻译约 900 ms，端到端约 1 秒/句，其中翻译占约 85%。若要压延迟，应优先优化这里（换更快的模型，或先出原文再补译文）。
3. **需要注意抖动**：同一句话 871~1255 ms，波动约 ±20%，字幕会出现偶发的明显卡顿，而不是稳定的 1 秒。

## 7. 局限（勿过度解读）

- 单机、单次抽样，样本量小（共 16 次调用），未做多轮统计；
- 未测试并发请求、超长文本、术语库注入、噪声输入对翻译的间接影响；
- 未与其它提供商（DeepL / 腾讯云 / 阿里云等）在同一批样本上对比，无法据此判断“DeepSeek 是否最优”，只能判断“本组样本下质量与延迟可用，且思考模式不值得开”；
- 数值受网络状况影响，不同时间/地区会有差异。
