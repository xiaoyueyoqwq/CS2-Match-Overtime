# CS2 Match Overtime

When a competitive match reaches a regulation tie (`12-12` at `mp_maxrounds 24`), this plugin pauses the game and asks **[Vote Improver](https://github.com/xiaoyueyoqwq/CS2-Vote-Improver)** for a humans-only yes/no vote. Pass continues vanilla overtime; fail ends the match as a draw.

This plugin does not draw the vote HUD and does not listen to `vote`. The electorate matches Vote Improver: HLTV, engine bots, and `botidentity:api` managed bots are excluded.

> [!IMPORTANT]
> The plugin counts the client commands `vote option1` (yes) and `vote option2` (no). If F1/F2 are bound to something else, the HUD appears but the ballot never arrives. Run this in the CS2 console, or add it to autoexec:
>
> ```
> bind F1 "vote option1"
> bind F2 "vote option2"
> ```
>
> Source bind syntax is `bind <key> "<command>"`. `bind voteoption1 F1` reverses the arguments, and there is no `voteoption1` command. To test without a keybind, type `vote option1` or `vote option2` in the console while a vote is open.

## Behaviour

1. Competitive only (`game_type 0` / `game_mode 1`). Other modes are not intercepted.
2. When at least one human is present, enable `mp_overtime_enable` at match start (runtime only, not disk cfg) and set `mp_overtime_startmoney` to 12500. Enabling it only at match point is too late: CS2 still ends a regulation tie as a draw.
3. When the tying round ends, `mp_pause_match` and start a Vote Improver ballot immediately. The plugin stretches `mp_halftime_duration`, `mp_team_intro_time`, and `mp_round_restart_delay` for the vote window and sets `mp_halftime_pausetimer 1`, so the overtime half does not start mid-ballot. Humans also receive a chat line (`VoteStartNotify`) because the vote HUD first line cannot say overtime. F1 continues overtime. F2, timeout, or a failed quorum clears `m_nOvertimePlaying` and ends the match as a draw.
4. On pass: `mp_unpause_match`. Later overtime, including 15-15, is left to the engine; there is no second vote.
5. After the match ends (win panel or a new warmup), the plugin resets so the next match is not left in `OvertimeAccepted`.
6. With zero humans, overtime is not enabled and the match stays a vanilla 12-12 draw.
7. If `voteimprover:api` is missing, intercept is disabled and the match stays a vanilla draw.

## Requirements

- CounterStrikeSharp API 1.0.371, `net10.0`
- Vote Improver **2.1.1+** (`voteimprover:api` and `VoteImproverApi.dll`)
- BotIdentity (`botidentity:api`) to identify managed bots; without it only the engine `IsBot` flag is used

`VoteImproverApi.dll` belongs in two places: `plugins/VoteImprover/` for Vote Improver itself, and CSS `shared/VoteImproverApi/VoteImproverApi.dll` for consumers, same layout as `BotIdentityApi`. Leaving it only next to Vote Improver makes CSS fail to load this plugin. Do not copy a second Api DLL into `MatchOvertime/`. Capability resolution happens in `OnAllPluginsLoaded`, so folder alphabetical order does not matter.

## Build and deploy

```bash
dotnet build -c Release
```

Copy `bin/Release/net10.0/MatchOvertime.dll` to:

`game/csgo/addons/counterstrikesharp/plugins/MatchOvertime/`

Also ship Vote Improver 2.1.1 (`plugins/VoteImprover/` including `VoteImproverApi.dll`), copy the same Api DLL to `shared/VoteImproverApi/`, and remove the old `plugins/BotVoteFix/` so two `vote` listeners are not loaded together. Back up, then reload on an empty server. A hot-load that fails with a missing Api leaves an UNREGISTERED slot that only a process restart clears. Do not replace DLLs or change CVars while players are connected. Existing `MatchOvertime.json` is not rewritten on upgrade; copy the new display and duration defaults if the file already exists.

The config is generated on first load:

`addons/counterstrikesharp/configs/plugins/MatchOvertime/MatchOvertime.json`

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | true | Master switch |
| `OvertimeStartMoney` | 12500 | Overtime half start money |
| `VoteDurationSeconds` | 20 | Ballot length in seconds. `0` follows Vote Improver / `sv_vote_timer_duration` |
| `VoteDisplayString` | `#SFUI_vote_restart_game` | VoteStart first line. Panorama only draws `#SFUI_vote_*` tokens; `#SFUI_Scoreboard_Overtime` and raw Chinese leave a blank HUD. First line stays 「重新开始比赛？」 |
| `VotePassedString` | `#SFUI_vote_passed_restart_game` | VotePass first line |
| `VoteStartNotify` | `[加时赛] 当前比分战平。请按 F1 或 F2 投票决定是否进入加时。` | Chat line sent to humans when the vote opens. `[加时赛]` and F1 are green, F2 is red. Empty string disables it |

## Rollback

Delete `plugins/VoteImprover/`, `plugins/MatchOvertime/`, and `shared/VoteImproverApi/`. Restore the previous `plugins/BotVoteFix/` 2.0.2, then reload or restart. If `mp_overtime_enable` was changed at runtime, this plugin writes it back to 0 on map change / new match after reload.
