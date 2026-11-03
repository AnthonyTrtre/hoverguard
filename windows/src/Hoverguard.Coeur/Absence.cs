namespace Hoverguard.Coeur;

/// <summary>Ce qui s'est passé pendant la garde, résumé au retour de l'utilisateur. Les dates sont locales.</summary>
public sealed class JournalAbsence
{
    public DateTime Debut { get; }
    private readonly List<DateTime> reveils = new();
    private readonly List<string> agentsTermines = new();
    private readonly List<string> photos = new();

    public JournalAbsence(DateTime? debut = null) => Debut = debut ?? DateTime.Now;

    public IReadOnlyList<DateTime> Reveils => reveils;
    public IReadOnlyList<string> AgentsTermines => agentsTermines;
    public IReadOnlyList<string> Photos => photos;
    public int MotsDePasseFaux { get; private set; }
    public int PicSousAgents { get; private set; }

    public void Reveil(DateTime? date = null) => reveils.Add(date ?? DateTime.Now);
    public void MotDePasseFaux() => MotsDePasseFaux++;
    public void Photo(string chemin) => photos.Add(chemin);
    public void AgentTermine(string nom) { if (!agentsTermines.Contains(nom)) agentsTermines.Add(nom); }
    public void SousAgents(int n) => PicSousAgents = Math.Max(PicSousAgents, n);

    /// <summary>Une photo de l'intrus à la 2e erreur, puis toutes les 3 erreurs (2, 5, 8…).</summary>
    public bool FautPhotographier => MotsDePasseFaux >= 2 && (MotsDePasseFaux - 2) % 3 == 0;

    public string Rapport(DateTime? fin = null)
    {
        var duree = (fin ?? DateTime.Now) - Debut;
        var lignes = new List<string>
        {
            $"Absence : {(duree.TotalSeconds < 60 ? "moins d'une minute" : ReglesEveil.TexteRestant(duree))}.",
            agentsTermines.Count == 0 ? "Aucun agent n'a terminé pendant ce temps."
                                      : $"Agents terminés : {string.Join(", ", agentsTermines)}.",
        };
        if (PicSousAgents > 0) lignes.Add($"Jusqu'à {PicSousAgents} sous-agents en même temps.");
        if (reveils.Count == 0)
            lignes.Add("Personne n'a touché au PC. 👍");
        else
            lignes.Add($"Quelqu'un a touché au PC {reveils.Count} fois ({string.Join(", ", reveils.Select(Textes.Heure))}).");
        if (MotsDePasseFaux > 0) lignes.Add($"Mots de passe faux : {MotsDePasseFaux}.");
        if (photos.Count > 0) lignes.Add($"Photos prises par la webcam : {photos.Count}.");
        return string.Join("\n", lignes);
    }
}

/// <summary>Événements qui pourront partir en notification sur le téléphone, avec un délai minimal entre deux envois
/// du même type. Sous Windows il n'y a pas encore de canal (pas d'iMessage) : voir <c>Notifications</c>.</summary>
public enum Alerte { AgentAttend, AgentsFinis, Intrus, BatterieFaible }

public static class Alertes
{
    public static readonly TimeSpan IntervalleMin = TimeSpan.FromMinutes(5);

    public static string Texte(this Alerte a, string detail) => a switch
    {
        Alerte.AgentAttend => $"🛸 Hoverguard : {detail} attend ta réponse.",
        Alerte.AgentsFinis => $"🛸 Hoverguard : tous les agents ont fini. {detail}",
        Alerte.Intrus => $"🚨 Hoverguard : quelqu'un essaie d'entrer sur ton PC ({detail}).",
        _ => $"🪫 Hoverguard : {detail} Branche le PC, les agents vont s'arrêter.",
    };
}

public sealed class LimiteurAlertes
{
    private readonly Dictionary<Alerte, DateTime> derniers = new();

    /// <summary>Vrai si l'alerte peut partir maintenant (et note l'envoi).</summary>
    public bool Autoriser(Alerte alerte, DateTime? a = null)
    {
        var maintenant = a ?? DateTime.UtcNow;
        if (derniers.TryGetValue(alerte, out var d) && maintenant - d < Alertes.IntervalleMin) return false;
        derniers[alerte] = maintenant;
        return true;
    }
}
