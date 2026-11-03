using System.Text;

namespace Hoverguard.Coeur;

/// <summary>Détection automatique des agents IA qui tournent sur le PC, sans rien brancher : chaque processus d'agent
/// connu (Claude Code — y compris les conversations de l'app Claude —, Codex, Gemini, Cursor, Aider…) est repéré, et il
/// compte « au travail » quand lui ou les commandes qu'il lance (compilations, tests) consomment du processeur.
/// Même règles que <c>macos/Sources/Detection.swift</c>. Les sessions cloud tournent sur des serveurs distants : elles ne
/// sont pas visibles d'ici. Logique pure : la lecture des processus Windows est dans <c>Hoverguard/Detecteur.cs</c>.</summary>
public static class ReglesDetection
{
    /// <summary>Nom de l'exécutable (sans « .exe ») ou du script lancé par node / python → nom affiché de l'agent.</summary>
    public static readonly IReadOnlyDictionary<string, string> Agents = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["claude"] = "Claude", ["codex"] = "Codex", ["gemini"] = "Gemini", ["agy"] = "Antigravity", ["cursor-agent"] = "Cursor",
        ["aider"] = "Aider", ["opencode"] = "OpenCode", ["goose"] = "Goose", ["copilot"] = "Copilot", ["amp"] = "Amp",
        ["droid"] = "Droid", ["qwen"] = "Qwen", ["crush"] = "Crush", ["kiro-cli"] = "Kiro", ["cline"] = "Cline",
        ["openhands"] = "OpenHands",
    };

    /// <summary>Interprètes dont il faut lire la ligne de commande pour savoir quel script ils exécutent.
    /// <c>python3.12</c>, <c>pythonw</c>… sont reconnus par leur préfixe ; <c>py</c> est le lanceur Python de Windows.</summary>
    public static readonly IReadOnlySet<string> Interpretes = new HashSet<string>(StringComparer.Ordinal)
        { "node", "bun", "deno", "python", "python3", "py", "uv", "uvx" };

    /// <summary>Au travail si l'arbre du processus a consommé au moins ce pourcentage d'un cœur depuis la mesure précédente.</summary>
    public const double SeuilActivite = 3.0;

    /// <summary>Intervalle entre deux relevés.</summary>
    public static readonly TimeSpan Intervalle = TimeSpan.FromSeconds(10);

    /// <summary>Au-delà, la mesure précédente est trop vieille (veille, PC figé) : pas d'activité déduite.</summary>
    public static readonly TimeSpan EcartMax = TimeSpan.FromMinutes(2);

    private static readonly char[] Separateurs = ['\\', '/'];

    private static readonly string[] Extensions = [".exe", ".js", ".mjs", ".cjs", ".py", ".cmd", ".bat", ".ps1"];

    /// <summary>Dernier élément d'un chemin, en minuscules, séparateurs Windows ou Unix.</summary>
    private static string Fichier(string chemin)
    {
        var i = chemin.LastIndexOfAny(Separateurs);
        return (i < 0 ? chemin : chemin[(i + 1)..]).ToLowerInvariant();
    }

    private static string SansExtension(string nom)
    {
        foreach (var e in Extensions)
            if (nom.EndsWith(e, StringComparison.Ordinal)) return nom[..^e.Length];
        return nom;
    }

    /// <summary>Nom d'exécutable à surveiller (sans « .exe ») : un agent, ou un interprète dont il faudra lire la ligne
    /// de commande. Sert à trier vite la liste des processus avant d'aller lire chemins et arguments.</summary>
    public static bool MeriteExamen(string executable)
    {
        var nom = SansExtension(Fichier(executable));
        return Agents.ContainsKey(nom) || EstInterprete(nom);
    }

    public static bool EstInterprete(string executable)
    {
        var nom = SansExtension(Fichier(executable));
        return Interpretes.Contains(nom) || nom.StartsWith("python", StringComparison.Ordinal);
    }

    /// <summary>L'app de bureau Claude (Electron) n'est pas un agent : ce sont ses conversations (un processus
    /// <c>claude.exe</c> de Claude Code chacune, installé ailleurs) qui comptent. Installée par Squirrel dans
    /// <c>%LOCALAPPDATA%\AnthropicClaude\app-x.y.z\claude.exe</c>, ou en paquet MSIX sous <c>WindowsApps</c>.</summary>
    public static bool EstAppClaude(string executable)
    {
        var morceaux = executable.ToLowerInvariant().Split(Separateurs, StringSplitOptions.RemoveEmptyEntries);
        if (morceaux.Length < 2 || morceaux[^1] != "claude.exe") return false;
        var dossier = morceaux[^2];
        if (dossier.StartsWith("app-", StringComparison.Ordinal) && morceaux.Length >= 3 && morceaux[^3] == "anthropicclaude")
            return true;
        // MSIX : …\WindowsApps\Claude_1.2.3.0_x64__abcd\app\claude.exe (ou directement dans le dossier du paquet).
        int windowsApps = Array.LastIndexOf(morceaux, "windowsapps");
        return windowsApps >= 0 && windowsApps + 1 < morceaux.Length - 1 && morceaux[windowsApps + 1].Contains("claude", StringComparison.Ordinal);
    }

    /// <summary>L'agent que représente ce processus, ou <c>null</c>. <paramref name="executable"/> est le chemin complet
    /// (ou à défaut le nom) de l'exécutable ; <paramref name="arguments"/>, la ligne de commande découpée (argument 0
    /// compris), ne sert que pour les interprètes (node, python…).</summary>
    public static string? Agent(string executable, IReadOnlyList<string> arguments)
    {
        if (EstAppClaude(executable)) return null;
        var nom = SansExtension(Fichier(executable));
        if (Agents.TryGetValue(nom, out var a)) return a;
        if (!EstInterprete(nom)) return null;
        foreach (var argument in arguments.Skip(1).Take(4))
        {
            if (argument.Length == 0 || argument.StartsWith('-')) continue;
            if (Agents.TryGetValue(SansExtension(Fichier(argument)), out var parScript)) return parScript;
            if (AgentDuPaquet(argument) is string parPaquet) return parPaquet;
        }
        return null;
    }

    /// <summary>Script d'un paquet npm reconnaissable à son dossier : « …\node_modules\@google\gemini-cli\dist\index.js »,
    /// « …\@openai\codex\bin\codex.js », « …\@anthropic-ai\claude-code\cli.js », « …\@qwen-code\qwen-code\cli.js ».</summary>
    private static string? AgentDuPaquet(string argument)
    {
        var morceaux = argument.ToLowerInvariant().Split(Separateurs, StringSplitOptions.RemoveEmptyEntries);
        // Le dernier morceau est le fichier lui-même : seuls les dossiers comptent.
        for (int i = 0; i < morceaux.Length - 1; i++)
        {
            var m = morceaux[i];
            foreach (var (cle, nomAgent) in Agents)
            {
                if (m == $"{cle}-cli" || m == $"{cle}-code") return nomAgent;
                // Paquet à portée exacte : « @openai\codex », « @sourcegraph\amp ».
                if (m == cle && i > 0 && morceaux[i - 1].StartsWith('@')) return nomAgent;
            }
        }
        return null;
    }

    /// <summary>Découpe une ligne de commande Windows comme <c>CommandLineToArgvW</c> : espaces hors guillemets,
    /// 2n barres obliques inverses + « " » → n barres et bascule des guillemets, 2n+1 → n barres et « " » littéral,
    /// « "" » entre guillemets → « " ». Le premier argument (programme) s'arrête au premier espace ou guillemet fermant.</summary>
    public static IReadOnlyList<string> Decouper(string? ligne)
    {
        var resultat = new List<string>();
        if (string.IsNullOrEmpty(ligne)) return resultat;
        int i = 0, n = ligne.Length;
        // Programme : aucune règle d'échappement, guillemets seulement.
        var courant = new StringBuilder();
        if (ligne[0] == '"')
        {
            i = 1;
            while (i < n && ligne[i] != '"') courant.Append(ligne[i++]);
            if (i < n) i++;
        }
        else
        {
            while (i < n && ligne[i] is not (' ' or '\t')) courant.Append(ligne[i++]);
        }
        resultat.Add(courant.ToString());

        while (true)
        {
            while (i < n && ligne[i] is ' ' or '\t') i++;
            if (i >= n) break;
            courant.Clear();
            bool guillemets = false;
            while (i < n)
            {
                char c = ligne[i];
                if (c == '\\')
                {
                    int barres = 0;
                    while (i < n && ligne[i] == '\\') { barres++; i++; }
                    if (i < n && ligne[i] == '"')
                    {
                        courant.Append('\\', barres / 2);
                        if (barres % 2 == 1) { courant.Append('"'); i++; }
                    }
                    else courant.Append('\\', barres);
                }
                else if (c == '"')
                {
                    if (guillemets && i + 1 < n && ligne[i + 1] == '"') { courant.Append('"'); i += 2; }
                    else { guillemets = !guillemets; i++; }
                }
                else if (!guillemets && c is ' ' or '\t') break;
                else { courant.Append(c); i++; }
            }
            resultat.Add(courant.ToString());
        }
        return resultat;
    }

    public static double Pourcentage(TimeSpan tempsCpu, TimeSpan intervalle)
        => intervalle > TimeSpan.Zero ? tempsCpu.TotalSeconds / intervalle.TotalSeconds * 100 : 0;

    public static bool EstActif(TimeSpan tempsCpu, TimeSpan intervalle)
        => intervalle > TimeSpan.Zero && intervalle < EcartMax && Pourcentage(tempsCpu, intervalle) >= SeuilActivite;

    /// <summary>Agents principaux au travail : le plus grand des deux comptes (détection des processus, signaux des hooks),
    /// car un même agent peut être vu des deux façons.</summary>
    public static int Principaux(int parHooks, IEnumerable<AgentDetecte> detectes)
        => Math.Max(parHooks, detectes.Count(d => d.Actif));

    /// <summary>Une ligne par outil, triée : (« Codex », 1 au travail, 2 ouverts).</summary>
    public static IReadOnlyList<(string Nom, int AuTravail, int Ouverts)> ParOutil(IEnumerable<AgentDetecte> detectes)
        => detectes.GroupBy(d => d.Nom).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (g.Key, g.Count(d => d.Actif), g.Count())).ToList();

    /// <summary>Résumé du menu : « Claude : 2 au travail / 3 · Codex : 0 au travail / 1 », ou <c>null</c> si rien n'est ouvert.</summary>
    public static string? LigneMenu(IEnumerable<AgentDetecte> detectes)
    {
        var lignes = ParOutil(detectes).Select(o => $"{o.Nom} : {o.AuTravail} au travail / {o.Ouverts}").ToList();
        return lignes.Count == 0 ? null : string.Join(" · ", lignes);
    }

    /// <summary>Détail de l'onglet Agents : « 1 au travail sur 2 ouverts ».</summary>
    public static string Detail(int auTravail, int ouverts) => $"{auTravail} au travail sur {ouverts} ouvert{(ouverts > 1 ? "s" : "")}";
}

/// <summary>Un agent vu à un instant : son processus racine et l'activité de tout son arbre.</summary>
public readonly record struct AgentDetecte(int Pid, string Nom, bool Actif);

/// <summary>Un processus tel que relevé : parent, date de création (0 si inconnue), temps processeur cumulé, et l'agent
/// qu'il représente s'il en est un (<see cref="ReglesDetection.Agent"/>).</summary>
public readonly record struct ProcessusVu(int Pid, int Parent, long Creation, TimeSpan TempsCpu, string? Agent = null);

/// <summary>Passe d'une liste de processus aux agents et à leur activité, d'un relevé au suivant. Non thread-safe :
/// un seul relevé à la fois (le détecteur le garantit).</summary>
public sealed class MesureActivite
{
    /// <summary>Garde-fou contre une chaîne de parents qui boucle (numéros de processus réutilisés).</summary>
    private const int ProfondeurMax = 128;

    private Dictionary<(int, long), TimeSpan> precedent = new();
    private DateTime instantPrecedent = DateTime.MinValue;

    public IReadOnlyList<AgentDetecte> Relever(IReadOnlyList<ProcessusVu> processus, DateTime maintenant)
    {
        var parPid = new Dictionary<int, ProcessusVu>(processus.Count);
        foreach (var p in processus) parPid.TryAdd(p.Pid, p);

        // Le parent d'un processus, s'il existe encore et n'est pas un numéro réutilisé par un processus plus récent.
        int? Parent(ProcessusVu p)
        {
            if (p.Parent <= 0 || p.Parent == p.Pid || !parPid.TryGetValue(p.Parent, out var parent)) return null;
            if (p.Creation != 0 && parent.Creation != 0 && parent.Creation > p.Creation) return null;
            return parent.Pid;
        }

        // Un agent lancé par un autre agent du même nom (helpers internes, node codex.js → codex.exe) n'est compté
        // qu'une fois : sa racine.
        var racines = new Dictionary<int, string>();
        foreach (var p in processus)
        {
            if (p.Agent is not string nom || racines.ContainsKey(p.Pid)) continue;
            bool double_ = false;
            var q = p;
            for (int profondeur = 0; profondeur < ProfondeurMax && Parent(q) is int ancetre; profondeur++)
            {
                q = parPid[ancetre];
                if (q.Agent == nom) { double_ = true; break; }
            }
            if (!double_) racines[p.Pid] = nom;
        }

        // Temps processeur de chaque arbre : la racine et tous ses descendants (outils, compilations). Un agent d'un
        // autre nom lancé par un agent garde son propre arbre.
        var arbre = new Dictionary<int, TimeSpan>();
        foreach (var p in processus)
        {
            var q = p;
            for (int profondeur = 0; profondeur < ProfondeurMax; profondeur++)
            {
                if (racines.ContainsKey(q.Pid))
                {
                    arbre[q.Pid] = arbre.GetValueOrDefault(q.Pid) + p.TempsCpu;
                    break;
                }
                if (Parent(q) is not int ancetre) break;
                q = parPid[ancetre];
            }
        }

        var intervalle = maintenant - instantPrecedent;
        var suivant = new Dictionary<(int, long), TimeSpan>();
        var resultat = new List<AgentDetecte>(racines.Count);
        foreach (var (pid, nom) in racines.OrderBy(r => r.Key))
        {
            var cle = (pid, parPid[pid].Creation);
            var total = arbre.GetValueOrDefault(pid);
            suivant[cle] = total;
            // Premier relevé de cet agent : pas encore d'écart mesurable.
            var ecart = precedent.TryGetValue(cle, out var avant) ? total - avant : TimeSpan.Zero;
            if (ecart < TimeSpan.Zero) ecart = TimeSpan.Zero;  // un descendant s'est terminé : son temps disparaît
            resultat.Add(new AgentDetecte(pid, nom, ReglesDetection.EstActif(ecart, intervalle)));
        }
        precedent = suivant;
        instantPrecedent = maintenant;
        return resultat;
    }
}
