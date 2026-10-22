using System.Reflection;
using CrusaderWars;
using CrusaderWars.data.save_file;
using CrusaderWars.StatFidelity;
using CrusaderWars.twbattle;
using CrusaderWars.unit_mapper;
using Xunit;

public sealed class KnightTemplateCandidateTests : IDisposable
{
    private static readonly Type Mapper = typeof(AvailableUnit).Assembly.GetType("CrusaderWars.unit_mapper.UnitMappers_BETA")!;
    private static readonly PropertyInfo MapperPath = Mapper.GetProperty("LoadedUnitMapper_FolderPath", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly PropertyInfo Playthrough = Mapper.GetProperty("ActivePlaythroughTag", BindingFlags.Public | BindingFlags.Static)!;
    private readonly string oldDirectory = Environment.CurrentDirectory;
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "cc-knight-candidates-" + Guid.NewGuid().ToString("N"));
    private readonly object? oldMapperPath = MapperPath.GetValue(null);
    private readonly object? oldPlaythrough = Playthrough.GetValue(null);
    private readonly Dictionary<(string, bool), (string, bool)> oldManual;
    private readonly Dictionary<string, (string replacementKey, bool isSiege)> oldAutofix;

    public KnightTemplateCandidateTests()
    {
        Directory.CreateDirectory(Path.Combine(scratch, "Factions"));
        Directory.CreateDirectory(Path.Combine(scratch, "Titles"));
        Directory.CreateDirectory(Path.Combine(scratch, "data"));
        Environment.CurrentDirectory = scratch;
        oldManual = BattleState.ManualUnitReplacements;
        BattleState.ManualUnitReplacements = new();
        oldAutofix = new(BattleProcessor.AutofixReplacements);
        BattleProcessor.AutofixReplacements.Clear();
        MapperPath.SetValue(null, scratch);
        Playthrough.SetValue(null, null);
    }

    [Fact]
    public void ActualBritonRankZeroUsesSameRankGeneralAppearanceWithoutChangingTheUnit()
    {
        Write("a.xml", """
            <General key="cha_cel_teulu" rank="1"/><General key="rom_desert_cav" rank="1"/>
            <General key="cha_cel_teulu" rank="2"/><General key="cha_cel_teulu" rank="3"/>
            <Knights key="bel_rom_bucellarii_guard_cavalry" rank="1"/><Knights key="cha_gen_skirm_cav" rank="1"/>
            <Knights key="KOK_cha_gen_guard_cav" rank="2"/><Knights key="REB_ITAL_dux" rank="3"/>
            """);
        var unit = Map();
        var stats = new Dictionary<string, AttilaUnitCombatStats> {
            ["bel_rom_bucellarii_guard_cavalry"] = Cavalry("bel_rom_bucellarii_guard_cavalry") with { HasMissileWeapon = true, MountHitPoints = 135 },
            ["cha_gen_skirm_cav"] = Cavalry("cha_gen_skirm_cav") with { HasMissileWeapon = true },
            ["cha_cel_teulu"] = Cavalry("cha_cel_teulu"),
            ["rom_desert_cav"] = Cavalry("rom_desert_cav") with { HasMissileWeapon = true }
        };
        var candidate = Assert.Single(Candidates(unit, key => stats[key]));
        Assert.Equal("cha_cel_teulu", candidate.UnitKey);
        Assert.Equal("Briton", candidate.Faction);
        Assert.Equal(1, candidate.AuthorRank);
        Assert.Equal("General", candidate.MapperRole);
        Assert.Equal(Path.Combine(scratch, "Factions", "a.xml"), candidate.MapperFile);
        Assert.Equal("bel_rom_bucellarii_guard_cavalry", candidate.OriginalUnitKey);
        Assert.Equal(candidate.OriginalUnitKey, unit.GetAttilaUnitKey());
        Assert.Equal(candidate.OriginalUnitKey, unit.OriginalAttilaKey);
        Assert.Equal(0, unit.CharacterRank);
    }

    [Fact]
    public void SameRolePrecedesGeneralAndBothKeepTheOriginalEffectiveAuthorRank()
    {
        Write("a.xml", """
            <Knights key="a_hybrid" rank="2"/><Knights key="z_knight" rank="2"/>
            <Knights key="lower_knight" rank="1"/><Knights key="higher_knight" rank="3"/>
            <General key="a_general" rank="2"/><General key="lower_general" rank="1"/>
            """);
        var unit = Map(rank: 2);
        var result = Candidates(unit, key => Cavalry(key) with { HasMissileWeapon = key == "a_hybrid" });
        Assert.Equal(new[] { "z_knight", "a_general" }, result.Select(candidate => candidate.UnitKey));
        Assert.Equal(new[] { "Knights", "General" }, result.Select(candidate => candidate.MapperRole));
        Assert.All(result, candidate => Assert.Equal(2, candidate.AuthorRank));
    }

    [Fact]
    public void FiltersMissilesSpecialEntitiesUnverifiedHpWrongRoleAndWrongRepresentation()
    {
        string[] names = ["a_hybrid", "foot", "missile", "special", "naval", "unknown_mount_hp", "zero_hp", "zero_damage", "wrong_role", "valid"];
        Write("a.xml", string.Join("", names.Select(key => $"<Knights key=\"{key}\" rank=\"1\"/>")));
        var stats = names.ToDictionary(key => key, Cavalry);
        stats["a_hybrid"] = stats["a_hybrid"] with { HasMissileWeapon = true };
        stats["foot"] = stats["foot"] with { IsMounted = false, MountHitPoints = null, Category = "inf_melee" };
        stats["missile"] = stats["missile"] with { HasMissileWeapon = true };
        stats["special"] = stats["special"] with { HasSpecialEntity = true };
        stats["naval"] = stats["naval"] with { IsNaval = true };
        stats["unknown_mount_hp"] = stats["unknown_mount_hp"] with { MountHitPoints = null };
        stats["zero_hp"] = stats["zero_hp"] with { EntityHitPoints = 0, MountHitPoints = 0 };
        stats["zero_damage"] = stats["zero_damage"] with { MeleeDamage = 0, ArmourPiercingDamage = 0 };
        stats["wrong_role"] = stats["wrong_role"] with { Category = "inf_ranged" };
        Assert.Equal("valid", Assert.Single(Candidates(Map(), key => stats[key])).UnitKey);
    }

    [Fact]
    public void FootKnightRepresentationStaysOnFoot()
    {
        Write("a.xml", "<Knights key=\"a_hybrid\"/><General key=\"foot\"/><General key=\"horse\"/>");
        Assert.Equal("foot", Assert.Single(Candidates(Map(), key => key == "horse" ? Cavalry(key) :
            Cavalry(key) with { IsMounted = false, MountHitPoints = null, Category = "inf_melee", HasMissileWeapon = key == "a_hybrid" })).UnitKey);
    }

    [Theory]
    [InlineData("naval")]
    [InlineData("special")]
    [InlineData("unknown_hp")]
    [InlineData("zero_hp")]
    [InlineData("zero_melee")]
    [InlineData("wrong_role")]
    [InlineData("already_melee_only")]
    public void UnsupportedOriginalTemplateCannotBeRepairedByAnOtherwiseValidAlternative(string condition)
    {
        Write("a.xml", "<Knights key=\"hybrid\"/><General key=\"good\"/>");
        var original = Cavalry("hybrid") with { HasMissileWeapon = true };
        original = condition switch {
            "naval" => original with { IsNaval = true },
            "special" => original with { HasSpecialEntity = true },
            "unknown_hp" => original with { MountHitPoints = null },
            "zero_hp" => original with { EntityHitPoints = 0, MountHitPoints = 0 },
            "zero_melee" => original with { MeleeDamage = 0, ArmourPiercingDamage = 0 },
            "wrong_role" => original with { Category = "inf_ranged" },
            "already_melee_only" => original with { HasMissileWeapon = false },
            _ => throw new ArgumentOutOfRangeException(nameof(condition))
        };
        Assert.Empty(Candidates(Map(), key => {
            Assert.Equal("hybrid", key); // No alternative query is allowed past this boundary.
            return original;
        }));
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("autofix")]
    [InlineData("explicit")]
    [InlineData("changed_key")]
    public void ExplicitSelectionsNeverTriggerAutomaticAlternatives(string mode)
    {
        Write("a.xml", "<Knights key=\"hybrid\"/><General key=\"good\"/>");
        var unit = Map();
        if (mode == "manual") BattleState.ManualUnitReplacements[("hybrid", false)] = ("hybrid", false);
        if (mode == "autofix") BattleProcessor.AutofixReplacements["hybrid"] = ("hybrid", false);
        string key = mode == "changed_key" ? "selected_by_user" : "hybrid";
        var result = Candidates(unit, _ => throw new Exception("Explicit selections must not query alternative stats."),
            mode == "explicit" ? "user_template" : null, key);
        Assert.Empty(result);
    }

    [Fact]
    public void AlternativeReplacementIsNotSilentlyUndone()
    {
        Write("a.xml", "<Knights key=\"hybrid\"/><General key=\"good\"/>");
        var unit = Map();
        BattleState.ManualUnitReplacements[("good", false)] = ("chosen_hybrid", false);
        Assert.Empty(Candidates(unit, key => {
            Assert.Equal("hybrid", key);
            return Cavalry(key) with { HasMissileWeapon = true };
        }));
    }

    [Fact]
    public void NoRankJumpOrOlderMapperOrDefaultFactionCanSupplyAnAlternative()
    {
        Write("a.xml", "<Knights key=\"hybrid\" rank=\"1\"/><General key=\"old_general\" rank=\"1\"/>");
        Write("z.xml", "<Knights key=\"hybrid\" rank=\"1\"/><General key=\"higher_general\" rank=\"2\"/>");
        File.WriteAllText(Path.Combine(scratch, "Factions", "default.xml"), "<Factions><Faction name=\"Default\"><Knights key=\"default_knight\" rank=\"1\"/><General key=\"default_general\" rank=\"1\"/></Faction></Factions>");
        Assert.Empty(Candidates(Map(), key => {
            Assert.Equal("hybrid", key);
            return Cavalry(key) with { HasMissileWeapon = true };
        }));
    }

    [Fact]
    public void TitleSpecificMappingIsNotReinterpretedAsFactionMapping()
    {
        Write("a.xml", "<Knights key=\"hybrid\"/><General key=\"good\"/>");
        File.WriteAllText(Path.Combine(scratch, "Titles", "title.xml"), "<Titles><Title title_key=\"k_test\"><Knights key=\"hybrid\"/></Title></Titles>");
        var owner = new Owner("42"); owner.SetPrimaryTitle("k_test");
        Assert.Empty(Candidates(Map(owner: owner), _ => throw new Exception("Title mapping must be preserved.")));
    }

    [Fact]
    public void DefaultFactionOrAnotherCultureCannotFillAnAbsentSpecificPool()
    {
        Write("a.xml", "<Knights key=\"default_knight\"/><General key=\"good\"/>", "Default");
        Write("b.xml", "<Knights key=\"other_knight\"/><General key=\"other_good\"/>", "Other");
        Assert.Empty(Candidates(Map(), _ => throw new Exception("No exact faction pool exists.")));
    }

    private void Write(string name, string nodes, string faction = "Briton") =>
        File.WriteAllText(Path.Combine(scratch, "Factions", name), $"<Factions><Faction name=\"{faction}\">{nodes}</Faction></Factions>");

    private Unit Map(int rank = 0, Owner? owner = null)
    {
        var unit = new Unit("Knight", 10, null, RegimentType.Knight, false, owner ?? new Owner("42"));
        typeof(Unit).GetProperty(nameof(Unit.CharacterRank))!.SetValue(unit, rank);
        unit.SetAttilaFaction("Briton");
        var result = ((string, bool))Mapper.GetMethod("GetUnitKey", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [unit])!;
        unit.SetUnitKey(result.Item1);
        return unit;
    }

    private static IReadOnlyList<KnightTemplateCandidate> Candidates(Unit unit, Func<string, AttilaUnitCombatStats> query,
        string? explicitOverride = null, string? key = null) =>
        (IReadOnlyList<KnightTemplateCandidate>)Mapper.GetMethod("GetAutomaticKnightTemplateCandidates", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [unit, key ?? unit.GetAttilaUnitKey(), query, explicitOverride])!;

    private static AttilaUnitCombatStats Cavalry(string key) =>
        new(key, key, "cavalry", 80, 6, 4, 100, 0, true, false, false, false, 20, 20, 40, 50);

    public void Dispose()
    {
        MapperPath.SetValue(null, oldMapperPath);
        Playthrough.SetValue(null, oldPlaythrough);
        BattleState.ManualUnitReplacements = oldManual;
        BattleProcessor.AutofixReplacements.Clear();
        foreach (var pair in oldAutofix) BattleProcessor.AutofixReplacements.Add(pair.Key, pair.Value);
        Environment.CurrentDirectory = oldDirectory;
        string resolved = Path.GetFullPath(scratch);
        if (Path.GetDirectoryName(resolved) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar))
            Directory.Delete(resolved, true);
    }
}
