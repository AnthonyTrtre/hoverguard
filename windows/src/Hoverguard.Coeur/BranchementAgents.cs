using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hoverguard.Coeur;

/// <summary>
/// Branche les agents en ligne de commande à Hoverguard par leurs « hooks » : chaque début de travail, attente, fin et
/// sous-agent lance <c>Start-Process "hoverguard://agent?…"</c> dans PowerShell. Ne touche qu'aux hooks de Hoverguard ;
/// une copie de sauvegarde du fichier est faite avant d'écrire. Formats repris de la version macOS
/// (<c>macos/Sources/BranchementAgents.swift</c>), vérifiés dans les documentations officielles le 2026-11-03
/// (docs.claude.com, learn.chatgpt.com/docs/hooks, geminicli.com/docs/hooks, cursor.com/docs/hooks).
/// <para>
/// La commande d'un hook passe, selon l'outil et sa version, par cmd.exe, PowerShell ou Git Bash (Claude Code sous
/// Windows). Chacun a ses propres règles de guillemets, de <c>$</c>, de <c>%VAR%</c> et de barres obliques inverses.
/// La commande n'utilise donc ni guillemets, ni variables, ni <c>\</c> : PowerShell est appelé par son chemin absolu
/// écrit avec des <c>/</c> (compris par les trois), et le script lui est passé encodé en Base64
/// (<c>-EncodedCommand</c>), qui ne contient aucun caractère spécial.
/// </para>
/// </summary>
public sealed record BranchementAgents(
    string Nom,
    /// Nom court montré sur le voile, suivi de 4 caractères de la session (« Claude a1b2 »).
    string Etiquette,
    /// Chemin relatif au dossier de l'utilisateur.
    string FichierRelatif,
    /// Délai maximal du hook, dans l'unité de l'outil (secondes, millisecondes pour Gemini CLI).
    int Delai,
    BranchementAgents.Forme Disposition,
    /// Champ de l'entrée standard qui identifie la session (4 caractères affichés sur le voile).
    string CleSession,
    IReadOnlyList<BranchementAgents.Evenement> Evenements)
{
    public enum Forme
    {
        /// <summary><c>{"hooks": {Evenement: [{"matcher"?, "hooks": [{type, command}]}]}}</c> (Claude Code, Codex, Gemini CLI).</summary>
        Groupes,
        /// <summary><c>{"version": 1, "hooks": {evenement: [{command}]}}</c> (Cursor).</summary>
        Plate,
    }

    /// <param name="Sortie">JSON à écrire sur la sortie standard (certains outils l'exigent), vide sinon.</param>
    public sealed record Evenement(string Nom, EtatAgent Etat, string? Matcher = null, string Sortie = "");

    // PowerShell démarre en 0,3 à 1,5 s : délais plus larges que sur macOS (5 s).
    public static readonly BranchementAgents Claude = new(
        "Claude Code", "Claude", Path.Combine(".claude", "settings.json"), 10, Forme.Groupes, "session_id",
        [
            new("UserPromptSubmit", EtatAgent.Travail), new("Notification", EtatAgent.Attente), new("Stop", EtatAgent.Fini),
            new("PreToolUse", EtatAgent.SousAgentDebut, "Agent|Task"), new("SubagentStop", EtatAgent.SousAgentFin),
        ]);

    /// <summary>Codex demande d'approuver les nouveaux hooks une fois (<c>/hooks</c> dans Codex). Sa sortie standard sur
    /// UserPromptSubmit part dans le contexte du modèle : la commande ne doit rien écrire.</summary>
    public static readonly BranchementAgents Codex = new(
        "Codex", "Codex", Path.Combine(".codex", "hooks.json"), 10, Forme.Groupes, "session_id",
        [
            new("UserPromptSubmit", EtatAgent.Travail), new("PermissionRequest", EtatAgent.Attente, "*"), new("Stop", EtatAgent.Fini),
            new("SubagentStart", EtatAgent.SousAgentDebut), new("SubagentStop", EtatAgent.SousAgentFin),
        ]);

    /// <summary>Gemini CLI attend un objet JSON (et rien d'autre) sur la sortie standard ; délai en millisecondes.</summary>
    public static readonly BranchementAgents Gemini = new(
        "Gemini CLI", "Gemini", Path.Combine(".gemini", "settings.json"), 10000, Forme.Groupes, "session_id",
        [
            new("BeforeAgent", EtatAgent.Travail, "*", "{}"), new("Notification", EtatAgent.Attente, "*", "{}"),
            new("AfterAgent", EtatAgent.Fini, "*", "{}"),
        ]);

    /// <summary>Cursor n'a pas d'événement « attente d'approbation » : seulement travail, fin et sous-agents.</summary>
    public static readonly BranchementAgents Cursor = new(
        "Cursor", "Cursor", Path.Combine(".cursor", "hooks.json"), 10, Forme.Plate, "conversation_id",
        [
            new("beforeSubmitPrompt", EtatAgent.Travail, null, """{"continue":true}"""), new("stop", EtatAgent.Fini, null, "{}"),
            new("subagentStart", EtatAgent.SousAgentDebut, null, "{}"), new("subagentStop", EtatAgent.SousAgentFin, null, "{}"),
        ]);

    public static readonly IReadOnlyList<BranchementAgents> Tous = [Claude, Codex, Gemini, Cursor];

    public string Fichier(string dossierUtilisateur) => Path.Combine(dossierUtilisateur, FichierRelatif);

    /// <summary>L'outil semble installé (son dossier de réglages existe).</summary>
    public bool Present(string dossierUtilisateur) => Directory.Exists(Path.GetDirectoryName(Fichier(dossierUtilisateur)));

    // ── Commande ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Script PowerShell du hook, en clair : lit l'entrée standard, garde 4 caractères de l'identifiant de
    /// session, ouvre l'URL sans rien écrire, puis écrit le JSON exigé par l'outil s'il y en a un.
    /// <paramref name="lanceur"/> est remplacé dans les tests pour voir l'URL produite sans rien ouvrir.</summary>
    public string Script(EtatAgent etat, string sortie = "", string lanceur = "Start-Process")
    {
        static string Litteral(string s) => "'" + s.Replace("'", "''") + "'";
        var lignes = new List<string>
        {
            "$ErrorActionPreference='SilentlyContinue'",
            "$e=[Console]::In.ReadToEnd()",
            "$id=''",
            $"if($e -match '\"{CleSession}\"\\s*:\\s*\"([^\"]{{1,4}})'){{$id=' '+$Matches[1]}}",
            $"{lanceur} ('hoverguard://agent?nom='+[Uri]::EscapeDataString({Litteral(Etiquette)}+$id)+'&etat={etat.Texte()}')",
        };
        if (sortie.Length > 0) lignes.Add($"[Console]::Out.Write({Litteral(sortie)})");
        return string.Join("\n", lignes);
    }

    /// <summary>Chemin absolu de Windows PowerShell (présent sur tout Windows 10 et 11), avec des <c>/</c> : un
    /// <c>PATH</c> piégé ne peut pas le remplacer (audit B7), et cmd.exe, PowerShell et Git Bash le comprennent tous
    /// sans guillemets. <c>%SystemRoot%</c> n'est pas utilisé : seul cmd.exe le développe.</summary>
    public static string CheminPowerShell(string? dossierWindows = null)
    {
        var racine = dossierWindows ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        // Hors de Windows (tests) ou dossier inattendu (espace, guillemet…) : l'emplacement standard.
        if (string.IsNullOrEmpty(racine) || racine.Length < 3 || racine[1] != ':' || racine.Any(c => c is ' ' or '"' or '\'' or '%' or '$' or '&' or '^' or '`'))
            racine = @"C:\Windows";
        return racine.TrimEnd('\\', '/').Replace('\\', '/') + "/System32/WindowsPowerShell/v1.0/powershell.exe";
    }

    public const string Options = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ";

    public static string Encoder(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    /// <summary>Commande complète du hook, telle qu'écrite dans le fichier de réglages de l'outil.</summary>
    public string Commande(EtatAgent etat, string sortie = "", string lanceur = "Start-Process", string? dossierWindows = null)
        => CheminPowerShell(dossierWindows) + " " + Options + Encoder(Script(etat, sortie, lanceur));

    /// <summary>Texte clair d'une commande de hook : le script décodé si c'est une commande PowerShell encodée.</summary>
    public static string Decoder(string commande)
    {
        const string marque = "-EncodedCommand ";
        var i = commande.IndexOf(marque, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return commande;
        var code = commande[(i + marque.Length)..].Trim().Split(' ')[0];
        try { return Encoding.Unicode.GetString(Convert.FromBase64String(code)); }
        catch (FormatException) { return commande; }
    }

    // ── Réglages ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Un hook est de Hoverguard si sa commande (décodée) ouvre une URL <c>hoverguard://</c>.</summary>
    public static bool EstDeHoverguard(JsonNode? hook)
        => hook is JsonObject o && o["command"]?.GetValueKind() == JsonValueKind.String
           && Decoder(o["command"]!.GetValue<string>()).Contains("hoverguard://", StringComparison.Ordinal);

    private static JsonArray Entrees(JsonObject hooks, string evenement) => hooks[evenement] as JsonArray ?? new JsonArray();

    private bool ContientHoverguard(JsonNode? entree) => Disposition switch
    {
        Forme.Groupes => entree?["hooks"] is JsonArray l && l.Any(EstDeHoverguard),
        _ => EstDeHoverguard(entree),
    };

    /// <summary>Copie de la liste sans les hooks de Hoverguard. Un groupe qui contenait aussi un hook de l'utilisateur est
    /// gardé, avec ses autres champs (audit M6) ; un groupe vidé disparaît.</summary>
    private JsonArray SansHoverguard(JsonArray liste)
    {
        var resultat = new JsonArray();
        foreach (var entree in liste)
        {
            if (Disposition == Forme.Plate)
            {
                if (!EstDeHoverguard(entree)) resultat.Add(entree?.DeepClone());
                continue;
            }
            if (entree is not JsonObject groupe || groupe["hooks"] is not JsonArray hooks || !hooks.Any(EstDeHoverguard))
            {
                resultat.Add(entree?.DeepClone());
                continue;
            }
            var restants = hooks.Where(h => !EstDeHoverguard(h)).Select(h => h?.DeepClone()).ToArray();
            if (restants.Length == 0) continue;
            var copie = (JsonObject)groupe.DeepClone();
            copie["hooks"] = new JsonArray(restants);
            resultat.Add(copie);
        }
        return resultat;
    }

    /// <summary>Chaque événement suivi a un hook de Hoverguard.</summary>
    public bool EstBranche(JsonObject reglages)
        => reglages["hooks"] is JsonObject hooks && Evenements.All(e => Entrees(hooks, e.Nom).Any(ContientHoverguard));

    /// <summary>Branché avec exactement les commandes de cette version (sinon : proposer de mettre à jour).</summary>
    public bool EstAJour(JsonObject reglages, string? dossierWindows = null)
        => reglages["hooks"] is JsonObject hooks && Evenements.All(e =>
        {
            var attendue = Commande(e.Etat, e.Sortie, dossierWindows: dossierWindows);
            var commandes = Disposition == Forme.Groupes
                ? Entrees(hooks, e.Nom).SelectMany(g => g?["hooks"] as JsonArray ?? [])
                : Entrees(hooks, e.Nom);
            return commandes.Any(h => h is JsonObject o && o["command"]?.GetValueKind() == JsonValueKind.String
                                      && o["command"]!.GetValue<string>() == attendue);
        });

    /// <summary>Un hook de Hoverguard, quel que soit l'événement (même un événement d'une ancienne version).</summary>
    public bool ContientHoverguard(JsonObject reglages)
        => reglages["hooks"] is JsonObject hooks && hooks.Any(p => p.Value is JsonArray l && l.Any(ContientHoverguard));

    /// <summary>Renvoie une copie des réglages avec les hooks de Hoverguard (remplacés s'ils existaient déjà), le reste
    /// intact.</summary>
    public JsonObject Brancher(JsonObject reglages, string? dossierWindows = null)
    {
        var resultat = (JsonObject)reglages.DeepClone();
        if (resultat["hooks"] is not JsonObject hooks)
        {
            hooks = new JsonObject();
            resultat["hooks"] = hooks;
        }
        foreach (var e in Evenements)
        {
            var liste = SansHoverguard(Entrees(hooks, e.Nom));
            var commande = Commande(e.Etat, e.Sortie, dossierWindows: dossierWindows);
            switch (Disposition)
            {
                case Forme.Groupes:
                    var groupe = new JsonObject();
                    if (e.Matcher is not null) groupe["matcher"] = e.Matcher;
                    groupe["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = commande, ["timeout"] = Delai });
                    liste.Add(groupe);
                    break;
                default:
                    liste.Add(new JsonObject { ["command"] = commande, ["timeout"] = Delai });
                    break;
            }
            hooks[e.Nom] = liste;
        }
        if (Disposition == Forme.Plate && resultat["version"] is null) resultat["version"] = 1;
        return resultat;
    }

    /// <summary>Renvoie une copie des réglages sans aucun hook de Hoverguard, le reste intact. Un événement dont la liste
    /// devient vide est retiré.</summary>
    public JsonObject Debrancher(JsonObject reglages)
    {
        var resultat = (JsonObject)reglages.DeepClone();
        if (resultat["hooks"] is not JsonObject hooks) return resultat;
        foreach (var (evenement, valeur) in hooks.ToList())
        {
            if (valeur is not JsonArray liste || !liste.Any(ContientHoverguard)) continue;
            var restante = SansHoverguard(liste);
            if (restante.Count == 0) hooks.Remove(evenement);
            else hooks[evenement] = restante;
        }
        return resultat;
    }

    // ── Fichier ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Le vrai fichier : si le chemin est un lien symbolique (dotfiles), sa cible finale, pour écrire dedans
    /// au lieu de remplacer le lien (audit M6). Une jonction ou un lien sur un dossier parent est suivi naturellement.</summary>
    public static string Cible(string chemin)
    {
        var info = new FileInfo(chemin);
        if (info.LinkTarget is null) return info.FullName;
        return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? info.FullName;
    }

    /// <summary>Copie unique gardée à côté du fichier avant chaque écriture.</summary>
    public static string Sauvegarde(string cible) => cible + ".avant-hoverguard";

    private static readonly JsonDocumentOptions Strict = new();
    private static readonly JsonDocumentOptions Tolerant = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    /// <summary>Lit les réglages. Un fichier absent ou vide donne un objet vide. Un fichier qui n'est pas du JSON strict
    /// (commentaires, virgules en trop…) ou dont la racine n'est pas un objet est refusé : le réécrire ferait perdre
    /// ce que Hoverguard ne sait pas relire.</summary>
    /// <exception cref="FichierIllisible">Le fichier existe mais Hoverguard ne doit pas y toucher.</exception>
    public JsonObject Lire(string dossierUtilisateur)
    {
        var fichier = Fichier(dossierUtilisateur);
        var cible = Cible(fichier);
        if (!File.Exists(cible)) return new JsonObject();
        var texte = File.ReadAllText(cible);
        if (string.IsNullOrWhiteSpace(texte)) return new JsonObject();
        return Analyser(texte, FichierAffiche(fichier));
    }

    /// <summary>Analyse le texte d'un fichier de réglages (voir <see cref="Lire"/>).</summary>
    public static JsonObject Analyser(string texte, string nomAffiche)
    {
        JsonNode? racine;
        try
        {
            racine = JsonNode.Parse(texte, documentOptions: Strict);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        {
            bool tolere;
            try { tolere = JsonNode.Parse(texte, documentOptions: Tolerant) is not null; }
            catch (Exception) { tolere = false; }
            throw new FichierIllisible(tolere
                ? $"{nomAffiche} contient des commentaires ou des virgules en trop. Hoverguard n'y touche pas : en le " +
                  "réécrivant, il ferait disparaître vos commentaires. Retirez-les, ou ajoutez les hooks à la main."
                : $"{nomAffiche} n'est pas du JSON lisible ({e.Message}). Hoverguard n'y touche pas : corrigez le fichier, puis réessayez.");
        }
        if (racine is not JsonObject objet)
            throw new FichierIllisible($"{nomAffiche} n'a pas la forme attendue (un objet JSON entre accolades). Hoverguard n'y touche pas.");
        try
        {
            // Une clé en double n'est détectée qu'au premier accès au contenu.
            _ = objet.Count;
        }
        catch (ArgumentException)
        {
            throw new FichierIllisible($"{nomAffiche} contient deux fois la même clé. Hoverguard n'y touche pas : corrigez le fichier, puis réessayez.");
        }
        return objet;
    }

    /// <summary>Chemin montré à l'utilisateur, avec <c>%USERPROFILE%</c> à la place du dossier personnel.</summary>
    public string FichierAffiche(string fichier)
    {
        var maison = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return maison.Length > 0 && fichier.StartsWith(maison, StringComparison.OrdinalIgnoreCase)
            ? "%USERPROFILE%" + fichier[maison.Length..]
            : fichier;
    }

    private static readonly JsonSerializerOptions OptionsEcriture = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,  // garde « é » et « & » lisibles ; le fichier reste du JSON valide
    };

    /// <summary>Écrit les réglages dans la vraie cible du chemin (lien symbolique suivi). Si le fichier existait, il
    /// devient l'unique copie <c>….avant-hoverguard</c> (la précédente est remplacée) et le nouveau fichier garde ses
    /// droits : sous Windows, <c>ReplaceFile</c> conserve ACL, attributs et date de création ; ailleurs, le mode Unix est
    /// recopié. Écriture atomique (fichier temporaire dans le même dossier). Renvoie le chemin de la copie.</summary>
    public string? Ecrire(string dossierUtilisateur, JsonObject reglages)
    {
        var cible = Cible(Fichier(dossierUtilisateur));
        Directory.CreateDirectory(Path.GetDirectoryName(cible)!);
        var temporaire = cible + ".hoverguard-tmp";
        File.WriteAllText(temporaire, reglages.ToJsonString(OptionsEcriture) + "\n", new UTF8Encoding(false));
        try
        {
            if (!File.Exists(cible))
            {
                // Nouveau fichier : il peut recevoir des clés plus tard, lisible par l'utilisateur seul (Windows : ACL héritée
                // du profil, déjà privée).
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporaire, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Move(temporaire, cible);
                return null;
            }
            var copie = Sauvegarde(cible);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporaire, File.GetUnixFileMode(cible));
            if (File.Exists(copie)) File.Delete(copie);
            File.Replace(temporaire, cible, copie, ignoreMetadataErrors: false);
            return copie;
        }
        finally
        {
            if (File.Exists(temporaire)) File.Delete(temporaire);
        }
    }
}

/// <summary>Fichier de réglages qu'Hoverguard refuse de modifier ; le message s'adresse à l'utilisateur.</summary>
public sealed class FichierIllisible(string message) : Exception(message);
