using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace MatchOvertime;

public sealed class MatchOvertimeConfig : BasePluginConfig
{
    [JsonPropertyName("Enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("OvertimeStartMoney")]
    public int OvertimeStartMoney { get; set; } = 12500;

    /// <summary>0 = Vote Improver / sv_vote_timer_duration.</summary>
    [JsonPropertyName("VoteDurationSeconds")]
    public float VoteDurationSeconds { get; set; } = 20f;

    [JsonPropertyName("VoteDisplayString")]
    public string VoteDisplayString { get; set; } = "#SFUI_vote_restart_game";

    [JsonPropertyName("VotePassedString")]
    public string VotePassedString { get; set; } = "#SFUI_vote_passed_restart_game";

    /// <summary>
    /// Chat line sent to humans when the overtime vote opens.
    /// Empty string disables it. The vote HUD first line cannot say overtime.
    /// </summary>
    [JsonPropertyName("VoteStartNotify")]
    public string VoteStartNotify { get; set; } =
        "[加时赛] 当前比分战平。请按 F1 或 F2 投票决定是否进入加时。";
}
