using System.Security;

namespace Hoverguard.Coeur;

public enum GenreSaisie
{
    Caractere,
    Effacer,
    Valider,
    Annuler,
    /// <summary>F2 : demander Windows Hello.</summary>
    Hello,
}

/// <summary>Ce qu'une touche physique fait au mot de passe tapé sous le voile (équivalent de <c>ActionSaisie</c> sur
/// macOS). Le crochet clavier avale la touche et transmet seulement cette action : aucune frappe n'atteint les apps,
/// quelle que soit la fenêtre qui a le clavier (sinon le mot de passe pourrait partir dans le terminal d'un agent).
/// Les caractères sont portés par deux <see cref="char"/> (une paire de substitution au plus), jamais par une chaîne
/// .NET qui resterait en mémoire.</summary>
public readonly record struct ActionSaisie(GenreSaisie Genre, char A = '\0', char B = '\0')
{
    public int Longueur => Genre != GenreSaisie.Caractere ? 0 : B == '\0' ? 1 : 2;

    public static ActionSaisie Caractere(char a, char b = '\0') => new(GenreSaisie.Caractere, a, b);

    /// <param name="vk">Touche virtuelle Windows.</param>
    /// <param name="caracteres">Ce que la disposition du clavier produit pour cette touche (<c>ToUnicodeEx</c>) ;
    /// vide pour une touche morte ou une touche sans caractère.</param>
    /// <param name="ctrl">Ctrl tenu. Ctrl + Alt (AltGr) reste permis : il sert à taper « @ » ou « € ».</param>
    public static ActionSaisie? Depuis(int vk, ReadOnlySpan<char> caracteres, bool ctrl, bool alt, bool win)
    {
        switch (vk)
        {
            case Touches.Effacement or Touches.Suppr: return new(GenreSaisie.Effacer);
            case Touches.Entree: return new(GenreSaisie.Valider);
            case Touches.Echap: return new(GenreSaisie.Annuler);
            case Touches.F2: return new(GenreSaisie.Hello);
        }
        if (win || (ctrl && !alt) || (alt && !ctrl)) return null;  // raccourcis : rien n'est tapé
        if (caracteres.Length is 0 or > 2) return null;
        foreach (var c in caracteres)
            if (char.IsControl(c)) return null;
        return caracteres.Length == 1 ? Caractere(caracteres[0]) : Caractere(caracteres[0], caracteres[1]);
    }
}

/// <summary>Le mot de passe tapé sous le voile, rempli par le crochet clavier. Il vit dans un <see cref="SecureString"/>
/// et n'est jamais converti en chaîne .NET ; seule sa longueur est affichée (des points).</summary>
public sealed class TamponMotDePasse : IDisposable
{
    public const int Maximum = 256;
    private SecureString texte = new();
    /// <summary>Nombre d'unités UTF-16 ajoutées à chaque touche (2 pour une paire de substitution, ou une touche morte
    /// suivie d'une lettre qu'elle ne compose pas, « ^z »), pour qu'Effacement retire ce que la touche a tapé.</summary>
    private readonly Stack<int> tailles = new();

    /// <summary>Nombre de caractères tapés (points affichés).</summary>
    public int Longueur => tailles.Count;

    public void Ajouter(ActionSaisie a)
    {
        if (a.Genre != GenreSaisie.Caractere || texte.Length + a.Longueur > Maximum) return;
        texte.AppendChar(a.A);
        if (a.Longueur == 2) texte.AppendChar(a.B);
        tailles.Push(a.Longueur);
    }

    public void Effacer()
    {
        if (tailles.Count == 0) return;
        for (int n = tailles.Pop(); n > 0; n--) texte.RemoveAt(texte.Length - 1);
    }

    /// <summary>Copie à vérifier (à libérer par l'appelant) ; le tampon, lui, est vidé.</summary>
    public SecureString Prendre()
    {
        var copie = texte.Copy();
        Vider();
        return copie;
    }

    public void Vider()
    {
        texte.Clear();
        tailles.Clear();
    }

    public void Dispose()
    {
        texte.Dispose();
        tailles.Clear();
    }
}

/// <summary>Mots de passe faux : compte des échecs et attente imposée. L'attente est portée par la garde, pas par la
/// fenêtre d'identification : appuyer sur Échap puis réveiller le drone ne la contourne pas, et un échec compte même
/// si Échap a été pressé pendant la vérification.</summary>
public sealed class CompteurEchecs
{
    public int Echecs { get; private set; }
    public DateTime BloqueJusqua { get; private set; } = DateTime.MinValue;

    /// <summary>Note un échec ; renvoie l'attente imposée, s'il y en a une.</summary>
    public TimeSpan? Echec(DateTime maintenant)
    {
        Echecs++;
        var attente = ReglesGarde.AttenteApres(Echecs);
        if (attente is TimeSpan a) BloqueJusqua = maintenant + a;
        return attente;
    }

    public bool EstBloque(DateTime maintenant) => maintenant < BloqueJusqua;

    public TimeSpan Restant(DateTime maintenant) => EstBloque(maintenant) ? BloqueJusqua - maintenant : TimeSpan.Zero;

    public void Reussite()
    {
        Echecs = 0;
        BloqueJusqua = DateTime.MinValue;
    }
}

/// <summary><c>hoverguard://verrouiller</c> peut être ouvert en boucle par n'importe quelle page ou app pour gêner :
/// une fois par minute au plus.</summary>
public sealed class LimiteurVerrouillage
{
    public static readonly TimeSpan Intervalle = TimeSpan.FromMinutes(1);
    private DateTime dernier = DateTime.MinValue;

    public bool Autoriser(DateTime maintenant)
    {
        if (maintenant - dernier <= Intervalle) return false;
        dernier = maintenant;
        return true;
    }
}

/// <summary>Ce que le crochet souris apprend du champ <c>dwExtraInfo</c>.</summary>
public static class Provenance
{
    /// <summary>Signature posée par Windows sur les messages souris fabriqués à partir du toucher ou du stylet
    /// (<c>MI_WP_SIGNATURE</c>, masque <c>0xFFFFFF00</c>).</summary>
    public const ulong SignatureToucher = 0xFF515700, MasqueSignature = 0xFFFFFF00;

    /// <summary>Vrai si l'événement vient du matériel. Un événement marqué « injecté » mais signé toucher/stylet est
    /// traité comme matériel : c'est un doigt sur l'écran ou le pavé tactile, pas un agent.</summary>
    public static bool EstMaterielle(bool injectee, ulong extraInfo)
        => !injectee || (extraInfo & MasqueSignature) == SignatureToucher;
}
