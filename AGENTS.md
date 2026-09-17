# Project Notes

- 投票 GUI、选民、计票全部在 Vote Improver。本插件只暂停比赛、改运行时加时 CVar、按 `voteimprover:api` 回调解暂停或强制平局。
- 选民必须是真人：不要在这里再实现一份 `vote` 监听，也不要把 bot 算进“有没有人可以投票”。
- 不要把 `mp_overtime_enable` 写进 `server.cfg` 或 gamemode cfg。只在赛点临时打开，新局写回 0。
- 常规赛平局只投一次。加时里的 15-15 不要再暂停。
- `TerminateRound(RoundDraw)` 在引擎已经进入加时后是否真的结束比赛，必须空服打穿后再改失败路径。不要用 `mp_restartgame` 冒充平局。
- 有玩家在线时不 reload、不改 CVar。部署走 CS2 维护手册第 8 节。
- `VoteImproverApi.dll` 必须进 CSS `shared/VoteImproverApi/`。只放在 `plugins/VoteImprover/` 时，加载本插件会 `FileNotFoundException: VoteImproverApi`；失败后的 UNREGISTERED 槽只能进程重启清掉。
