namespace Hoverguard.Coeur;

/// <summary>Durées proposées dans le menu « Garder le PC éveillé ». <c>Indefinie</c> = jusqu'à ce qu'on arrête.</summary>
public enum Duree
{
    QuinzeMinutes = 900,
    TrenteMinutes = 1800,
    UneHeure = 3600,
    DeuxHeures = 7200,
    QuatreHeures = 14400,
    Indefinie = 0,
}

public static class Durees
{
    public static readonly IReadOnlyList<Duree> Toutes =
        [Duree.QuinzeMinutes, Duree.TrenteMinutes, Duree.UneHeure, Duree.DeuxHeures, Duree.QuatreHeures, Duree.Indefinie];

    public static TimeSpan? Longueur(this Duree d) => d == Duree.Indefinie ? null : TimeSpan.FromSeconds((int)d);

    public static string Libelle(this Duree d) => d switch
    {
        Duree.QuinzeMinutes => "15 minutes",
        Duree.TrenteMinutes => "30 minutes",
        Duree.UneHeure => "1 heure",
        Duree.DeuxHeures => "2 heures",
        Duree.QuatreHeures => "4 heures",
        _ => "Jusqu'à l'arrêt",
    };
}

/// <summary>Une période pendant laquelle le PC reste éveillé.</summary>
public sealed record SessionEveil(Duree Duree, DateTime? Fin)
{
    public static SessionEveil Commencer(Duree duree, DateTime debut)
        => new(duree, duree.Longueur() is TimeSpan l ? debut + l : null);

    /// <summary>Temps restant, null pour une session sans fin.</summary>
    public TimeSpan? Restant(DateTime maintenant)
        => Fin is DateTime f ? (f > maintenant ? f - maintenant : TimeSpan.Zero) : null;

    public bool EstExpiree(DateTime maintenant) => Restant(maintenant) == TimeSpan.Zero;
}

public static class ReglesEveil
{
    /// <summary>Arrêt automatique : seulement sur batterie, à <paramref name="seuil"/> % ou moins. 0 désactive la règle.</summary>
    public static bool DoitArreter(EtatBatterie batterie, int seuil)
        => seuil > 0 && !batterie.SurSecteur && batterie.Niveau is int n && n <= seuil;

    /// <summary>« 1 h 05 », « 12 min ». Arrondi à la minute supérieure pour ne jamais annoncer 0 min tant que ça tourne.</summary>
    public static string TexteRestant(TimeSpan duree)
    {
        int minutes = (int)Math.Ceiling(duree.TotalSeconds / 60);
        return minutes < 60 ? $"{minutes} min" : $"{minutes / 60} h {minutes % 60:00}";
    }
}
