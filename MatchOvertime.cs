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
    }

    private static readonly PluginCapability<IHumanVoteApi> VoteCapability = new("voteimprover:api");
    private static readonly PluginCapability<IBotIdentityApi> BotIdentityCapability = new("botidentity:api");

    private IHumanVoteApi? _voteApi;
    private IBotIdentityApi? _botIdentityApi;
    private Phase _phase = Phase.Idle;
    private bool _pausedByUs;
    private bool _interceptDisabled;

    public MatchOvertimeConfig Config { get; set; } = new();

    public override string ModuleName => "Match Overtime";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "CS2-Match-Overtime";
    public override string ModuleDescription =>
        "Pauses a regulation tie and waits for a Vote Improver humans-only vote before vanilla overtime or a draw.";

    public void OnConfigParsed(MatchOvertimeConfig config)
    {
        config.OvertimeStartMoney = Math.Clamp(config.OvertimeStartMoney, 0, 16000);
        config.VoteDurationSeconds = Math.Clamp(config.VoteDurationSeconds, 0f, 300f);
        config.VoteDisplayDetails ??= "是否进入加时赛？";
        Config = config;
    }

    public override void Load(bool hotReload)
    {
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventRoundFreezeEnd>(OnRoundFreezeEnd);
        RegisterEventHandler<EventRoundAnnounceMatchStart>(OnMatchStart);
        RegisterEventHandler<EventBeginNewMatch>(OnBeginNewMatch);
        RegisterEventHandler<EventCsWinPanelRound>(OnCsWinPanelRound);
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
        ResetMatch("begin_new_match");
        return HookResult.Continue;
    }

    private HookResult OnMatchStart(EventRoundAnnounceMatchStart @event, GameEventInfo info)
    {
        ResetMatch("match_start");
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

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        if (!ShouldRun()) return HookResult.Continue;
        if (_phase == Phase.Voting)
        {
            Pause("round_start during vote");
            return HookResult.Continue;
        }

        HandleScores("round_start");
        return HookResult.Continue;
    }

    private HookResult OnRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        if (!ShouldRun()) return HookResult.Continue;
        if (_phase == Phase.Voting)
            Pause("freeze_end during vote");
        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        if (!ShouldRun()) return HookResult.Continue;
        HandleScores("round_end");
        return HookResult.Continue;
    }

    private void HandleScores(string source)
    {
        if (_phase == Phase.OvertimeAccepted || _phase == Phase.Voting)
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
                "[MatchOT] {Source} regulation tie {T}-{CT} (tieScore={Tie})",
                source, terrorist, counterTerrorist, tieScore);
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

        ArmOvertime(source, terrorist, counterTerrorist);
    }

    private void ArmOvertime(string source, int terrorist, int counterTerrorist)
    {
        int money = Config.OvertimeStartMoney;
        Server.ExecuteCommand("mp_overtime_enable 1");
        Server.ExecuteCommand($"mp_overtime_startmoney {money}");
        Server.ExecuteCommand("mp_overtime_maxrounds 6");
        Server.ExecuteCommand("mp_overtime_limit 0");
        _phase = Phase.OtArmed;
        Logger.LogInformation(
            "[MatchOT] {Source} armed vanilla OT at {T}-{CT}; startmoney={Money}",
            source, terrorist, counterTerrorist, money);
    }

    private void BeginTieVote()
    {
        if (_phase == Phase.Voting || _phase == Phase.OvertimeAccepted)
            return;

        Pause("regulation tie");

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
                DetailsForUi = Config.VoteDisplayDetails,
                DurationSeconds = Config.VoteDurationSeconds,
            },
            OnVoteComplete);

        if (!started)
        {
            FailToDraw("TryStartVote failed");
            return;
        }

        _phase = Phase.Voting;
        Logger.LogInformation("[MatchOT] overtime vote started; waiting for Vote Improver (humans only)");
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
                UnpauseIfOurs();
                Logger.LogInformation("[MatchOT] vote passed; resuming vanilla overtime");
                return;
            }

            FailToDraw($"vote {outcome}");
        });
    }

    private void FailToDraw(string reason)
    {
        Logger.LogInformation("[MatchOT] forcing draw: {Reason}", reason);
        _phase = Phase.Idle;
        RestoreOvertimeDisabled();

        var gameRules = GetGameRules();
        if (gameRules == null)
            Logger.LogWarning("[MatchOT] TerminateRound skipped: cs_gamerules missing");
        else
            gameRules.TerminateRound(5f, RoundEndReason.RoundDraw);

        UnpauseIfOurs();
    }

    private void ResetMatch(string reason)
    {
        if (_phase == Phase.Voting)
            _voteApi?.CancelActiveVote($"match reset ({reason})");

        _phase = Phase.Idle;
        RestoreOvertimeDisabled();
        UnpauseIfOurs();
        Logger.LogInformation("[MatchOT] match reset ({Reason})", reason);
    }

    private void Pause(string reason)
    {
        Server.ExecuteCommand("mp_pause_match");
        _pausedByUs = true;
        Logger.LogInformation("[MatchOT] mp_pause_match ({Reason})", reason);
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

    private static CCSGameRules? GetGameRules()
    {
        return Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
            .FirstOrDefault()?.GameRules;
    }
}
