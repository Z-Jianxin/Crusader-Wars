using CrusaderWars;
using CrusaderWars.client;
using System.Reflection;
using System.IO.Compression;
using System.Text;
using CrusaderWars.process;
using MoonSharp.Interpreter;
using Xunit;

public sealed class DevelopmentRuntimeTests : IDisposable
{
    private readonly string previous = Environment.CurrentDirectory;
    private readonly string scratch = Path.Combine(Path.GetTempPath(), "cc-development-tests-" + Guid.NewGuid().ToString("N"));

    public DevelopmentRuntimeTests()
    {
        Directory.CreateDirectory(scratch);
        Environment.CurrentDirectory = scratch;
    }

    [Fact]
    public async Task ExplicitMarkerSkipsUpstreamReplacementAndPreservesNormalVersions()
    {
        Assert.False(DevelopmentRuntime.Enabled);
        File.WriteAllText(DevelopmentRuntime.MarkerFileName, "{\"purpose\":\"test\"}");
        File.WriteAllText("app_version.txt", "version=\"1.4.2\"");
        File.WriteAllText("um_version.txt", "version=\"1.2.3\"");
        Assert.True(DevelopmentRuntime.Enabled);
        Assert.Contains("development", DevelopmentRuntime.VersionSuffix);
        var updater = new Updater();
        await updater.CheckAppVersion();
        await updater.CheckUnitMappersVersion();
        Assert.Equal("1.4.2", updater.AppVersion);
        Assert.Equal("1.2.3", updater.UMVersion);
        Assert.Equal("version=\"1.4.2\"", File.ReadAllText("app_version.txt"));
        Assert.False(Directory.Exists("data/updater"));
    }

    [Fact]
    public void DevelopmentResultPublicationKeepsPersonalSavesAndContinueTargetUnchanged()
    {
        File.WriteAllText(DevelopmentRuntime.MarkerFileName, "{}");
        Directory.CreateDirectory("data/save_file_data");
        File.WriteAllText("data/save_file_data/battle_results.zip", "test-output-bytes");
        File.WriteAllText("data/save_file_data/last_save.zip", "test-input-bytes");
        Directory.CreateDirectory("data/attila_battle");
        File.WriteAllText("data/attila_battle/battle_log_snippet.txt", "test-native-handoff");
        string fakeGame = Path.Combine(scratch, "fake-attila");
        Directory.CreateDirectory(Path.Combine(fakeGame, "data"));
        File.WriteAllText(Path.Combine(fakeGame, "data", "BattleResults_log.txt"), "test-native-aar");
        string personal = Path.Combine(scratch, "personal");
        Directory.CreateDirectory(Path.Combine(personal, "save games"));
        File.WriteAllText(Path.Combine(personal, "last_save.ck3"), "personal-last");
        File.WriteAllText(Path.Combine(personal, "save games", "battle_results.ck3"), "personal-result");
        File.WriteAllText(Path.Combine(personal, "continue_game.json"), "{\"title\":\"personal-save.ck3\"}");
        Type settingsType = typeof(HomePage).Assembly.GetType("CrusaderWars.Properties.Settings")!;
        object settings = settingsType.GetProperty("Default", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        var saveDirectory = settingsType.GetProperty("VAR_dir_save")!;
        object? oldDirectory = saveDirectory.GetValue(settings);
        var attilaPath = settingsType.GetProperty("VAR_attila_path")!;
        object? oldAttila = attilaPath.GetValue(settings);
        var attilaLog = settingsType.GetProperty("VAR_log_attila")!;
        object? oldAttilaLog = attilaLog.GetValue(settings);
        try
        {
            saveDirectory.SetValue(settings, Path.Combine(personal, "save games"));
            attilaPath.SetValue(settings, Path.Combine(fakeGame, "Attila.exe"));
            attilaLog.SetValue(settings, Path.Combine(fakeGame, "data", "BattleResults_log.txt"));
            Type saveFile = typeof(HomePage).Assembly.GetType("CrusaderWars.data.save_file.SaveFile")!;
            saveFile.GetMethod("Finish", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null);
            string created = Assert.Single(Directory.GetFiles("results", "battle_results.ck3", SearchOption.AllDirectories));
            Assert.Equal("test-output-bytes", File.ReadAllText(created));
            Assert.Equal("personal-last", File.ReadAllText(Path.Combine(personal, "last_save.ck3")));
            Assert.Equal("personal-result", File.ReadAllText(Path.Combine(personal, "save games", "battle_results.ck3")));
            Assert.Equal("{\"title\":\"personal-save.ck3\"}", File.ReadAllText(Path.Combine(personal, "continue_game.json")));
            Assert.True(File.Exists("data/save_file_data/last_save.zip"));
            Assert.True(File.Exists("data/save_file_data/battle_results.zip"));
            string resultDirectory = Path.GetDirectoryName(created)!;
            File.WriteAllText("data/save_file_data/last_save.zip", "next-input");
            File.WriteAllText("data/attila_battle/battle_log_snippet.txt", "next-handoff");
            File.WriteAllText(Path.Combine(fakeGame, "data", "BattleResults_log.txt"), "next-aar");
            Assert.Equal("test-input-bytes", File.ReadAllText(Path.Combine(resultDirectory, "native-input.ck3")));
            Assert.Equal("test-native-handoff", File.ReadAllText(Path.Combine(resultDirectory, "native-handoff.txt")));
            Assert.Equal("test-native-aar", File.ReadAllText(Path.Combine(resultDirectory, "native-attila-result.txt")));
        }
        finally { saveDirectory.SetValue(settings, oldDirectory); attilaPath.SetValue(settings, oldAttila); attilaLog.SetValue(settings, oldAttilaLog); }
    }

    [Fact]
    public void NativeSaveExtractionSupportsPlaintextAndSkipsIncidentalPkBeforeZip()
    {
        Type saveFile = typeof(HomePage).Assembly.GetType("CrusaderWars.data.save_file.SaveFile")!;
        var extract = saveFile.GetMethod("ExtractGamestate", BindingFlags.Public | BindingFlags.Static)!;
        const string gamestate = "meta_data={\nname=\"PK unrelated text\"\n}\nliving={}\n";
        string plain = Path.Combine(scratch, "plain.ck3"); File.WriteAllText(plain, gamestate);
        string output = Path.Combine(scratch, "plain-result");
        extract.Invoke(null, new object[] { plain, output });
        Assert.Equal(gamestate, File.ReadAllText(Path.Combine(output, "gamestate")));
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, true))
        {
            using var writer = new StreamWriter(archive.CreateEntry("gamestate").Open());
            writer.Write(gamestate);
        }
        string compressed = Path.Combine(scratch, "compressed.ck3");
        File.WriteAllBytes(compressed, Encoding.UTF8.GetBytes("SAV0102\nmeta_data={\nname=\"PK before ZIP\"\n}\n").Concat(zip.ToArray()).ToArray());
        string zipOutput = Path.Combine(scratch, "zip-result");
        extract.Invoke(null, new object[] { compressed, zipOutput });
        Assert.Equal(gamestate, File.ReadAllText(Path.Combine(zipOutput, "gamestate")));
    }

    [Fact]
    public void NativeUnicodeLfPrefixSurvivesRepackAndCrLfLengthCorruptionIsRejected()
    {
        Type saveFile = typeof(HomePage).Assembly.GetType("CrusaderWars.data.save_file.SaveFile")!;
        var read = saveFile.GetMethod("ReadMetaData", BindingFlags.NonPublic | BindingFlags.Static)!;
        var compress = saveFile.GetMethod("Compress", BindingFlags.NonPublic | BindingFlags.Static)!;
        Directory.CreateDirectory("data/save_file_data");
        byte[] metadata = Encoding.UTF8.GetBytes("meta_data={\n\tmeta_player_name=\"英国哈罗德\"\n}\n");
        byte[] prefix = Encoding.ASCII.GetBytes("SAV010214dd10a0" + metadata.Length.ToString("x8") + "\n").Concat(metadata).ToArray();
        using var payload = new MemoryStream();
        using (var zip = new ZipArchive(payload, ZipArchiveMode.Create, true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("gamestate").Open());
            writer.Write("living={}\n");
        }
        string input = Path.Combine(scratch, "native-prefix.ck3");
        File.WriteAllBytes(input, prefix.Concat(payload.ToArray()).ToArray());
        read.Invoke(null, new object[] { input });
        Assert.Equal(prefix, File.ReadAllBytes("data/save_file_data/metadata.txt"));
        File.WriteAllText("data/save_file_data/gamestate", "living={1={}}\n");
        compress.Invoke(null, null);
        byte[] result = File.ReadAllBytes("data/save_file_data/battle_results.zip");
        Assert.Equal(prefix, result.Take(prefix.Length).ToArray());
        Assert.Equal(new byte[] { 0x50, 0x4b, 3, 4 }, result.Skip(prefix.Length).Take(4).ToArray());
        File.Delete("data/save_file_data/battle_results.zip");
        File.WriteAllBytes("data/save_file_data/metadata.txt", Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(prefix).Replace("\n", "\r\n")));
        var error = Assert.Throws<TargetInvocationException>(() => compress.Invoke(null, null));
        Assert.IsType<InvalidDataException>(error.InnerException);
        Assert.False(File.Exists("data/save_file_data/battle_results.zip"));
    }

    [Fact]
    public void DevelopmentBattleSkipsOsSuspensionButKeepsResumeRecoveryAndReleaseBehavior()
    {
        var controllerField = typeof(HomePage.ProcessCommands).GetField("_controller", BindingFlags.NonPublic | BindingFlags.Static)!;
        object? previousController = controllerField.GetValue(null);
        var recorder = new RecordingProcessController();
        try
        {
            HomePage.ProcessCommands.Initialize(recorder);
            HomePage.ProcessCommands.SuspendProcess();
            Assert.Equal(new[] { "ck3.exe" }, recorder.Suspended);
            File.WriteAllText(DevelopmentRuntime.MarkerFileName, "{}");
            HomePage.ProcessCommands.SuspendProcess();
            Assert.Single(recorder.Suspended);
            HomePage.ProcessCommands.ResumeProcess();
            Assert.Equal(new[] { "ck3.exe" }, recorder.Resumed);
            File.Delete(DevelopmentRuntime.MarkerFileName);
            HomePage.ProcessCommands.SuspendProcess();
            Assert.Equal(2, recorder.Suspended.Count);
        }
        finally { controllerField.SetValue(null, previousController); }
    }

    private sealed class RecordingProcessController : IProcessController
    {
        public bool IsSupported => true;
        public List<string> Suspended { get; } = new();
        public List<string> Resumed { get; } = new();
        public void SuspendProcess(string processName) => Suspended.Add(processName);
        public void ResumeProcess(string processName) => Resumed.Add(processName);
    }

    [Theory]
    [InlineData(true, 10, 2, 0)]
    [InlineData(false, 10, 0, 2)]
    [InlineData(true, 1000, 0, 2)]
    public void ExplicitRegistryUsesVisibleAttacksOrNormalApproachWhenGlobalEnumerationIsEmpty(bool visible, int distance, int attacks, int moves)
    {
        File.WriteAllText(DevelopmentRuntime.MarkerFileName, "{}");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "test-battle-control.lua"), "test-battle-control.lua");
        string folder = "data/battle files/script/tut_tutorial_battle";
        Directory.CreateDirectory(folder);
        string output = Path.Combine(folder, "tut_start.lua");
        File.WriteAllText(output, "");
        var names = new[] { "0_attacker_army1_TYPEbowmen_1_CULTURE7_0", "0_defender_army2_TYPEcommander20_2_CULTURE8_0", "1_defender_army2_TYPEhuscarl_3_CULTURE8_0" };
        BattleScript.AppendDevelopmentController(names.Select(name => (name, "UNIT" + name)).ToList());
        string emitted = File.ReadAllText(output);
        Assert.DoesNotContain("@@CC_TRIAL_UNITS@@", emitted);
        const string prelude = """
            events = {}; logs = {}; calls = { take=0, attack=0, halt=0, move=0 }
            dev = { log=function(message) table.insert(logs,message) end }
            bm = {
              register_phase_change_callback=function(self,phase,callback) events[phase]=callback end,
              callback=function(self,callback,delay) if delay==1000 then callback() end end
            }
            function make_unit(x)
              return {
                alliance={},
                unit={ number_of_men_alive=function() return 10 end, is_routing=function() return false end,
                  is_visible_to_alliance=function() return visible end,
                  position=function() return {get_x=function() return x end,get_z=function() return 0 end} end },
                uc={ take_control=function() calls.take=calls.take+1 end, halt=function() calls.halt=calls.halt+1 end,
                  goto_location=function(self,position,run) assert(run); calls.move=calls.move+1 end,
                  attack_unit=function() calls.attack=calls.attack+1 end }
              }
            end
            """;
        var lua = new Script();
        string code = "visible=" + visible.ToString().ToLowerInvariant() + "\n" + prelude + "\n" +
            string.Join("\n", names.Select((name, index) => "UNIT" + name + "=make_unit(" + (index == 0 ? 0 : distance) + ")")) +
            "\n_G={}\nmath.huge=nil\nmath.sqrt=nil\n" + emitted + "\nevents.Deployed()\n";
        lua.DoString(code);
        var logs = lua.Globals.Get("logs").Table.Values.Select(v => v.String).ToArray();
        Assert.True(!logs.Any(line => line.Contains("|ERROR|")), string.Join("\n", logs));
        Assert.Equal(3, lua.Globals.Get("calls").Table.Get("take").Number);
        Assert.Equal(attacks, lua.Globals.Get("calls").Table.Get("attack").Number);
        Assert.Equal(moves, lua.Globals.Get("calls").Table.Get("move").Number);
        Assert.Equal(1, lua.Globals.Get("calls").Table.Get("halt").Number);
        Assert.Contains("CC_ACTUAL_TRIAL|START|1|2", logs);
        Assert.DoesNotContain(logs, line => line.Contains("|ERROR|"));
        File.Delete(DevelopmentRuntime.MarkerFileName);
        File.WriteAllText(output, "release-output");
        BattleScript.AppendDevelopmentController(names.Select(name => (name, "UNIT" + name)).ToList());
        Assert.Equal("release-output", File.ReadAllText(output));
    }

    public void Dispose()
    {
        Environment.CurrentDirectory = previous;
        if (Path.GetFullPath(scratch).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(scratch, true);
    }
}
