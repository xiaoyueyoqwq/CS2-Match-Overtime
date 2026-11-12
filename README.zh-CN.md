# CS2-Match-Overtime

[English](README.md) | 简体中文

当竞技比赛常规时间战平（`mp_maxrounds 24` 时的 `12-12`），本插件会暂停比赛，并请求 **[Vote Improver](https://github.com/xiaoyueyoqwq/CS2-Vote-Improver)** 发起一场仅限真人的 yes/no 投票。通过则进入原版加时；否决或超时则以平局结束比赛。

本插件不绘制投票 HUD，也不监听 `vote`。选民范围与 Vote Improver 一致：HLTV、引擎 Bot、以及 `botidentity:api` 托管 Bot 一律排除。

> [!IMPORTANT]
> 本插件统计的是客户端命令 `vote option1`（赞成）和 `vote option2`（反对）。如果 F1/F2 绑了别的东西，HUD 会出现但选票永远到不了。在 CS2 控制台执行以下命令，或写进 autoexec：
>
> ```
> bind F1 "vote option1"
> bind F2 "vote option2"
> ```
>
> Source 的绑定语法是 `bind <按键> "<命令>"`。写成 `bind voteoption1 F1` 是把参数弄反了，而且不存在 `voteoption1` 这个命令。不想绑键的话，投票开放期间直接在控制台输入 `vote option1` 或 `vote option2` 即可。

## 行为

1. 仅竞技模式（`game_type 0` / `game_mode 1`）。其他模式不拦截。
2. 至少有一名真人在场时，在开局就把 `mp_overtime_enable` 打开（仅运行时写入，不落盘 cfg），并把 `mp_overtime_startmoney` 设为 12500。等到赛点才开就太迟了：CS2 仍会把常规战平按平局结束。
3. 战平回合结束时，立即 `mp_pause_match` 并发起 Vote Improver 投票。插件会把 `mp_halftime_duration`、`mp_team_intro_time`、`mp_round_restart_delay` 拉长到覆盖投票窗口，并设 `mp_halftime_pausetimer 1`，避免加时半场在投票中途开始。真人还会收到一条聊天提示（`VoteStartNotify`），因为投票 HUD 第一行没法显示加时字样。F1 进入加时；F2、超时或人数未达 quorum 会清掉 `m_nOvertimePlaying` 并以平局结束比赛。
4. 通过时：`mp_unpause_match`。之后的加时（包括 15-15）完全交给引擎，不再发起第二次投票。
5. 比赛结束后（胜利面板或新一场热身），插件复位，避免下一场带着 `OvertimeAccepted` 状态。
6. 没有真人在场时不开加时，比赛按原版 12-12 平局收场。
7. `voteimprover:api` 缺失时拦截功能停用，比赛按原版平局收场。

## 运行要求

- CounterStrikeSharp API 1.0.371，`net10.0`
- Vote Improver **2.1.1+**（`voteimprover:api` 与 `VoteImproverApi.dll`）
- BotIdentity（`botidentity:api`），用于识别托管 Bot；缺失时退化到引擎 `IsBot` 标志

`VoteImproverApi.dll` 需要放在两处：`plugins/VoteImprover/`（给 Vote Improver 自身）和 CSS `shared/VoteImproverApi/VoteImproverApi.dll`（给消费方），布局与 `BotIdentityApi` 相同。只放在 Vote Improver 旁边会让 CSS 加载本插件失败。不要再往 `MatchOvertime/` 里多拷一份 Api DLL。capability 在 `OnAllPluginsLoaded` 解析，目录字母顺序无所谓。

## 构建与部署

```bash
dotnet build -c Release
```

把 `bin/Release/net10.0/MatchOvertime.dll` 拷到：

`game/csgo/addons/counterstrikesharp/plugins/MatchOvertime/`

同时部署 Vote Improver 2.1.1（`plugins/VoteImprover/`，含 `VoteImproverApi.dll`），把同一个 Api DLL 拷到 `shared/VoteImproverApi/`，并删掉旧的 `plugins/BotVoteFix/`，避免两个 `vote` 监听器并存。先备份，再在空服上重载。因缺 Api 导致的热载失败会留下 UNREGISTERED 槽位，只能进程重启清除。有玩家在线时不要替换 DLL 或改 CVar。已存在的 `MatchOvertime.json` 升级时不会被重写；老文件存在的话需要手动同步新的显示与时长默认值。

配置文件在首次加载时生成：

`addons/counterstrikesharp/configs/plugins/MatchOvertime/MatchOvertime.json`

| 键 | 默认值 | 含义 |
|---|---|---|
| `Enabled` | true | 总开关 |
| `OvertimeStartMoney` | 12500 | 加时半场起始金钱 |
| `VoteDurationSeconds` | 20 | 投票时长（秒）。`0` 表示跟随 Vote Improver / `sv_vote_timer_duration` |
| `VoteDisplayString` | `#SFUI_vote_restart_game` | VoteStart 第一行。Panorama 只画 `#SFUI_vote_*` token；`#SFUI_Scoreboard_Overtime` 和纯中文都会留下空白 HUD。第一行固定显示「重新开始比赛？」 |
| `VotePassedString` | `#SFUI_vote_passed_restart_game` | VotePass 第一行 |
| `VoteStartNotify` | `[加时赛] 当前比分战平。请按 F1 或 F2 投票决定是否进入加时。` | 投票开启时发给真人的聊天提示。`[加时赛]` 与 F1 为绿色，F2 为红色。空字符串关闭 |

## 许可证

[GNU Affero General Public License v3.0](LICENSE)
