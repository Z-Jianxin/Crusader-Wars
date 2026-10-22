using System.Reflection;
using System.Xml;
using CrusaderWars;
using CrusaderWars.data.save_file;
using CrusaderWars.StatFidelity;
using CrusaderWars.twbattle;
using CrusaderWars.unit_mapper;
using Xunit;

public sealed class NativeMappingResolverTests
{
    [InstalledDefaultMapperFact]
    public void SemanticCandidateThenManualOrAutofixOverrideSurvivesRegeneration()
    {
        using var fixture = new Fixture();
        var unit = fixture.Unit("druzhina");
        Assert.Equal("cha_gen_armoured_sword", fixture.Map(unit)); // Authored mounted A -> same-family foot C.
        Assert.Equal("repaired", fixture.Decision(unit).Status);
        BattleState.ManualUnitReplacements[("cha_gen_armoured_sword", false)] = ("chosen_manually", false);
        Assert.Equal("chosen_manually", fixture.Map(unit));
        Assert.Equal("protected", fixture.Decision(unit).Status);
        Assert.Equal("chosen_manually", fixture.Decision(unit).ResolvedKey);
        BattleState.ManualUnitReplacements.Clear();
        BattleProcessor.AutofixReplacements["cha_gen_armoured_sword"] = ("chosen_autofix", false);
        Assert.Equal("chosen_autofix", fixture.Map(unit));
        Assert.Equal("protected", fixture.Decision(unit).Status);
        Assert.Equal("chosen_autofix", fixture.Decision(unit).ResolvedKey);
    }

    [InstalledDefaultMapperFact]
    public void RefreshRepairsStoredWrongKeyAndZeroMaximumAndUnsafeOriginalCannotFallThrough()
    {
        using var fixture = new Fixture();
        var unit = fixture.Unit("druzhina"); unit.SetUnitKey("HEP_steppe_lancers"); unit.SetMax(0);
        var army = new Army("1", "attacker", true); army.Units.Add(unit);
        fixture.Invoke("RefreshNativeMappings", army);
        Assert.Equal("cha_gen_armoured_sword", unit.GetAttilaUnitKey()); Assert.True(unit.GetMax() > 0);
        var newNative = fixture.Unit("order_knights");
        Assert.Equal("cha_gen_noble_cav", fixture.Map(newNative));
        Assert.True((int)fixture.Invoke("GetMax", newNative)! > 0);
        fixture.SetUnsafeCavalryDatabase();
        // A wrong cavalry mapping for native FOOT can still be replaced with a verified
        // foot template; the original horse body's unsupported HP is irrelevant to that repair.
        Assert.Equal("cha_gen_armoured_sword", fixture.Map(fixture.Unit("druzhina")));
        var unsafeUnit = fixture.Unit("armored_horsemen");
        var error = Assert.Throws<TargetInvocationException>(() => fixture.Map(unsafeUnit));
        Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Contains("unsupported-body", error.InnerException!.Message);
    }

    [InstalledDefaultMapperFact]
    public void EditedSameFilenameInvalidatesCandidatesBeforeGetMaxAndKeepsCustomChoice()
    {
        using var fixture = new Fixture(); var unit = fixture.Unit("druzhina");
        Assert.Equal("cha_gen_armoured_sword", fixture.Map(unit));
        var xml = new XmlDocument(); xml.Load(fixture.MapperFile);
        var node = (XmlElement)xml.SelectSingleNode("/Factions/Faction[@name='Latin']/MenAtArm[@type='druzhina']")!;
        node.SetAttribute("key", "custom_authored_horse"); node.SetAttribute("max", "37"); xml.Save(fixture.MapperFile);
        // GetMax runs before any GetUnitKey/DOM refresh here: it must not trust old review fingerprints.
        Assert.Equal(37, (int)fixture.Invoke("GetMax", unit)!);
        Assert.Equal("custom_authored_horse", fixture.Map(unit));
        Assert.Equal("protected", fixture.Decision(unit).Status);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Type mapper = typeof(AvailableUnit).Assembly.GetType("CrusaderWars.unit_mapper.UnitMappers_BETA")!;
        private readonly Dictionary<PropertyInfo, object?> state = new();
        private readonly FieldInfo definitions = typeof(ArmiesReader).GetField("nativeMaaDefinitions", BindingFlags.NonPublic | BindingFlags.Static)!;
        private readonly object oldDefinitions;
        private readonly Dictionary<(string, bool), (string, bool)> oldManual;
        private readonly Dictionary<string, (string replacementKey, bool isSiege)> oldAutofix;
        private readonly string oldDirectory = Environment.CurrentDirectory;
        private readonly string scratch = Path.Combine(Path.GetTempPath(), "cc-semantic-resolver-" + Guid.NewGuid().ToString("N"));
        public string MapperFile { get; }

        public Fixture()
        {
            Directory.CreateDirectory(Path.Combine(scratch, "Factions")); Directory.CreateDirectory(Path.Combine(scratch, "data"));
            Environment.CurrentDirectory = scratch;
            MapperFile = Path.Combine(scratch, "Factions", "OfficialCC_DefaultCK3_EarlyMedieval_Factions.xml");
            File.Copy(Path.Combine(DefaultCk3OverlayScopeTests.InstalledMapper, "Factions", Path.GetFileName(MapperFile)), MapperFile);
            oldDefinitions = definitions.GetValue(null)!;
            oldManual = BattleState.ManualUnitReplacements; BattleState.ManualUnitReplacements = new();
            oldAutofix = new(BattleProcessor.AutofixReplacements); BattleProcessor.AutofixReplacements.Clear();
            Set(mapper, "LoadedUnitMapper_FolderPath", scratch); Set(mapper, "ActivePlaythroughTag", "DefaultCK3");
            // Deliberately synthetic unit-test state, never a claimed native replay.
            var export = new StatExport(); typeof(StatExport).GetProperty(nameof(StatExport.Complete))!.SetValue(export, true);
            Set(typeof(StatFidelitySession), "Enabled", true); Set(typeof(StatFidelitySession), "SaveEvidenceValidated", true);
            Set(typeof(StatFidelitySession), "Export", export); Set(typeof(StatFidelitySession), "PreparedDatabase", Prepared(false));
            definitions.SetValue(null, new Dictionary<string, NativeMaaDefinition> {
                ["druzhina"] = new("druzhina", "heavy_infantry", true, "cultural.txt"),
                ["armored_footmen"] = new("armored_footmen", "heavy_infantry", true, "00_maa_types.txt"),
                ["armored_horsemen"] = new("armored_horsemen", "heavy_cavalry", true, "00_maa_types.txt"),
                ["order_knights"] = new("order_knights", "heavy_cavalry", true, "holy_order.txt")
            });
            Invoke("ClearFactionCache");
        }

        public Unit Unit(string name)
        {
            var unit = new Unit(name, 100, new Culture("1", "norman", "heritage_frankish"), RegimentType.MenAtArms, false, new Owner("42"));
            unit.SetAttilaFaction("Latin"); return unit;
        }
        public string Map(Unit unit) => (((string, bool))Invoke("GetUnitKey", unit)!).Item1;
        public NativeMappingDecision Decision(Unit unit) => ((IReadOnlyDictionary<int, NativeMappingDecision>)mapper.GetProperty("NativeMappingDecisions")!.GetValue(null)!)[unit.UniqueID];
        public object? Invoke(string name, params object[] args) => mapper.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args);
        public void SetUnsafeCavalryDatabase() => typeof(StatFidelitySession).GetProperty("PreparedDatabase")!.SetValue(null, Prepared(true));
        private void Set(Type type, string name, object? value)
        {
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
            state[property] = property.GetValue(null); property.SetValue(null, value);
        }
        private static PreparedAttilaDatabase Prepared(bool unsafeCavalry)
        {
            var db = new AttilaDatabase();
            void Add(string name, string[] columns, params string[][] rows)
            {
                var table = new TsvTable(name, "1", columns);
                foreach (var row in rows) table.Rows.Add(columns.Select((c, i) => (c, row[i])).ToDictionary(x => x.c, x => x.Item2));
                db.Merge(table);
            }
            string[] keys = ["HEP_steppe_lancers", "cha_gen_armoured_sword", "cha_gen_noble_cav", "VISI_bellatores", "VISI_caballarii_mediogres"];
            Add("main_units_tables", ["unit", "land_unit", "num_men", "is_naval", "weight"], keys.Select(k => new[] {k,k,"80","false","heavy"}).ToArray());
            Add("land_units_tables", ["key","category","class","man_entity","bonus_hit_points","primary_melee_weapon","mount","num_mounts"],
                keys.Select(k => { bool foot = k is "cha_gen_armoured_sword" or "VISI_bellatores";
                    return new[] {k,foot?"inf_melee":"cavalry",foot?"inf_mel":"cav_mel","man","0","sword",foot?"":"mount",foot?"0":unsafeCavalry && (k=="cha_gen_noble_cav" || k=="HEP_steppe_lancers")?"55":"80"}; }).ToArray());
            Add("battle_entities_tables", ["key","type","hit_points"], ["man","man","100"],["horse","horse","50"]);
            Add("mounts_tables", ["key","entity"], ["mount","horse"]);
            Add("melee_weapons_tables", ["key","damage","ap_damage"], ["sword","10","4"]);
            Add("unit_variants_tables", ["unit","faction"], keys.Select(k => new[] {k,""}).ToArray());
            return (PreparedAttilaDatabase)typeof(PreparedAttilaDatabase).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single()
                .Invoke([db, Array.Empty<string>(), (Action)(() => { })]);
        }
        public void Dispose()
        {
            foreach (var pair in state) pair.Key.SetValue(null, pair.Value);
            definitions.SetValue(null, oldDefinitions); BattleState.ManualUnitReplacements = oldManual;
            BattleProcessor.AutofixReplacements.Clear(); foreach (var pair in oldAutofix) BattleProcessor.AutofixReplacements.Add(pair.Key, pair.Value);
            Invoke("ClearFactionCache"); Environment.CurrentDirectory = oldDirectory;
            string full = Path.GetFullPath(scratch);
            if (Path.GetDirectoryName(full) == Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)) Directory.Delete(full, true);
        }
    }
}
