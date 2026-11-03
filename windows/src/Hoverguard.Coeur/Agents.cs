namespace Hoverguard.Coeur;

/// <summary>État signalé par un agent (Claude Code, Codex…) dans <c>hoverguard://agent?nom=…&amp;etat=…</c>.</summary>
public enum EtatAgent
{
    Travail,
    Attente,
    Fini,
    /// <summary>Un sous-agent démarre dans cette session.</summary>
    SousAgentDebut,
    /// <summary>Un sous-agent s'arrête dans cette session.</summary>
    SousAgentFin,
}

public static class EtatsAgent
{
    public static string Texte(this EtatAgent e) => e switch
    {
        EtatAgent.Travail => "travail",
        EtatAgent.Attente => "attente",
        EtatAgent.Fini => "fini",
        EtatAgent.SousAgentDebut => "sous-agent-debut",
        _ => "sous-agent-fin",
    };

    public static EtatAgent? Lire(string? texte) => texte switch
    {
        "travail" => EtatAgent.Travail,
        "attente" => EtatAgent.Attente,
        "fini" => EtatAgent.Fini,
        "sous-agent-debut" => EtatAgent.SousAgentDebut,
        "sous-agent-fin" => EtatAgent.SousAgentFin,
        _ => null,
    };
}

/// <summary>Ce que les agents signalent. Une session = un agent principal ; ses sous-agents sont comptés à part, car
/// ça peut vite monter. Non thread-safe : utilisé sur le fil de l'interface.</summary>
public sealed class SuiviAgents
{
    public sealed record Session(EtatAgent Etat, DateTime Date, int SousAgents);

    /// <summary>Au-delà, un agent qui n'a plus rien dit est considéré comme arrêté (il a pu planter sans prévenir).</summary>
    public static readonly TimeSpan SilenceMax = TimeSpan.FromMinutes(20);

    private readonly Dictionary<string, Session> sessions = new();
    public IReadOnlyDictionary<string, Session> Sessions => sessions;

    /// <summary>Au-delà, les sessions les plus anciennes sont oubliées (une app pourrait en inventer des milliers par
    /// URL).</summary>
    public const int MaxSessions = 50;

    public void Signaler(string agent, EtatAgent etat, DateTime? date = null)
    {
        var maintenant = date ?? DateTime.UtcNow;
        if (!sessions.ContainsKey(agent) && sessions.Count >= MaxSessions)
        {
            foreach (var muette in sessions.Where(s => maintenant - s.Value.Date >= SilenceMax).Select(s => s.Key).ToList())
                sessions.Remove(muette);
            if (sessions.Count >= MaxSessions) sessions.Remove(sessions.MinBy(s => s.Value.Date).Key);
        }
        var s = sessions.TryGetValue(agent, out var existante) ? existante : new Session(EtatAgent.Travail, maintenant, 0);
        s = s with { Date = maintenant };
        switch (etat)
        {
            case EtatAgent.Fini: sessions.Remove(agent); return;
            case EtatAgent.Travail or EtatAgent.Attente: s = s with { Etat = etat }; break;
            case EtatAgent.SousAgentDebut: s = s with { Etat = EtatAgent.Travail, SousAgents = s.SousAgents + 1 }; break;
            case EtatAgent.SousAgentFin: s = s with { SousAgents = Math.Max(0, s.SousAgents - 1) }; break;
        }
        sessions[agent] = s;
    }

    private IEnumerable<KeyValuePair<string, Session>> Vivantes(DateTime maintenant)
        => sessions.Where(s => maintenant - s.Value.Date < SilenceMax);

    public IReadOnlyList<string> Actifs(DateTime? a = null)
        => Vivantes(a ?? DateTime.UtcNow).Where(s => s.Value.Etat == EtatAgent.Travail).Select(s => s.Key)
            .Order(StringComparer.Ordinal).ToList();

    public IReadOnlyList<string> EnAttente(DateTime? a = null)
        => Vivantes(a ?? DateTime.UtcNow).Where(s => s.Value.Etat == EtatAgent.Attente).Select(s => s.Key)
            .Order(StringComparer.Ordinal).ToList();

    public int SousAgents(DateTime? a = null)
        => Vivantes(a ?? DateTime.UtcNow).Where(s => s.Value.Etat == EtatAgent.Travail).Sum(s => s.Value.SousAgents);

    /// <summary>Tout le monde : agents principaux qui travaillent + leurs sous-agents.</summary>
    public int Total(DateTime? a = null)
    {
        var t = a ?? DateTime.UtcNow;
        return Actifs(t).Count + SousAgents(t);
    }
}

/// <summary>Ordres reçus par URL.</summary>
public abstract record Ordre
{
    public sealed record Agent(string Nom, EtatAgent Etat) : Ordre;
    public sealed record Verrouiller : Ordre;
    /// <summary><c>hoverguard://licence?cle=HG1-…</c> : lien envoyé avec une licence (achat ou cadeau). Sans danger :
    /// une licence ne s'active que si sa signature Utahpik est valide.</summary>
    public sealed record ActiverLicence(string Cle) : Ordre;

    /// <summary><c>hoverguard://agent?nom=claude&amp;etat=travail</c>, <c>hoverguard://verrouiller</c>.
    /// Volontairement AUCUN ordre pour LEVER la garde : n'importe quelle page web ou app peut ouvrir une URL.</summary>
    public static Ordre? Lire(string? texte)
    {
        if (string.IsNullOrWhiteSpace(texte) || texte.Length > 2048) return null;
        if (!Uri.TryCreate(texte.Trim(), UriKind.Absolute, out var url)) return null;
        if (!string.Equals(url.Scheme, "hoverguard", StringComparison.OrdinalIgnoreCase)) return null;
        // Windows ajoute parfois une barre oblique finale (« hoverguard://verrouiller/ ») : on l'accepte, rien d'autre.
        if (url.AbsolutePath is not ("" or "/")) return null;
        var parametres = LireRequete(url.Query);
        switch (url.Host.ToLowerInvariant())
        {
            case "agent":
                if (EtatsAgent.Lire(parametres.GetValueOrDefault("etat")) is not EtatAgent etat) return null;
                return new Agent(Regles.NomAgentPropre(parametres.GetValueOrDefault("nom")), etat);
            case "verrouiller":
                return new Verrouiller();
            case "licence":
                var cle = parametres.GetValueOrDefault("cle");
                return cle is not null && cle.StartsWith(Licence.Prefixe, StringComparison.Ordinal) && cle.Length < 2000
                    ? new ActiverLicence(cle) : null;
            default:
                return null;
        }
    }

    private static Dictionary<string, string> LireRequete(string requete)
    {
        var resultat = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var morceau in requete.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = morceau.IndexOf('=');
            var cle = Uri.UnescapeDataString(i < 0 ? morceau : morceau[..i]);
            var valeur = i < 0 ? "" : Uri.UnescapeDataString(morceau[(i + 1)..]);
            resultat.TryAdd(cle, valeur);  // le premier gagne, comme sur macOS
        }
        return resultat;
    }
}
