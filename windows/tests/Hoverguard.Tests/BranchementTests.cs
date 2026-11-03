using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;

namespace Hoverguard.Tests;

public class BranchementTests
{
    static JsonObject? Hooks(JsonObject r) => r["hooks"] as JsonObject;
    static JsonObject Json(string texte) => JsonNode.Parse(texte)!.AsObject();
    static string Texte(JsonNode? n) => n!.GetValue<string>();

    /// <summary>Dossier personnel jetable pour les tests d'écriture.</summary>
    sealed class Maison : IDisposable
    {
        public string Chemin { get; } = Directory.CreateTempSubdirectory("hoverguard-tests-").FullName;
        public void Dispose() => Directory.Delete(Chemin, recursive: true);
    }

    // ── Ajout, groupes mixtes, débranchement ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void AjouteSansToucherAuResteEtSansDoublon()
    {
        var claude = BranchementAgents.Claude;
        var avant = Json("""{"model":"opus","hooks":{"Stop":[{"hooks":[{"type":"command","command":"echo fini"}]}]}}""");
        var apres = claude.Brancher(claude.Brancher(avant));
        Assert.Equal("opus", Texte(apres["model"]));
        Assert.True(claude.EstBranche(apres));
        Assert.True(claude.EstAJour(apres));
        Assert.False(claude.EstBranche(avant));
        Assert.Equal(2, ((JsonArray)Hooks(apres)!["Stop"]!).Count);  // le hook existant + celui de Hoverguard, une seule fois
        foreach (var e in claude.Evenements)
            Assert.Single((JsonArray)Hooks(apres)![e.Nom]!, g => g!["hooks"]!.AsArray().Any(BranchementAgents.EstDeHoverguard));
        Assert.Equal("Agent|Task", Texte(Hooks(apres)!["PreToolUse"]![0]!["matcher"]));
        Assert.Null(Hooks(apres)!["SubagentStop"]![0]!["matcher"]);
        Assert.Single((JsonArray)Hooks(avant)!["Stop"]!);  // l'original n'est pas modifié
    }

    const string Mixte = """
        {"hooks":{"Stop":[{"matcher":"x","hooks":[
          {"type":"command","command":"echo a moi"},
          {"type":"command","command":"C:/Windows/System32/WindowsPowerShell/v1.0/powershell.exe -EncodedCommand AAAA","note":"pas a nous"},
          {"type":"command","command":"start hoverguard://agent?etat=fini"}
        ]}]}}
        """;

    [Fact]
    public void GroupeMixteGardeAuBranchement()
    {
        var apres = BranchementAgents.Claude.Brancher(Json(Mixte));
        var stop = (JsonArray)Hooks(apres)!["Stop"]!;
        Assert.Equal(2, stop.Count);
        Assert.Equal("x", Texte(stop[0]!["matcher"]));  // le groupe de l'utilisateur, avec son matcher
        var siens = stop[0]!["hooks"]!.AsArray();
        Assert.Equal(["echo a moi", "C:/Windows/System32/WindowsPowerShell/v1.0/powershell.exe -EncodedCommand AAAA"],
            siens.Select(h => Texte(h!["command"])));
        Assert.True(BranchementAgents.EstDeHoverguard(stop[1]!["hooks"]![0]));
    }

    [Fact]
    public void DebrancherNeRetireQueHoverguard()
    {
        var claude = BranchementAgents.Claude;
        var avant = Json("""{"model":"opus","hooks":{"Stop":[{"hooks":[{"type":"command","command":"echo fini"}]}],"PostToolUse":[{"hooks":[{"type":"command","command":"echo apres"}]}]}}""");
        var branche = claude.Brancher(avant);
        Assert.True(claude.ContientHoverguard(branche));
        var debranche = claude.Debrancher(branche);
        Assert.False(claude.ContientHoverguard(debranche));
        Assert.True(JsonNode.DeepEquals(avant, debranche));  // retour exact à l'état d'origine
        Assert.True(JsonNode.DeepEquals(avant, claude.Debrancher(avant)));  // rien à retirer : rien ne change

        var mixte = claude.Debrancher(Json(Mixte));
        Assert.Equal(2, Hooks(mixte)!["Stop"]![0]!["hooks"]!.AsArray().Count);  // le groupe mixte reste, sans notre hook

        // Un hook d'une ancienne version, sur un événement qui n'est plus suivi, part aussi.
        var ancien = Json("""{"hooks":{"Ancien":[{"hooks":[{"type":"command","command":"start hoverguard://agent?etat=fini"}]}],"Autre":"pas une liste"}}""");
        Assert.True(claude.ContientHoverguard(ancien));
        var nettoye = claude.Debrancher(ancien);
        Assert.Null(Hooks(nettoye)!["Ancien"]);
        Assert.Equal("pas une liste", Texte(Hooks(nettoye)!["Autre"]));
    }

    [Fact]
    public void DebrancherCursorGardeLesHooksDeLUtilisateur()
    {
        var cursor = BranchementAgents.Cursor;
        var avant = Json("""{"version":1,"hooks":{"stop":[{"command":"./mon-script.ps1"}]}}""");
        var debranche = cursor.Debrancher(cursor.Brancher(avant));
        Assert.True(JsonNode.DeepEquals(avant, debranche));
    }

    [Fact]
    public void UneAncienneCommandeEstAMettreAJour()
    {
        var claude = BranchementAgents.Claude;
        var vieux = (JsonObject)claude.Brancher(new JsonObject()).DeepClone();
        foreach (var e in claude.Evenements)
            vieux["hooks"]![e.Nom]![0]!["hooks"]![0]!["command"] = "powershell.exe -EncodedCommand " + BranchementAgents.Encoder(claude.Script(e.Etat));
        Assert.True(claude.EstBranche(vieux));
        Assert.False(claude.EstAJour(vieux));
        var neuf = claude.Brancher(vieux);
        Assert.True(claude.EstAJour(neuf));
        Assert.Single((JsonArray)Hooks(neuf)!["Stop"]!);  // l'ancien hook est remplacé, pas doublé
    }

    // ── Formats ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FormatsCodexGeminiCursor()
    {
        var codex = BranchementAgents.Codex.Brancher(new JsonObject());
        Assert.Equal(["UserPromptSubmit", "PermissionRequest", "Stop", "SubagentStart", "SubagentStop"], Hooks(codex)!.Select(p => p.Key));
        Assert.Equal("*", Texte(Hooks(codex)!["PermissionRequest"]![0]!["matcher"]));
        Assert.Equal(10, Hooks(codex)!["Stop"]![0]!["hooks"]![0]!["timeout"]!.GetValue<int>());
        Assert.Equal("command", Texte(Hooks(codex)!["Stop"]![0]!["hooks"]![0]!["type"]));
        Assert.DoesNotContain("Out.Write", BranchementAgents.Decoder(Texte(Hooks(codex)!["UserPromptSubmit"]![0]!["hooks"]![0]!["command"])));

        var gemini = BranchementAgents.Gemini.Brancher(new JsonObject());
        Assert.Equal(["BeforeAgent", "Notification", "AfterAgent"], Hooks(gemini)!.Select(p => p.Key));
        var hookGemini = Hooks(gemini)!["AfterAgent"]![0]!["hooks"]![0]!;
        Assert.Equal("*", Texte(Hooks(gemini)!["AfterAgent"]![0]!["matcher"]));
        Assert.Equal(10000, hookGemini["timeout"]!.GetValue<int>());  // millisecondes chez Gemini
        Assert.EndsWith("[Console]::Out.Write('{}')", BranchementAgents.Decoder(Texte(hookGemini["command"])));

        var cursor = BranchementAgents.Cursor.Brancher(new JsonObject());
        Assert.Equal(1, cursor["version"]!.GetValue<int>());
        Assert.Equal(["beforeSubmitPrompt", "stop", "subagentStart", "subagentStop"], Hooks(cursor)!.Select(p => p.Key));
        var stop = Hooks(cursor)!["stop"]![0]!;
        Assert.Null(stop["hooks"]);  // forme plate
        Assert.Null(stop["type"]);
        Assert.True(BranchementAgents.EstDeHoverguard(stop));
        Assert.True(BranchementAgents.Cursor.EstBranche(cursor));
        Assert.EndsWith("""[Console]::Out.Write('{"continue":true}')""", BranchementAgents.Decoder(Texte(Hooks(cursor)!["beforeSubmitPrompt"]![0]!["command"])));
        Assert.Equal(2, BranchementAgents.Cursor.Brancher(Json("""{"version":2}"""))["version"]!.GetValue<int>());  // version existante gardée
    }

    [Fact]
    public void CommandeAbsolueSansGuillemetsNiVariablesPourToutesLesConsoles()
    {
        foreach (var outil in BranchementAgents.Tous)
            foreach (var e in outil.Evenements)
            {
                var c = outil.Commande(e.Etat, e.Sortie, dossierWindows: @"D:\WINDOWS");
                Assert.StartsWith("D:/WINDOWS/System32/WindowsPowerShell/v1.0/powershell.exe -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ", c);
                foreach (var interdit in "\"'$%\\`^&|<>;()")
                    Assert.DoesNotContain(interdit, c);
                var script = BranchementAgents.Decoder(c);
                Assert.Contains($"&etat={e.Etat.Texte()}')", script);
                Assert.Contains($"\"{outil.CleSession}\"", script);
                Assert.Contains("Start-Process", script);
                Assert.Contains("{1,4}", script);  // 4 caractères de l'identifiant, pas plus
                Assert.DoesNotContain("verrouiller", script);
                Assert.Equal(e.Sortie.Length > 0, script.Contains("Out.Write"));
            }
    }

    [Theory]
    [InlineData(null, "C:/Windows/System32/WindowsPowerShell/v1.0/powershell.exe")]  // hors de Windows : emplacement standard
    [InlineData(@"C:\Windows\", "C:/Windows/System32/WindowsPowerShell/v1.0/powershell.exe")]
    [InlineData(@"E:\Win", "E:/Win/System32/WindowsPowerShell/v1.0/powershell.exe")]
    [InlineData(@"C:\Mon Windows", "C:/Windows/System32/WindowsPowerShell/v1.0/powershell.exe")]  // espace : refusé
    [InlineData(@"\\serveur\partage", "C:/Windows/System32/WindowsPowerShell/v1.0/powershell.exe")]
    public void CheminDePowerShell(string? dossier, string attendu)
    {
        if (dossier is null && OperatingSystem.IsWindows()) return;
        Assert.Equal(attendu, BranchementAgents.CheminPowerShell(dossier));
    }

    [Fact]
    public void LeHookDUnAutreOutilNEstPasPrisPourCeluiDeHoverguard()
    {
        Assert.False(BranchementAgents.EstDeHoverguard(JsonNode.Parse("""{"command":"powershell.exe -EncodedCommand ZQBjAGgAbwA="}""")));
        Assert.False(BranchementAgents.EstDeHoverguard(JsonNode.Parse("""{"command":42}""")));
        Assert.False(BranchementAgents.EstDeHoverguard(JsonNode.Parse("""["hoverguard://"]""")));
        Assert.True(BranchementAgents.EstDeHoverguard(JsonNode.Parse("""{"command":"start hoverguard://agent?etat=fini"}""")));
    }

    // ── Lecture : fichiers refusés ───────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("{\n  // commentaire\n  \"model\": \"opus\"\n}", "commentaires")]
    [InlineData("{\"model\": \"opus\",}", "commentaires")]
    [InlineData("{ abîmé", "pas du JSON lisible")]
    [InlineData("[1, 2]", "objet JSON")]
    [InlineData("\"texte\"", "objet JSON")]
    [InlineData("null", "objet JSON")]
    [InlineData("{\"a\": 1, \"a\": 2}", "deux fois la même clé")]
    public void FichierIllisibleRefuse(string contenu, string motif)
    {
        using var maison = new Maison();
        var outil = BranchementAgents.Claude;
        Directory.CreateDirectory(Path.GetDirectoryName(outil.Fichier(maison.Chemin))!);
        File.WriteAllText(outil.Fichier(maison.Chemin), contenu);
        var erreur = Assert.Throws<FichierIllisible>(() => outil.Lire(maison.Chemin));
        Assert.Contains(motif, erreur.Message);
        Assert.Contains("Hoverguard n'y touche pas", erreur.Message);
        Assert.Contains("settings.json", erreur.Message);
        Assert.Equal(contenu, File.ReadAllText(outil.Fichier(maison.Chemin)));  // intact
        Assert.False(File.Exists(BranchementAgents.Sauvegarde(outil.Fichier(maison.Chemin))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \n")]
    public void FichierVideCommeAbsent(string contenu)
    {
        using var maison = new Maison();
        var outil = BranchementAgents.Gemini;
        Assert.Empty(outil.Lire(maison.Chemin));
        Directory.CreateDirectory(Path.GetDirectoryName(outil.Fichier(maison.Chemin))!);
        File.WriteAllText(outil.Fichier(maison.Chemin), contenu);
        Assert.Empty(outil.Lire(maison.Chemin));
    }

    // ── Écriture sûre ────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EcritAvecUneSeuleSauvegarde()
    {
        using var maison = new Maison();
        var outil = BranchementAgents.Claude;
        var fichier = outil.Fichier(maison.Chemin);
        Assert.False(outil.Present(maison.Chemin));
        Assert.Null(outil.Ecrire(maison.Chemin, outil.Brancher(outil.Lire(maison.Chemin))));  // pas de fichier : pas de copie
        Assert.True(outil.Present(maison.Chemin));
        Assert.True(outil.EstAJour(outil.Lire(maison.Chemin)));

        File.WriteAllText(fichier, "{\"model\": \"opus\", \"nom\": \"é & <b>\"}");
        var copie = outil.Ecrire(maison.Chemin, outil.Brancher(outil.Lire(maison.Chemin)));
        Assert.Equal(fichier + ".avant-hoverguard", copie);
        Assert.Equal("{\"model\": \"opus\", \"nom\": \"é & <b>\"}", File.ReadAllText(copie!));
        var relu = outil.Lire(maison.Chemin);
        Assert.Equal("opus", Texte(relu["model"]));
        Assert.Equal("é & <b>", Texte(relu["nom"]));
        Assert.Contains("é & <b>", File.ReadAllText(fichier));  // lisible, pas échappé
        Assert.True(outil.EstAJour(relu));

        outil.Ecrire(maison.Chemin, outil.Debrancher(relu));
        Assert.False(outil.ContientHoverguard(outil.Lire(maison.Chemin)));
        var dossier = Path.GetDirectoryName(fichier)!;
        Assert.Equal(["settings.json", "settings.json.avant-hoverguard"], Directory.GetFiles(dossier).Select(Path.GetFileName).Order());
        Assert.True(outil.EstAJour(Json(File.ReadAllText(copie!))));  // la copie remplacée : l'état juste avant
    }

    [Fact]
    public void EcritDansLaCibleDUnLienSymbolique()
    {
        using var maison = new Maison();
        var outil = BranchementAgents.Codex;
        var depot = Path.Combine(maison.Chemin, "dotfiles");
        Directory.CreateDirectory(depot);
        var vrai = Path.Combine(depot, "codex-hooks.json");
        File.WriteAllText(vrai, "{\"hooks\": {}}");
        Directory.CreateDirectory(Path.Combine(maison.Chemin, ".codex"));
        var lien = outil.Fichier(maison.Chemin);
        try { File.CreateSymbolicLink(lien, Path.Combine("..", "dotfiles", "codex-hooks.json")); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return; }  // Windows sans le droit de créer des liens

        outil.Ecrire(maison.Chemin, outil.Brancher(outil.Lire(maison.Chemin)));
        Assert.NotNull(new FileInfo(lien).LinkTarget);  // le lien est toujours un lien
        Assert.True(outil.EstAJour(Json(File.ReadAllText(vrai))));  // la cible a reçu les hooks
        Assert.True(File.Exists(vrai + ".avant-hoverguard"));  // copie à côté de la cible
        Assert.False(File.Exists(lien + ".avant-hoverguard"));
    }

    [Fact]
    public void GardeLesDroitsDOrigine()
    {
        using var maison = new Maison();
        var outil = BranchementAgents.Cursor;
        var fichier = outil.Fichier(maison.Chemin);
        Directory.CreateDirectory(Path.GetDirectoryName(fichier)!);
        File.WriteAllText(fichier, "{}");
        if (OperatingSystem.IsWindows())
        {
            // Une règle explicite propre à ce fichier doit survivre au remplacement.
            var acl = new FileInfo(fichier).GetAccessControl();
            var invites = new SecurityIdentifier(WellKnownSidType.BuiltinGuestsSid, null);
            acl.AddAccessRule(new FileSystemAccessRule(invites, FileSystemRights.Read, AccessControlType.Deny));
            new FileInfo(fichier).SetAccessControl(acl);
            outil.Ecrire(maison.Chemin, outil.Brancher(outil.Lire(maison.Chemin)));
            Assert.True(RefusAuxInvites(fichier, invites));
            Assert.True(RefusAuxInvites(BranchementAgents.Sauvegarde(fichier), invites));
        }
        else
        {
            File.SetUnixFileMode(fichier, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            outil.Ecrire(maison.Chemin, outil.Brancher(outil.Lire(maison.Chemin)));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, File.GetUnixFileMode(fichier));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, File.GetUnixFileMode(BranchementAgents.Sauvegarde(fichier)));
            // Nouveau fichier : lisible par l'utilisateur seul (il peut contenir des clés).
            var autre = BranchementAgents.Claude;
            autre.Ecrire(maison.Chemin, autre.Brancher(new JsonObject()));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(autre.Fichier(maison.Chemin)));
        }
        Assert.True(outil.EstAJour(outil.Lire(maison.Chemin)));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static bool RefusAuxInvites(string fichier, SecurityIdentifier invites)
        => new FileInfo(fichier).GetAccessControl().GetAccessRules(true, false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().Any(r => r.IdentityReference == invites && r.AccessControlType == AccessControlType.Deny);

    // ── Exécution réelle (Windows seulement, vérifiée par la CI Windows) ─────────────────────────────────────────

    const string GitBash = @"C:\Program Files\Git\bin\bash.exe";

    /// <summary>Lance la commande complète du hook, telle qu'écrite dans le fichier, à travers une console
    /// (<c>cmd</c>, <c>powershell</c> ou <c>bash</c>), avec <c>Write-Output</c> à la place de <c>Start-Process</c> pour
    /// voir l'URL produite sans rien ouvrir. Renvoie la sortie standard, ou null hors de Windows.</summary>
    static string? Sortie(string console, BranchementAgents outil, EtatAgent etat, string entree, string sortie = "")
    {
        if (!OperatingSystem.IsWindows()) return null;
        var commande = outil.Commande(etat, sortie, lanceur: "Write-Output");
        var info = console switch
        {
            "cmd" => new ProcessStartInfo("cmd.exe") { ArgumentList = { "/d", "/s", "/c", commande } },
            "powershell" => new ProcessStartInfo(BranchementAgents.CheminPowerShell().Replace('/', '\\')) { ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", commande } },
            _ => new ProcessStartInfo(GitBash) { ArgumentList = { "-c", commande } },
        };
        info.RedirectStandardInput = info.RedirectStandardOutput = info.RedirectStandardError = true;
        info.UseShellExecute = false;
        using var p = Process.Start(info)!;
        p.StandardInput.Write(entree);
        p.StandardInput.Close();
        var erreurs = p.StandardError.ReadToEndAsync();
        var texte = p.StandardOutput.ReadToEnd();
        Assert.True(p.WaitForExit(60_000), $"{console} : délai dépassé");
        Assert.True(p.ExitCode == 0, $"{console} : code {p.ExitCode}, erreurs : {erreurs.Result}");
        return texte.Replace("\r", "").Replace("\n", "");
    }

    public static TheoryData<string> Consoles => new() { "cmd", "powershell", "bash" };

    [Theory]
    [MemberData(nameof(Consoles))]
    public void LaCommandeProduitLaBonneUrl(string console)
    {
        if (console == "bash" && !File.Exists(GitBash)) return;
        var url = Sortie(console, BranchementAgents.Claude, EtatAgent.Travail, """{"session_id": "a1b2c3d4-xx", "hook_event_name": "UserPromptSubmit"}""");
        if (url is null) return;  // PowerShell n'existe que sous Windows : vérifié par la CI Windows
        Assert.Equal("hoverguard://agent?nom=Claude%20a1b2&etat=travail", url);
        Assert.Equal("hoverguard://agent?nom=Codex%20ff00&etat=sous-agent-debut",
            Sortie(console, BranchementAgents.Codex, EtatAgent.SousAgentDebut, """{"session_id":"ff00ee","hook_event_name":"SubagentStart"}"""));
        Assert.Equal("hoverguard://agent?nom=Cursor%20zz99&etat=fini{}",
            Sortie(console, BranchementAgents.Cursor, EtatAgent.Fini, """{"conversation_id":"zz99yy","status":"completed"}""", "{}"));
        Assert.Equal("hoverguard://agent?nom=Gemini&etat=fini{}", Sortie(console, BranchementAgents.Gemini, EtatAgent.Fini, "pas du json", "{}"));
    }

    [Fact]
    public void LeHookNEcritRienSurLaSortieSaufLeJsonExige()
    {
        // « [void] » à la place de Start-Process : le reste du script ne doit rien écrire (Codex met la sortie de
        // UserPromptSubmit dans le contexte du modèle). On n'ouvre pas la vraie URL sur le runner.
        if (!OperatingSystem.IsWindows()) return;
        foreach (var outil in BranchementAgents.Tous)
            foreach (var e in outil.Evenements)
            {
                var info = new ProcessStartInfo("cmd.exe") { ArgumentList = { "/d", "/s", "/c", outil.Commande(e.Etat, e.Sortie, lanceur: "[void]") } };
                info.RedirectStandardInput = info.RedirectStandardOutput = true;
                info.UseShellExecute = false;
                using var p = Process.Start(info)!;
                p.StandardInput.Write("""{"session_id":"abcd","conversation_id":"abcd"}""");
                p.StandardInput.Close();
                var texte = p.StandardOutput.ReadToEnd();
                Assert.True(p.WaitForExit(60_000));
                Assert.Equal(e.Sortie, texte);
            }
    }
}
