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
2. On match point (12-11 / 11-12) with at least one human, enable `mp_overtime_enable` at runtime and set `mp_overtime_startmoney` to 12500. These CVars are not written to disk cfg.
3. When the tying round ends: `mp_pause_match`, then start a Vote Improver ballot. F1 continues to overtime; F2, timeout, or a failed quorum ends the match as a draw.
4. On pass: `mp_unpause_match`. Later overtime, including 15-15, is left to the engine; there is no second vote.
5. With zero humans, overtime is not enabled and the match stays a vanilla 12-12 draw.
6. If `voteimprover:api` is missing, intercept is disabled and the match stays a vanilla draw.

## Requirements

- CounterStrikeSharp API 1.0.371, `net10.0`
- Vote Improver **2.1.0+** (`voteimprover:api` and `VoteImproverApi.dll`)
- BotIdentity (`botidentity:api`) to identify managed bots; without it only the engine `IsBot` flag is used

`VoteImproverApi.dll` belongs in two places: `plugins/VoteImprover/` for Vote Improver itself, and CSS `shared/VoteImproverApi/VoteImproverApi.dll` for consumers, same layout as `BotIdentityApi`. Leaving it only next to Vote Improver makes CSS fail to load this plugin. Do not copy a second Api DLL into `MatchOvertime/`. Capability resolution happens in `OnAllPluginsLoaded`, so folder alphabetical order does not matter.

## Build and deploy

```bash
dotnet build -c Release
```

Copy `bin/Release/net10.0/MatchOvertime.dll` to:

`game/csgo/addons/counterstrikesharp/plugins/MatchOvertime/`

Also ship Vote Improver 2.1.0 (`plugins/VoteImprover/` including `VoteImproverApi.dll`), copy the same Api DLL to `shared/VoteImproverApi/`, and remove the old `plugins/BotVoteFix/` so two `vote` listeners are not loaded together. Back up, then reload on an empty server. A hot-load that fails with a missing Api leaves an UNREGISTERED slot that only a process restart clears. Do not replace DLLs or change CVars while players are connected.

The config is generated on first load:

`addons/counterstrikesharp/configs/plugins/MatchOvertime/MatchOvertime.json`

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | true | Master switch |
| `OvertimeStartMoney` | 12500 | Overtime half start money |
| `VoteDurationSeconds` | 0 | `0` follows Vote Improver / `sv_vote_timer_duration` |
| `VoteDisplayString` | `#SFUI_vote` | VoteStart Panorama token |
| `VotePassedString` | `#SFUI_vote_passed` | Pass token |
| `VoteDisplayDetails` | `是否进入加时赛？` | HUD second line |

## Rollback

Delete `plugins/VoteImprover/`, `plugins/MatchOvertime/`, and `shared/VoteImproverApi/`. Restore the previous `plugins/BotVoteFix/` 2.0.2, then reload or restart. If `mp_overtime_enable` was changed at runtime, this plugin writes it back to 0 on map change / new match after reload.
