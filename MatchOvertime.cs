using BotIdentityApi;
using VoteImproverApi;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Entities.Constants;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace MatchOvertime;

/// <summary>
/// At a regulation tie, pause and ask Vote Improver for a humans-only vote.
/// Pass continues vanilla overtime; fail forces a draw. This plugin does
/// not own the Panorama HUD or the <c>vote</c> command.
/// </summary>
[MinimumApiVersion(334)]
public sealed class MatchOvertime : BasePlugin, IPluginConfig<MatchOvertimeConfig>
{
    private enum Phase
    {
        Idle,
        OtArmed,
        Voting,
        OvertimeAccepted,
        DrawDecided,
    }

    private static readonly PluginCapability<IHumanVoteApi> VoteCapability = new("voteimprover:api");
    private static readonly PluginCapability<IBotIdentityApi> BotIdentityCapability = new("botidentity:api");

    private IHumanVoteApi? _voteApi;
    private IBotIdentityApi? _botIdentityApi;
    private Phase _phase = Phase.Idle;
    private bool _pausedByUs;
    private bool _interceptDisabled;
    private float? _savedRoundRestartDelay;
    private float? _savedHalftimeDuration;
    private float? _savedTeamIntroTime;
    private float? _savedWinPanelDisplayTime;
    private int? _savedHalftimePauseTimer;
    private float _voteStartedAt;

    public MatchOvertimeConfig Config { get; set; } = new();

    public override string ModuleName => "Match Overtime";
    public override string ModuleVersion => "1.0.13";
    public override string ModuleAuthor => "CS2-Match-Overtime";
    public override string ModuleDescription =>
        "Pauses a regulation tie and waits for a Vote Improver humans-only vote before vanilla overtime or a draw.";

    public void OnConfigParsed(MatchOvertimeConfig config)
    {
        config.OvertimeStartMoney = Math.Clamp(config.OvertimeStartMoney, 0, 16000);
        config.VoteDurationSeconds = Math.Clamp(config.VoteDurationSeconds, 0f, 300f);
        config.VoteDisplayString ??= "#SFUI_vote_restart_game";
        config.VotePassedString ??= "#SFUI_vote_passed_restart_game";
        config.VoteStartNotify ??= "[加时赛] 当前比分战平。请按 F1 或 F2 投票决定是否进入加时。";
        Config = config;
    }

    public override void Load(bool hotReload)
    {
        RegisterEventHandler<EventRoundEnd>(OnRoundEndPre, HookMode.Pre);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventRoundFreezeEnd>(OnRoundFreezeEnd);
        RegisterEventHandler<EventRoundAnnounceMatchStart>(OnMatchStart);
        RegisterEventHandler<EventBeginNewMatch>(OnBeginNewMatch);
        RegisterEventHandler<EventCsWinPanelRound>(OnCsWinPanelRound);
        RegisterEventHandler<EventCsWinPanelMatch>(OnCsWinPanelMatch);
        RegisterEventHandler<EventWarmupEnd>(OnWarmupEnd);
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);

        if (hotReload)
            ResolveApis();

        Logger.LogInformation("[MatchOT] Loading Match Overtime v{Version}", ModuleVersion);
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        ResolveApis();
    }

    public override void Unload(bool hotReload)
    {
        if (_phase == Phase.Voting)
            _voteApi?.CancelActiveVote("Match Overtime unload");
        UnpauseIfOurs();
        RestoreVoteWindow();
        RestoreOvertimeDisabled();
    }

    private void ResolveApis()
    {
        try { _voteApi = VoteCapability.Get(); }
        catch { _voteApi = null; }

        try { _botIdentityApi = BotIdentityCapability.Get(); }
        catch { _botIdentityApi = null; }

        _interceptDisabled = _voteApi == null;
        Logger.LogInformation(
            "[MatchOT] voteimprover:api {Vote}; botidentity:api {Identity}; intercept {Intercept}",
            _voteApi == null ? "missing" : "available",
            _botIdentityApi == null ? "missing" : "available",
            _interceptDisabled ? "disabled (12-12 stays a vanilla draw)" : "enabled");
    }

    private HookResult OnBeginNewMatch(EventBeginNewMatch @event, GameEventInfo info)
    {
        ResetMatch("begin_new_match", ignoreDuringArmedVote: true);
        EnsureOvertimeArmed("begin_new_match");
        Server.NextFrame(() => EnsureOvertimeArmed("begin_new_match+1frame"));
        return HookResult.Continue;
    }

    private HookResult OnMatchStart(EventRoundAnnounceMatchStart @event, GameEventInfo info)
    {
        ResetMatch("match_start", ignoreDuringArmedVote: true);
        EnsureOvertimeArmed("match_start");
        Server.NextFrame(() => EnsureOvertimeArmed("match_start+1frame"));
        return HookResult.Continue;
    }

    private void OnMapStart(string mapName) => ResetMatch("map_start");

    private void OnMapEnd() => ResetMatch("map_end");

    private HookResult OnCsWinPanelRound(EventCsWinPanelRound @event, GameEventInfo info)
    {
        if (@event.FinalEvent != 0)
            ResetMatch("win_panel_final");
        return HookResult.Continue;
    }

    private HookResult OnCsWinPanelMatch(EventCsWinPanelMatch @event, GameEventInfo info)
    {
        ResetMatch("win_panel_match");
        return HookResult.Continue;
    }

    private HookResult OnWarmupEnd(EventWarmupEnd @event, GameEventInfo info)
    {
        if (_phase is Phase.OvertimeAccepted or Phase.DrawDecided or Phase.Voting)
            ResetMatch("warmup_end");
        return HookResult.Continue;
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        if (!ShouldRun()) return HookResult.Continue;

        if (IsWarmup() && _phase is Phase.OvertimeAccepted or Phase.DrawDecided or Phase.Voting)
            ResetMatch("warmup");

        if (_phase == Phase.Voting)
        {
            var gameRules = GetGameRules();
            Logger.LogWarning(
                "[MatchOT] round_start during vote overtimePlaying={Overtime} gamePhase={Phase}",
                gameRules?.OvertimePlaying, gameRules?.GamePhase);
            Pause("round_start during vote");
            HoldVoteWindow("round_start during vote");
            return HookResult.Continue;
        }

        EnsureOvertimeArmed("round_start");
        HandleScores("round_start", startVoteIfTied: true);
        return HookResult.Continue;
    }

    private HookResult OnRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        if (!ShouldRun()) return HookResult.Continue;
        if (_phase == Phase.Voting)
            Pause("freeze_end during vote");
        return HookResult.Continue;
    }

    private HookResult OnRoundEndPre(EventRoundEnd @event, GameEventInfo info)
    {
        if (!ShouldRun())
            return HookResult.Continue;
        if (_phase is Phase.OvertimeAccepted or Phase.Voting or Phase.DrawDecided)
            return HookResult.Continue;
        if (IsWarmup())
            return HookResult.Continue;
        if (!TryGetTeamScores(out int terrorist, out int counterTerrorist))
            return HookResult.Continue;

        int tieScore = RegulationTieScore();
        if (terrorist == tieScore && counterTerrorist == tieScore && CountHumans() > 0)
            StretchWinPanelDisplayTime(VoteHoldSeconds(), "round_end pre");

        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        if (!ShouldRun()) return HookResult.Continue;
        HandleScores("round_end", startVoteIfTied: true);
        return HookResult.Continue;
    }

    private void HandleScores(string source, bool startVoteIfTied)
    {
        if (_phase is Phase.OvertimeAccepted or Phase.Voting or Phase.DrawDecided)
            return;
        if (IsWarmup())
            return;
        if (!TryGetTeamScores(out int terrorist, out int counterTerrorist))
            return;

        int tieScore = RegulationTieScore();
        if (tieScore <= 0)
            return;

        if (terrorist == tieScore && counterTerrorist == tieScore)
        {
            Logger.LogInformation(
                "[MatchOT] {Source} regulation tie {T}-{CT} (tieScore={Tie}) mp_overtime_enable={Enable}",
                source, terrorist, counterTerrorist, tieScore, ReadOvertimeEnabled());
            if (startVoteIfTied)
                BeginTieVote();
            return;
        }

        bool matchPoint =
            (terrorist == tieScore && counterTerrorist == tieScore - 1)
            || (counterTerrorist == tieScore && terrorist == tieScore - 1);
        if (!matchPoint)
            return;

        if (CountHumans() == 0)
        {
            Logger.LogInformation(
                "[MatchOT] {Source} match point {T}-{CT} with 0 humans; leave mp_overtime_enable 0",
                source, terrorist, counterTerrorist);
            return;
        }

        EnsureOvertimeArmed(source, terrorist, counterTerrorist);
    }

    private void EnsureOvertimeArmed(string source, int? terrorist = null, int? counterTerrorist = null)
    {
        if (_phase is Phase.Voting or Phase.OvertimeAccepted or Phase.DrawDecided)
            return;
        if (!ShouldRun())
            return;
        if (CountHumans() == 0)
            return;

        bool alreadyArmed = _phase == Phase.OtArmed;
        bool wasEnabled = ReadOvertimeEnabled();
        ApplyOvertimeEnabled();
        _phase = Phase.OtArmed;

        bool enabled = ReadOvertimeEnabled();
        if (alreadyArmed && wasEnabled && enabled)
            return;

        string scores = terrorist.HasValue && counterTerrorist.HasValue
            ? $" at {terrorist}-{counterTerrorist}"
            : string.Empty;
        Logger.LogInformation(
            "[MatchOT] {Source} armed vanilla OT{Scores}; mp_overtime_enable={Enable} startmoney={Money}",
            source, scores, enabled, Config.OvertimeStartMoney);
    }

    private void ApplyOvertimeEnabled()
    {
        int money = Config.OvertimeStartMoney;
        Server.ExecuteCommand("mp_overtime_enable 1");
        Server.ExecuteCommand($"mp_overtime_startmoney {money}");
        Server.ExecuteCommand("mp_overtime_maxrounds 6");
        Server.ExecuteCommand("mp_overtime_limit 0");
    }

    private static bool ReadOvertimeEnabled()
    {
        var overtime = ConVar.Find("mp_overtime_enable");
        if (overtime == null)
            return false;
        try
        {
            return overtime.GetPrimitiveValue<bool>();
        }
        catch
        {
            try
            {
                return overtime.GetPrimitiveValue<int>() != 0;
            }
            catch
            {
                return false;
            }
        }
    }

    private void BeginTieVote()
    {
        if (_phase is Phase.Voting or Phase.OvertimeAccepted or Phase.DrawDecided)
            return;

        Pause("regulation tie");
        HoldVoteWindow("regulation tie");

        if (CountHumans() == 0)
        {
            FailToDraw("no humans at regulation tie");
            return;
        }

        if (_voteApi == null)
        {
            FailToDraw("voteimprover:api missing");
            return;
        }

        if (_voteApi.IsVoteActive)
            _voteApi.CancelActiveVote("regulation tie overtime vote");

        bool started = _voteApi.TryStartVote(
            new HumanVoteRequest
            {
                IssueType = "Overtime",
                DisplayString = Config.VoteDisplayString,
                PassedString = Config.VotePassedString,
                DurationSeconds = Config.VoteDurationSeconds,
            },
            OnVoteComplete);

        if (!started)
        {
            FailToDraw("TryStartVote failed");
            return;
        }

        _phase = Phase.Voting;
        _voteStartedAt = Server.CurrentTime;
        NotifyHumansVoteStarted();
        Logger.LogInformation("[MatchOT] overtime vote started; waiting for Vote Improver (humans only)");
    }

    private void NotifyHumansVoteStarted()
    {
        var message = Config.VoteStartNotify;
        if (string.IsNullOrWhiteSpace(message))
            return;

        var api = _botIdentityApi;
        foreach (var player in Utilities.GetPlayers())
        {
            if (!IsHuman(player, api))
                continue;

            // Leading space so CS2 does not eat the first color byte.
            player.PrintToChat($" {ColorizeVoteNotify(message)}");
        }
    }

    private static string ColorizeVoteNotify(string message)
    {
        // CS2 does not paint `[` with the color that precedes it; re-apply
        // Green after the opening bracket so the frame and 加时赛 are green.
        return message
            .Replace("[加时赛]", $"{ChatColors.Green}[{ChatColors.Green}加时赛{ChatColors.Green}]{ChatColors.Default}", StringComparison.Ordinal)
            .Replace("F1", $"{ChatColors.Green}F1{ChatColors.Default}", StringComparison.Ordinal)
            .Replace("F2", $"{ChatColors.Red}F2{ChatColors.Default}", StringComparison.Ordinal);
    }

    private void OnVoteComplete(HumanVoteOutcome outcome)
    {
        Server.NextFrame(() =>
        {
            if (_phase != Phase.Voting)
                return;

            if (outcome == HumanVoteOutcome.Passed)
            {
                _phase = Phase.OvertimeAccepted;
                RestoreVoteWindow();
                UnpauseIfOurs();
                Logger.LogInformation("[MatchOT] vote passed; resuming vanilla overtime");
                return;
            }

            FailToDraw($"vote {outcome}");
        });
    }

    // GAMEPHASE_MATCH_ENDED. After a regulation tie with overtime already
    // armed, the engine sets m_nOvertimePlaying; TerminateRound(RoundDraw)
    // plus mp_overtime_enable 0 does not leave that state.
    private const int GamePhaseHalftime = 4;
    private const int GamePhaseMatchEnded = 5;

    private void FailToDraw(string reason)
    {
        Logger.LogInformation("[MatchOT] forcing draw: {Reason}", reason);
        _phase = Phase.DrawDecided;

        ApplyMatchEndedState("pre-terminate");
        RestoreOvertimeDisabled();
        // pausetimer must be 0 before TerminateRound or timeout skips
        // cs_win_panel_match. Do not restore duration/intro/display_time
        // here: writing duration back to 2 starts a fresh 2s halftime
        // scoreboard, and announce_phase_end opens that board rather
        // than closing it.
        RestoreHalftimePauseTimer();

        var gameRules = GetGameRules();
        if (gameRules == null)
            Logger.LogWarning("[MatchOT] TerminateRound skipped: cs_gamerules missing");
        else
            gameRules.TerminateRound(0.1f, RoundEndReason.RoundDraw);

        UnpauseIfOurs();
        Server.NextFrame(() =>
        {
            if (_phase != Phase.DrawDecided)
                return;
            ApplyMatchEndedState("post-terminate+1frame");
        });

        // F2 at ~2s still sits on the round win panel; the engine then emits
        // cs_win_panel_match. Timeout at 20s is still HALFTIME but that
        // window is gone: TerminateRound does not emit the event, and the
        // server map_end ~34s later without a client settlement screen.
        AddTimer(1.0f, FireMatchWinPanelIfEngineSkipped);
    }

    private void ApplyMatchEndedState(string source)
    {
        var proxy = GetGameRulesProxy();
        var gameRules = proxy?.GameRules;
        if (gameRules == null)
        {
            Logger.LogWarning("[MatchOT] {Source} skipped: cs_gamerules missing", source);
            return;
        }

        if (gameRules.GamePhase != GamePhaseHalftime && gameRules.GamePhase != GamePhaseMatchEnded)
        {
            Logger.LogWarning(
                "[MatchOT] {Source} FailToDraw outside HALFTIME overtimePlaying={Overtime} gamePhase={Phase}; client match-end may desync",
                source, gameRules.OvertimePlaying, gameRules.GamePhase);
        }

        Logger.LogInformation(
            "[MatchOT] {Source} before overtimePlaying={Overtime} gamePhase={Phase} endMatchOnThink={EndThink} endMatchOnRoundReset={EndReset} teamIntro={Intro} switchingTeams={Switch} freeze={Freeze} matchEndCount={MatchEnd} intermissionEnd={IntermissionEnd}",
            source, gameRules.OvertimePlaying, gameRules.GamePhase, gameRules.EndMatchOnThink, gameRules.EndMatchOnRoundReset, gameRules.TeamIntroPeriod, gameRules.SwitchingTeamsAtRoundReset, gameRules.FreezePeriod, gameRules.MatchEndCount, gameRules.IntermissionEndTime);

        gameRules.OvertimePlaying = 0;
        gameRules.GamePhase = GamePhaseMatchEnded;
        gameRules.EndMatchOnThink = true;
        gameRules.EndMatchOnRoundReset = true;
        gameRules.TimeUntilNextPhaseStarts = 0.1f;
        gameRules.TeamIntroPeriod = false;
        gameRules.SwitchingTeamsAtRoundReset = false;
        gameRules.FreezePeriod = false;
        gameRules.IntermissionEndTime = Server.CurrentTime;
        NotifyMatchEndedState(proxy);

        Logger.LogInformation(
            "[MatchOT] {Source} after overtimePlaying={Overtime} gamePhase={Phase} endMatchOnThink={EndThink} endMatchOnRoundReset={EndReset}",
            source, gameRules.OvertimePlaying, gameRules.GamePhase, gameRules.EndMatchOnThink, gameRules.EndMatchOnRoundReset);
    }

    private void NotifyMatchEndedState(CCSGameRulesProxy? proxy)
    {
        if (proxy == null || !proxy.IsValid)
            return;

        try
        {
            Utilities.SetStateChanged(proxy, "CCSGameRules", "m_nOvertimePlaying");
            Utilities.SetStateChanged(proxy, "CCSGameRules", "m_gamePhase");
            Utilities.SetStateChanged(proxy, "CCSGameRules", "m_endMatchOnThink");
            Utilities.SetStateChanged(proxy, "CCSGameRules", "m_endMatchOnRoundReset");
            Utilities.SetStateChanged(proxy, "CCSGameRules", "m_timeUntilNextPhaseStarts");
            Utilities.SetStateChanged(proxy, "CCSGameRules", "m_bTeamIntroPeriod");
            Utilities.SetStateChanged(proxy, "CCSGameRules", "m_bSwitchingTeamsAtRoundReset");
            Utilities.SetStateChanged(proxy, "CCSGameRules", "m_bFreezePeriod");
            Utilities.SetStateChanged(proxy, "CCSGameRules", "m_flIntermissionEndTime");
            Utilities.SetStateChanged(proxy, "CCSGameRules", "m_nMatchEndCount");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[MatchOT] SetStateChanged failed");
        }
    }

    // begin_new_match / match_start also fire at the start of an overtime
    // half. Ignoring OtArmed / Voting / OvertimeAccepted / DrawDecided
    // there keeps Yes from being undone by RestoreOvertimeDisabled, and
    // keeps FailToDraw from being undone by clearing EndMatchOnThink.
    // A real next match is reset from warmup or cs_win_panel_match instead.
    private void ResetMatch(string reason, bool ignoreDuringArmedVote = false)
    {
        if (ignoreDuringArmedVote && _phase is Phase.OtArmed or Phase.Voting or Phase.OvertimeAccepted or Phase.DrawDecided)
        {
            Logger.LogInformation("[MatchOT] ignore {Reason} during {Phase}", reason, _phase);
            return;
        }

        if (_phase == Phase.Voting)
            _voteApi?.CancelActiveVote($"match reset ({reason})");

        _phase = Phase.Idle;
        RestoreVoteWindow();
        RestoreOvertimeDisabled();
        var gameRules = GetGameRules();
        if (gameRules != null)
        {
            if (gameRules.EndMatchOnThink)
                gameRules.EndMatchOnThink = false;
            if (gameRules.EndMatchOnRoundReset)
                gameRules.EndMatchOnRoundReset = false;
        }
        UnpauseIfOurs();
        Logger.LogInformation("[MatchOT] match reset ({Reason})", reason);
    }

    private void FireMatchWinPanelIfEngineSkipped()
    {
        if (_phase != Phase.DrawDecided)
            return;

        Logger.LogWarning("[MatchOT] engine skipped cs_win_panel_match; firing it");
        try
        {
            new EventCsWinPanelMatch(true).FireEvent(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[MatchOT] FireEvent cs_win_panel_match failed");
        }
    }

    private void Pause(string reason)
    {
        Server.ExecuteCommand("mp_pause_match");
        _pausedByUs = true;
        Logger.LogInformation("[MatchOT] mp_pause_match ({Reason})", reason);
    }

    private float VoteHoldSeconds()
    {
        float duration = Config.VoteDurationSeconds > 0f ? Config.VoteDurationSeconds : 20f;
        return duration + 8f;
    }

    // Tie-to-OT uses mp_halftime_duration + mp_team_intro_time, not
    // mp_round_restart_delay. Timeout at 20s with duration=2/intro=6.5
    // started OT at 8.5s; FailToDraw then desynced map-vote from clients.
    private void HoldVoteWindow(string reason)
    {
        float hold = VoteHoldSeconds();
        StretchFloatConVar("mp_round_restart_delay", hold, 7f, ref _savedRoundRestartDelay, reason);
        StretchFloatConVar("mp_halftime_duration", hold, 15f, ref _savedHalftimeDuration, reason);
        StretchFloatConVar("mp_team_intro_time", hold, 6.5f, ref _savedTeamIntroTime, reason);
        StretchWinPanelDisplayTime(hold, reason);
        SetHalftimePauseTimer(1, reason);
        WriteTimeUntilNextPhaseStarts(hold);
    }

    private void WriteTimeUntilNextPhaseStarts(float seconds)
    {
        var proxy = GetGameRulesProxy();
        var gameRules = proxy?.GameRules;
        if (gameRules == null)
            return;

        gameRules.TimeUntilNextPhaseStarts = seconds;
        try
        {
            if (proxy != null && proxy.IsValid)
                Utilities.SetStateChanged(proxy, "CCSGameRules", "m_timeUntilNextPhaseStarts");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[MatchOT] SetStateChanged TimeUntilNextPhaseStarts failed");
        }
    }

    private void RestoreVoteWindow(bool restorePhaseTimer = true)
    {
        RestoreFloatConVar("mp_round_restart_delay", ref _savedRoundRestartDelay);
        RestoreFloatConVar("mp_halftime_duration", ref _savedHalftimeDuration);
        RestoreFloatConVar("mp_team_intro_time", ref _savedTeamIntroTime);
        RestoreHalftimePauseTimer();
        if (restorePhaseTimer)
        {
            RestoreWinPanelDisplayTime();
            WriteTimeUntilNextPhaseStarts(ReadFloatConVar("mp_halftime_duration", 15f));
        }
    }

    // The auto halftime scoreboard appears after mp_win_panel_display_time
    // (~3s) while GamePhase=4. Stretch this CVar immediately so the round
    // win panel lasts the vote. announce_phase_end opens a new board.
    private void StretchWinPanelDisplayTime(float hold, string reason)
    {
        float current = ReadFloatConVar("mp_win_panel_display_time", 3f);
        if (current >= hold)
            return;

        _savedWinPanelDisplayTime ??= current;
        var cvar = ConVar.Find("mp_win_panel_display_time");
        if (cvar != null)
            cvar.SetValue(hold);
        else
            Server.ExecuteCommand($"mp_win_panel_display_time {hold}");
        Logger.LogInformation("[MatchOT] stretch mp_win_panel_display_time {From} -> {To} ({Reason})", current, hold, reason);
    }

    private void RestoreWinPanelDisplayTime()
    {
        if (_savedWinPanelDisplayTime is not float value)
            return;

        var cvar = ConVar.Find("mp_win_panel_display_time");
        if (cvar != null)
            cvar.SetValue(value);
        else
            Server.ExecuteCommand($"mp_win_panel_display_time {value}");
        Logger.LogInformation("[MatchOT] restore mp_win_panel_display_time {Value}", value);
        _savedWinPanelDisplayTime = null;
    }

    private void StretchFloatConVar(string name, float hold, float fallback, ref float? saved, string reason)
    {
        float current = ReadFloatConVar(name, fallback);
        if (current >= hold)
            return;

        saved ??= current;
        Server.ExecuteCommand($"{name} {hold}");
        Logger.LogInformation("[MatchOT] stretch {Cvar} {From} -> {To} ({Reason})", name, current, hold, reason);
    }

    private void RestoreFloatConVar(string name, ref float? saved)
    {
        if (saved is not float value)
            return;
        Server.ExecuteCommand($"{name} {value}");
        Logger.LogInformation("[MatchOT] restore {Cvar} {Value}", name, value);
        saved = null;
    }

    private void SetHalftimePauseTimer(int value, string reason)
    {
        int current = (int)ReadFloatConVar("mp_halftime_pausetimer", 0f);
        _savedHalftimePauseTimer ??= current;
        if (current == value)
            return;
        Server.ExecuteCommand($"mp_halftime_pausetimer {value}");
        Logger.LogInformation("[MatchOT] mp_halftime_pausetimer {From} -> {To} ({Reason})", current, value, reason);
    }

    private void RestoreHalftimePauseTimer()
    {
        if (_savedHalftimePauseTimer is not int saved)
            return;
        Server.ExecuteCommand($"mp_halftime_pausetimer {saved}");
        Logger.LogInformation("[MatchOT] restore mp_halftime_pausetimer {Value}", saved);
        _savedHalftimePauseTimer = null;
    }

    private static float ReadFloatConVar(string name, float fallback)
    {
        var cvar = ConVar.Find(name);
        if (cvar == null)
            return fallback;
        try
        {
            return cvar.GetPrimitiveValue<float>();
        }
        catch
        {
            try
            {
                return cvar.GetPrimitiveValue<int>();
            }
            catch
            {
                return fallback;
            }
        }
    }

    private void UnpauseIfOurs()
    {
        if (!_pausedByUs)
            return;
        Server.ExecuteCommand("mp_unpause_match");
        _pausedByUs = false;
        Logger.LogInformation("[MatchOT] mp_unpause_match");
    }

    private static void RestoreOvertimeDisabled()
    {
        Server.ExecuteCommand("mp_overtime_enable 0");
    }

    private bool ShouldRun()
    {
        return Config.Enabled && !_interceptDisabled && IsCompetitive();
    }

    private static bool IsCompetitive()
    {
        var type = ConVar.Find("game_type");
        var mode = ConVar.Find("game_mode");
        if (type == null || mode == null)
            return false;
        try
        {
            return type.GetPrimitiveValue<int>() == 0 && mode.GetPrimitiveValue<int>() == 1;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsWarmup()
    {
        return GetGameRules()?.WarmupPeriod ?? true;
    }

    private static int RegulationTieScore()
    {
        var maxRounds = ConVar.Find("mp_maxrounds");
        int max = 24;
        try
        {
            if (maxRounds != null)
                max = maxRounds.GetPrimitiveValue<int>();
        }
        catch
        {
            max = 24;
        }

        if (max <= 0)
            max = 24;
        return max / 2;
    }

    private int CountHumans()
    {
        int count = 0;
        var api = _botIdentityApi;
        foreach (var player in Utilities.GetPlayers())
        {
            if (!IsHuman(player, api))
                continue;
            count++;
        }

        return count;
    }

    private static bool IsHuman(CCSPlayerController player, IBotIdentityApi? api)
    {
        if (!player.IsValid || player.IsHLTV)
            return false;
        if (player.Connected != PlayerConnectedState.Connected)
            return false;
        if (player.IsBot)
            return false;
        if (api?.IsManagedBot(player.Slot) == true)
            return false;
        return true;
    }

    private static bool TryGetTeamScores(out int terrorist, out int counterTerrorist)
    {
        terrorist = 0;
        counterTerrorist = 0;

        var teams = Utilities.FindAllEntitiesByDesignerName<CCSTeam>("cs_team_manager")
            .Where(team => team.IsValid)
            .ToList();
        var t = teams.FirstOrDefault(team => team.TeamNum == (byte)CsTeam.Terrorist);
        var ct = teams.FirstOrDefault(team => team.TeamNum == (byte)CsTeam.CounterTerrorist);
        if (t == null || ct == null)
            return false;

        terrorist = t.Score;
        counterTerrorist = ct.Score;
        return true;
    }

    private static CCSGameRulesProxy? GetGameRulesProxy()
    {
        return Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
            .FirstOrDefault();
    }

    private static CCSGameRules? GetGameRules() => GetGameRulesProxy()?.GameRules;
}
