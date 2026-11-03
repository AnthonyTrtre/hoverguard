namespace Hoverguard.Tests;

public class DetectionTests
{
    static readonly DateTime T0 = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

    static string? Agent(string ligne)
    {
        var args = ReglesDetection.Decouper(ligne);
        return ReglesDetection.Agent(args[0], args);
    }

    [Theory]
    [InlineData(@"C:\Users\anna\.local\bin\claude.exe", "Claude")]
    [InlineData(@"C:\Users\anna\AppData\Roaming\Claude\claude-code\2.1.0\claude.exe", "Claude")]
    [InlineData(@"C:\Program Files\nodejs\node_modules\@openai\codex\vendor\x86_64-pc-windows-msvc\codex\codex.exe", "Codex")]
    [InlineData(@"C:\tools\Cursor-Agent.EXE", "Cursor")]
    [InlineData(@"C:\Users\anna\.local\bin\aider.exe", "Aider")]
    [InlineData(@"C:\Users\anna\.local\bin\kiro-cli.exe", "Kiro")]
    [InlineData("opencode.exe", "OpenCode")]
    [InlineData("goose.exe", "Goose")]
    [InlineData("crush", "Crush")]
    [InlineData("openhands.exe", "OpenHands")]
    [InlineData("amp.exe", "Amp")]
    [InlineData("droid.exe", "Droid")]
    public void ExecutablesDAgents(string exe, string attendu)
        => Assert.Equal(attendu, ReglesDetection.Agent(exe, [exe]));

    [Theory]
    [InlineData(@"C:\Windows\explorer.exe")]
    [InlineData(@"C:\Program Files\Microsoft VS Code\Code.exe")]
    [InlineData(@"C:\Program Files\Git\bin\bash.exe")]
    [InlineData("claudette.exe")]
    [InlineData("codex-helper.exe")]
    public void AutresExecutables(string exe) => Assert.Null(ReglesDetection.Agent(exe, [exe]));

    [Theory]
    [InlineData(@"C:\Users\anna\AppData\Local\AnthropicClaude\app-0.14.10\claude.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\Claude_0.14.10.0_x64__pzs8sxrjxfjjc\app\claude.exe")]
    [InlineData(@"C:\Program Files\WindowsApps\Claude_0.14.10.0_x64__pzs8sxrjxfjjc\claude.exe")]
    public void LAppDeBureauClaudeNestPasUnAgent(string exe)
    {
        Assert.True(ReglesDetection.EstAppClaude(exe));
        Assert.Null(ReglesDetection.Agent(exe, [exe, "--type=renderer"]));
    }

    [Theory]
    [InlineData(@"""C:\Program Files\nodejs\node.exe"" ""C:\Users\anna\AppData\Roaming\npm\node_modules\@openai\codex\bin\codex.js""", "Codex")]
    [InlineData(@"node  C:\Users\anna\AppData\Roaming\npm\node_modules\@google\gemini-cli\dist\index.js", "Gemini")]
    [InlineData(@"""C:\Program Files\nodejs\node.exe"" --no-warnings=DEP0040 ""C:\Users\anna\AppData\Roaming\npm\node_modules\@anthropic-ai\claude-code\cli.js"" --resume", "Claude")]
    [InlineData(@"node.exe C:\Users\anna\AppData\Roaming\npm\node_modules\@qwen-code\qwen-code\cli.js", "Qwen")]
    [InlineData(@"node.exe C:\Users\anna\AppData\Roaming\npm\node_modules\@sourcegraph\amp\dist\main.js", "Amp")]
    [InlineData(@"node /usr/lib/node_modules/@google/gemini-cli/dist/index.js", "Gemini")]
    [InlineData(@"bun.exe C:\Users\anna\.bun\bin\opencode.js", "OpenCode")]
    [InlineData(@"python.exe -m aider --model sonnet", "Aider")]
    [InlineData(@"C:\Python312\python3.12.exe C:\Python312\Scripts\aider-script.py", null)]
    [InlineData(@"C:\Python312\pythonw.exe C:\Python312\Scripts\aider.py", "Aider")]
    [InlineData(@"py -3.12 -m openhands", "OpenHands")]
    [InlineData(@"uv.exe tool run aider", "Aider")]
    [InlineData(@"uvx.exe goose", "Goose")]
    [InlineData(@"""C:\Program Files\nodejs\node.exe"" C:\projets\site\server.js", null)]
    [InlineData(@"node.exe C:\projets\amp\index.js", null)]
    [InlineData(@"python.exe -m http.server", null)]
    [InlineData(@"node.exe", null)]
    public void LignesDeCommandeDInterpretes(string ligne, string? attendu) => Assert.Equal(attendu, Agent(ligne));

    [Fact]
    public void LesArgumentsNeComptentPasPourUnExecutableOrdinaire()
        => Assert.Null(ReglesDetection.Agent(@"C:\Windows\System32\notepad.exe", ["notepad.exe", "codex.js"]));

    [Theory]
    [InlineData("node.exe", true)]
    [InlineData(@"C:\x\Python311.exe", true)]
    [InlineData("claude.exe", true)]
    [InlineData("svchost.exe", false)]
    public void TriRapide(string exe, bool attendu) => Assert.Equal(attendu, ReglesDetection.MeriteExamen(exe));

    [Fact]
    public void DecoupageCommeWindows()
    {
        Assert.Equal([@"C:\Program Files\nodejs\node.exe", @"C:\a b\c.js", "-x"],
            ReglesDetection.Decouper(@"""C:\Program Files\nodejs\node.exe"" ""C:\a b\c.js"" -x"));
        Assert.Equal(["prog", @"a\\b", "c\"d", @"e\", "f g"], ReglesDetection.Decouper(@"prog a\\b c\""d ""e\\"" ""f g"""));
        Assert.Equal(["prog", "a\"b"], ReglesDetection.Decouper("prog \"a\"\"b\""));
        Assert.Equal([@"C:\outil\x.exe"], ReglesDetection.Decouper(@"C:\outil\x.exe   "));
        Assert.Empty(ReglesDetection.Decouper(null));
        Assert.Empty(ReglesDetection.Decouper(""));
    }

    [Theory]
    [InlineData(0.30, 10, true)]   // 3 % d'un cœur : pile le seuil
    [InlineData(0.29, 10, false)]
    [InlineData(2.0, 10, true)]
    [InlineData(0.0, 10, false)]
    [InlineData(10.0, 130, false)]  // mesure précédente trop vieille (veille)
    [InlineData(1.0, 0, false)]
    public void SeuilDActivite(double cpuSecondes, double intervalleSecondes, bool attendu)
        => Assert.Equal(attendu, ReglesDetection.EstActif(TimeSpan.FromSeconds(cpuSecondes), TimeSpan.FromSeconds(intervalleSecondes)));

    [Fact]
    public void Pourcentage() =>
        Assert.Equal(50, ReglesDetection.Pourcentage(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)), 6);

    static ProcessusVu P(int pid, int parent, double cpu, string? agent = null, long creation = 0)
        => new(pid, parent, creation, TimeSpan.FromSeconds(cpu), agent);

    [Fact]
    public void ActiviteDeLArbreEntier()
    {
        var m = new MesureActivite();
        // Claude (100) lance une compilation (101 → 102) ; Codex (200) ne fait rien.
        var premier = m.Relever([P(1, 0, 50), P(100, 1, 1, "Claude"), P(101, 100, 0), P(102, 101, 0), P(200, 1, 3, "Codex")], T0);
        Assert.All(premier, a => Assert.False(a.Actif));  // premier relevé : référence seulement
        // Claude : 0,61 s sur 10 s (6 %), surtout dans ses descendants ; Codex : 0,5 s (5 %).
        var second = m.Relever([P(1, 0, 90), P(100, 1, 1.01, "Claude"), P(101, 100, 0.1), P(102, 101, 0.5), P(200, 1, 3.5, "Codex")],
            T0.AddSeconds(10));
        Assert.Equal([new AgentDetecte(100, "Claude", true), new AgentDetecte(200, "Codex", true)], second);
        var troisieme = m.Relever([P(100, 1, 1.02, "Claude"), P(200, 1, 3.5, "Codex")], T0.AddSeconds(20));
        // Les descendants terminés font baisser le total : jamais d'écart négatif, et ici sous le seuil.
        Assert.Equal([new AgentDetecte(100, "Claude", false), new AgentDetecte(200, "Codex", false)], troisieme);
    }

    [Fact]
    public void UnAgentLanceParUnAgentDuMemeNomNeCompteQuUneFois()
    {
        var m = new MesureActivite();
        // node codex.js (10) → codex.exe (11) → codex.exe (12, helper) ; Claude (20) lance Codex (21).
        var r = m.Relever([P(10, 1, 0, "Codex"), P(11, 10, 0, "Codex"), P(12, 11, 0, "Codex"), P(20, 1, 0, "Claude"), P(21, 20, 0, "Codex")], T0);
        Assert.Equal([10, 20, 21], r.Select(a => a.Pid));
        Assert.Equal("Claude : 0 au travail / 1 · Codex : 0 au travail / 2", ReglesDetection.LigneMenu(r));
    }

    [Fact]
    public void UnAgentDUnAutreNomGardeSonPropreArbre()
    {
        var m = new MesureActivite();
        m.Relever([P(20, 1, 0, "Claude"), P(21, 20, 0, "Codex"), P(22, 21, 0)], T0);
        var r = m.Relever([P(20, 1, 0, "Claude"), P(21, 20, 0, "Codex"), P(22, 21, 5)], T0.AddSeconds(10));
        Assert.Equal([new AgentDetecte(20, "Claude", false), new AgentDetecte(21, "Codex", true)], r);
    }

    [Fact]
    public void NumeroDeParentReutiliseIgnore()
    {
        var m = new MesureActivite();
        // 30 a été créé AVANT son « parent » 20 : son vrai parent est mort et le numéro 20 a été repris par Claude.
        m.Relever([P(20, 1, 0, "Claude", creation: 1000), P(30, 20, 0, creation: 500)], T0);
        var r = m.Relever([P(20, 1, 0, "Claude", creation: 1000), P(30, 20, 9, creation: 500)], T0.AddSeconds(10));
        Assert.False(Assert.Single(r).Actif);
    }

    [Fact]
    public void BoucleDeParentsSansBlocage()
    {
        var m = new MesureActivite();
        var r = m.Relever([P(5, 6, 0), P(6, 5, 0), P(7, 7, 0, "Aider")], T0);
        Assert.Equal([new AgentDetecte(7, "Aider", false)], r);
    }

    [Fact]
    public void NumeroReutiliseParUnNouvelAgentRepartDeZero()
    {
        var m = new MesureActivite();
        m.Relever([P(40, 1, 100, "Claude", creation: 1)], T0);
        // Même numéro, autre processus (création différente) : pas d'écart calculé contre l'ancien.
        var r = m.Relever([P(40, 1, 0.5, "Claude", creation: 2)], T0.AddSeconds(10));
        Assert.False(Assert.Single(r).Actif);
    }

    [Fact]
    public void CompteurEtResumes()
    {
        AgentDetecte[] d = [new(1, "Codex", true), new(2, "Codex", false), new(3, "Claude", true)];
        Assert.Equal(2, ReglesDetection.Principaux(1, d));
        Assert.Equal(4, ReglesDetection.Principaux(4, d));
        Assert.Equal("Claude : 1 au travail / 1 · Codex : 1 au travail / 2", ReglesDetection.LigneMenu(d));
        Assert.Null(ReglesDetection.LigneMenu([]));
        Assert.Equal("1 au travail sur 2 ouverts", ReglesDetection.Detail(1, 2));
        Assert.Equal("0 au travail sur 1 ouvert", ReglesDetection.Detail(0, 1));
    }
}
