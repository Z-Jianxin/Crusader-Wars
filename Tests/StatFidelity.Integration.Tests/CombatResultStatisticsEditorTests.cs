using CrusaderWars.data.battle_results.editors;
using Xunit;

public sealed class CombatResultStatisticsEditorTests
{
    private static string Group(string? type, string knight, int initial) =>
        "{ " + (type == null ? "" : "type=" + type + " ") + "knight=" + knight + " damage_last_tick=3.125 main_kills=9.5 pursuit_kills=2 main_losses=1.25 pursuit_losses_maa=1 pursuit_losses_self=1 initial_count=" + initial + " final_count=0 note=\"} # untouched\" }";

    private static string Fixture(string attacker, string defender) =>
        "combat_results={\r\n 3=none\r\n 4={ location=8 attacker={ surviving_soldiers=777 inital_soldiers=200 regiment_stats={ " + attacker + " } }\r\n defender={ surviving_soldiers=888 inital_soldiers=80 regiment_stats={ " + defender + " } }\r\n winning_side=-1 start_date=1066.9.25 end_date=1.1.1 war_score=0 win=4294967295 leader=4294967295 result={ prestige=0 custom=\"{ \\\" untouched\" } }\r\n 9={ untouched=\"history } {\" }\r\n}";

    [Fact]
    public void SentinelLeviesAndQuotedMaaUpdateBothSidesWithoutTouchingPendingNativeSummary()
    {
        string levy = Group(null, "4294967295", 100);
        string bow = Group("\"bowmen\"", "4294967295", 100);
        string enemy = Group("bowmen", "4294967295", 80);
        string original = Fixture(levy + " " + bow, enemy);
        var updates = new[] {
            new CombatResultStatisticsUpdate("attacker", null, null, 30, 4, 60, 5),
            new CombatResultStatisticsUpdate("attacker", "bowmen", null, 11, 0, 70, 0),
            new CombatResultStatisticsUpdate("defender", "bowmen", null, 9, 1, 20, 10)
        };
        string expected = original.Replace(levy, Edited(levy, 30, 4, 35, 5))
            .Replace(bow, Edited(bow, 11, 0, 30, 0)).Replace(enemy, Edited(enemy, 9, 1, 50, 10))
            .Replace("surviving_soldiers=777", "surviving_soldiers=130").Replace("surviving_soldiers=888", "surviving_soldiers=20");
        Assert.Equal(expected, CombatResultStatisticsEditor.Edit(original, "4", updates));
    }

    [Fact]
    public void ActualKnightIdsNeverCollideWithTheNoKnightSentinel()
    {
        string a = Group(null, "42", 1), b = Group(null, "43", 1), c = Group(null, "4294967295", 10);
        string original = Fixture(a + " " + b, c);
        var updates = new[] {
            new CombatResultStatisticsUpdate("attacker", null, "42", 15, 0, 1, 0),
            new CombatResultStatisticsUpdate("attacker", null, "43", 20, 0, 0, 0),
            new CombatResultStatisticsUpdate("defender", null, null, 0, 0, 4, 0)
        };
        string actual = CombatResultStatisticsEditor.Edit(original, "4", updates);
        Assert.Contains(Edited(a, 15, 0, 0, 0), actual);
        Assert.Contains(Edited(b, 20, 0, 1, 0), actual);
        Assert.Contains("surviving_soldiers=1 ", actual);
        Assert.Contains("surviving_soldiers=4 ", actual);
    }

    [Fact]
    public void UnknownMissingAndDuplicateGroupsFailWithoutFallback()
    {
        string group = Group(null, "4294967295", 10);
        string original = Fixture(group, group);
        var a = new CombatResultStatisticsUpdate("attacker", null, null, 0, 0, 5, 0);
        var d = a with { Side = "defender" };
        Assert.Throws<InvalidDataException>(() => CombatResultStatisticsEditor.Edit(original, "4", new[] { a }));
        Assert.Throws<InvalidDataException>(() => CombatResultStatisticsEditor.Edit(original, "4", new[] { a, d, d }));
        Assert.Throws<InvalidDataException>(() => CombatResultStatisticsEditor.Edit(original, "4", new[] { a, d, a with { RegimentType = "unknown" } }));
        Assert.Throws<InvalidDataException>(() => CombatResultStatisticsEditor.Edit(Fixture(group + " " + group, group), "4", new[] { a, d }));
        Assert.Throws<InvalidDataException>(() => CombatResultStatisticsEditor.Edit(original, "3", new[] { a, d }));
        Assert.Throws<InvalidDataException>(() => CombatResultStatisticsEditor.Edit(original, "11", new[] { a, d }));
    }

    [Theory]
    [InlineData("attacker", "type", "42", 0, 0, 1, 0)]
    [InlineData("left", null, null, 0, 0, 1, 0)]
    [InlineData("attacker", null, "4294967295", 0, 0, 1, 0)]
    [InlineData("attacker", "", null, 0, 0, 1, 0)]
    [InlineData("attacker", null, null, -1, 0, 1, 0)]
    [InlineData("attacker", null, null, 0, -1, 1, 0)]
    [InlineData("attacker", null, null, 0, 0, -1, 0)]
    [InlineData("attacker", null, null, 0, 0, 1, -1)]
    [InlineData("attacker", null, null, 0, 0, 9, 2)]
    public void InvalidIdentityOrInconsistentLossAccountingIsRejected(string side, string? type, string? knight,
        int main, int pursuit, int final, int losses)
    {
        string g = Group(null, "4294967295", 10);
        Assert.Throws<InvalidDataException>(() => CombatResultStatisticsEditor.Edit(Fixture(g, g), "4", new[] {
            new CombatResultStatisticsUpdate(side,type,knight,main,pursuit,final,losses),
            new CombatResultStatisticsUpdate("defender",null,null,0,0,0,0)
        }));
    }

    [Theory]
    [InlineData("main_kills=9.5", "main_kills=9.5 main_kills=2")]
    [InlineData("initial_count=10", "initial_count=10 initial_count=11")]
    [InlineData("pursuit_losses_self=1", "")]
    [InlineData("knight=4294967295", "knight=unknown")]
    [InlineData("note=\"} # untouched\"", "note=\"unterminated")]
    public void MalformedOrAmbiguousNativeFieldsAreRejected(string before, string after)
    {
        string g = Group(null, "4294967295", 10);
        string text = Fixture(g.Replace(before, after), g);
        Assert.Throws<InvalidDataException>(() => CombatResultStatisticsEditor.Edit(text,"4",new[] {
            new CombatResultStatisticsUpdate("attacker",null,null,0,0,5,0),
            new CombatResultStatisticsUpdate("defender",null,null,0,0,4,0)
        }));
    }

    [Fact]
    public void CommentBracesAndTwoSameTypeGroupsOnDifferentSidesAreIndependent()
    {
        string g = Group("huscarl", "4294967295", 10);
        string text = Fixture(g, g).Replace("regiment_stats={", "regiment_stats={ # } { comment\r\n");
        string result = CombatResultStatisticsEditor.Edit(text, "4", new[] {
            new CombatResultStatisticsUpdate("attacker","huscarl",null,2,0,3,0),
            new CombatResultStatisticsUpdate("defender","huscarl",null,1,0,4,0)
        });
        Assert.Contains(Edited(g, 2, 0, 7, 0), result);
        Assert.Contains(Edited(g, 1, 0, 6, 0), result);
        Assert.Contains("regiment_stats={ # } { comment\r\n", result);
    }

    [Fact]
    public void PendingFinalCountStaysZeroUntilNativeCompletionAndPrefilledResultsFailClosed()
    {
        string group = Group(null, "4294967295", 10);
        string original = Fixture(group, group);
        var updates = new[] {
            new CombatResultStatisticsUpdate("attacker", null, null, 7, 0, 6, 0),
            new CombatResultStatisticsUpdate("defender", null, null, 3, 0, 2, 0)
        };
        string actual = CombatResultStatisticsEditor.Edit(original, "4", updates);
        Assert.Equal(2, actual.Split("final_count=0").Length - 1);
        Assert.Contains("surviving_soldiers=6", actual);
        Assert.Contains("surviving_soldiers=2", actual);
        Assert.Contains("main_losses=4", actual);
        Assert.Contains("main_losses=8", actual);
        Assert.Throws<InvalidDataException>(() => CombatResultStatisticsEditor.Edit(
            original.Replace("final_count=0", "final_count=1"), "4", updates));
    }

    private static string Edited(string original, int mainKills, int pursuitKills, int mainLosses, int pursuitLosses) =>
        original.Replace("main_kills=9.5", "main_kills=" + mainKills).Replace("pursuit_kills=2", "pursuit_kills=" + pursuitKills)
            .Replace("main_losses=1.25", "main_losses=" + mainLosses).Replace("pursuit_losses_maa=1", "pursuit_losses_maa=0")
            .Replace("pursuit_losses_self=1", "pursuit_losses_self=" + pursuitLosses);
}
