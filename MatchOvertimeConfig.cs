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
    public float VoteDurationSeconds { get; set; }

    [JsonPropertyName("VoteDisplayString")]
    public string VoteDisplayString { get; set; } = "#SFUI_vote";

    [JsonPropertyName("VotePassedString")]
    public string VotePassedString { get; set; } = "#SFUI_vote_passed";

    [JsonPropertyName("VoteDisplayDetails")]
    public string VoteDisplayDetails { get; set; } = "是否进入加时赛？";
}
