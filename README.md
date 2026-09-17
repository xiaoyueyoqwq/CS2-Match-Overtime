# CS2 Match Overtime

竞技模式打到常规赛平局（`mp_maxrounds 24` 时为 12-12）时，先暂停比赛，交给 **Vote Improver** 做一次只计真人的是/否投票，再决定走原版加时还是平局结束。

本插件不绘制投票 HUD，也不监听 `vote`。选民规则与 Vote Improver 相同：排除 HLTV、引擎 bot、以及 `botidentity:api` 托管的 bot。玩家需按 Vote Improver README 绑定 F1/F2。

## 行为

1. 仅 `game_type 0` / `game_mode 1`。其它模式不拦截。
2. 赛点（12-11 / 11-12）且场上有真人时，运行时打开 `mp_overtime_enable`，并把 `mp_overtime_startmoney` 设为 12500。不写磁盘 cfg。
3. 平局回合结束：`mp_pause_match`，向 `voteimprover:api` 发起投票。F1 进入加时，F2 / 超时 / 不够法定人数则平局。
4. 通过后 `mp_unpause_match`，之后的加时（含 15-15）交给引擎，不再投票。
5. 零真人时不打开加时，保持现网的 12-12 平局。
6. 拿不到 `voteimprover:api` 时禁用拦截，同样保持现网平局。

## 依赖

- CounterStrikeSharp API 1.0.371，`net10.0`
- Vote Improver **2.1.0+**（提供 `voteimprover:api` 和 `VoteImproverApi.dll`）
- BotIdentity（`botidentity:api`）：用于识别托管 bot；缺失时只能靠引擎 `IsBot`

`VoteImproverApi.dll` 只放在 `plugins/VoteImprover/`。本插件在 `OnAllPluginsLoaded` 解析 capability，不依赖目录字母序。不要把 Api DLL 再复制一份到 `MatchOvertime/`。

## 构建与部署

```bash
dotnet build -c Release
```

把 `bin/Release/net10.0/MatchOvertime.dll` 放到：

`game/csgo/addons/counterstrikesharp/plugins/MatchOvertime/`

同时把 Vote Improver 升到 2.1.0（`plugins/VoteImprover/`，含 `VoteImproverApi.dll`），并卸掉旧的 `plugins/BotVoteFix/`，避免两套 `vote` 监听并存。空服按维护手册第 8 节备份后再 reload。有玩家在线时不要换 DLL、不要改 CVar。

配置在首次加载后生成：

`addons/counterstrikesharp/configs/plugins/MatchOvertime/MatchOvertime.json`

| 键 | 默认 | 含义 |
|---|---|---|
| `Enabled` | true | 总开关 |
| `OvertimeStartMoney` | 12500 | 加时每半场起手（EWC） |
| `VoteDurationSeconds` | 0 | 0 = 跟 Vote Improver / `sv_vote_timer_duration` |
| `VoteDisplayString` | `#SFUI_vote` | VoteStart 的 Panorama token |
| `VotePassedString` | `#SFUI_vote_passed` | 通过时的 token |
| `VoteDisplayDetails` | `是否进入加时赛？` | HUD 第二行 |

## 回滚

删除 `plugins/VoteImprover/` 和 `plugins/MatchOvertime/`，把现网原来的 `plugins/BotVoteFix/` 2.0.2 放回去，reload。运行时若已改过 `mp_overtime_enable`，reload 后本插件会在换图 / 新局时写回 0。
