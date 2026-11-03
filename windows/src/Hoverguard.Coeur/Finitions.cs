namespace Hoverguard.Coeur;

/// <summary>Photos d'intrus : effacées au bout de 30 jours, comme sur macOS (<c>Camera.purger</c>, audit M8).</summary>
public static class PhotosIntrus
{
    public const int JoursDeConservation = 30;

    /// <summary>Les fichiers à effacer : ceux créés il y a plus de <paramref name="jours"/> jours. Une date inconnue
    /// (<c>null</c>) compte comme « maintenant » : dans le doute, on garde.</summary>
    public static IReadOnlyList<string> AEffacer(IEnumerable<(string Chemin, DateTime? CreeLeUtc)> fichiers, DateTime maintenantUtc,
                                                 int jours = JoursDeConservation)
    {
        var limite = maintenantUtc - TimeSpan.FromDays(jours);
        return fichiers.Where(f => (f.CreeLeUtc ?? maintenantUtc) < limite).Select(f => f.Chemin).ToList();
    }
}

/// <summary>Icône de la zone de notification : deux états (libre, garde) comme la barre des menus du Mac, chacun
/// décliné pour une barre des tâches sombre ou claire (fichiers <c>windows/ressources/notification-*.ico</c>).</summary>
public static class IconesNotification
{
    public static string Nom(bool garde, bool barreClaire)
        => $"notification-{(garde ? "garde" : "libre")}-{(barreClaire ? "clair" : "sombre")}.ico";

    /// <summary>Info-bulle de l'icône, comme sur macOS ; Windows la coupe à 63 caractères.</summary>
    public static string InfoBulle(bool garde, int principaux, int sousAgents)
    {
        var texte = garde ? "Hoverguard : garde posée" : $"Hoverguard : {Textes.Compteur(principaux, sousAgents)}";
        return texte.Length > 63 ? texte[..63] : texte;
    }
}
