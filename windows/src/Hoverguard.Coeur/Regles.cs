namespace Hoverguard.Coeur;

public enum EtatGarde
{
    Libre,
    /// <summary>Voile posé, clavier et souris physiques bloqués ; Claude et les autres agents passent.</summary>
    Garde,
    /// <summary>Une touche ou un clic a « réveillé » la garde : le mot de passe Hoverguard est demandé.</summary>
    Authentification,
}

public enum Genre
{
    /// <summary>Touche enfoncée (hors modificateurs seuls).</summary>
    Touche,
    /// <summary>Bouton de souris enfoncé.</summary>
    Clic,
    /// <summary>Relâchements, mouvements, molette, modificateurs seuls…</summary>
    Autre,
}

/// <summary>Ce que le filtre d'entrées sait d'un événement clavier ou souris.</summary>
/// <param name="EstPhysique">Vrai pour le vrai clavier et la vraie souris ; faux pour un événement injecté
/// (<c>LLKHF_INJECTED</c> / <c>LLMHF_INJECTED</c>) par un logiciel, c'est-à-dire par les agents.</param>
/// <param name="AvecSysteme">Raccourci qui ferait quitter la saisie du mot de passe (touche Windows, Alt+Tab…).</param>
public readonly record struct Entree(bool EstPhysique, Genre Genre, bool EstRaccourci = false, bool AvecSysteme = false);

public enum Decision
{
    Laisser,
    Bloquer,
    /// <summary>Raccourci hors garde : poser la garde.</summary>
    Poser,
    /// <summary>Touche ou clic sous la garde : afficher la demande d'identification.</summary>
    Reveiller,
    /// <summary>Pendant la demande : la touche est avalée et sert au mot de passe, quelle que soit la fenêtre qui a le
    /// clavier (sinon le mot de passe pourrait partir dans le terminal de l'agent).</summary>
    Saisir,
}

/// <summary>Codes de touches virtuelles Windows utiles au filtre.</summary>
public static class Touches
{
    public const int Effacement = 0x08, Tab = 0x09, Entree = 0x0D, Echap = 0x1B, Espace = 0x20, Suppr = 0x2E;
    public const int F2 = 0x71, F4 = 0x73, W = 0x57, Veille = 0x5F;
    public const int Maj = 0x10, Ctrl = 0x11, Alt = 0x12, VerrMaj = 0x14;
    public const int WinGauche = 0x5B, WinDroite = 0x5C, Applications = 0x5D;
    public const int MajGauche = 0xA0, MajDroite = 0xA1, CtrlGauche = 0xA2, CtrlDroite = 0xA3, AltGauche = 0xA4, AltDroite = 0xA5;
    /// <summary>Touche virtuelle non attribuée : injectée pour que Windows n'ouvre pas le menu Démarrer au
    /// relâchement de la touche Windows (même astuce qu'AutoHotkey).</summary>
    public const int Masque = 0xE8;

    public static bool EstCtrl(int vk) => vk is Ctrl or CtrlGauche or CtrlDroite;
    public static bool EstAlt(int vk) => vk is Alt or AltGauche or AltDroite;
    public static bool EstMaj(int vk) => vk is Maj or MajGauche or MajDroite;
    public static bool EstWin(int vk) => vk is WinGauche or WinDroite;
    public static bool EstModificateur(int vk) => EstCtrl(vk) || EstAlt(vk) || EstMaj(vk) || EstWin(vk) || vk == VerrMaj;

    /// <summary>Touches multimédia et système : navigateur, volume, lecture, lancement d'apps (0xA6 à 0xB7), veille.
    /// Sous la garde elles sont avalées sans réveiller le drone, comme les touches système sur macOS.</summary>
    public static bool EstMultimedia(int vk) => vk is >= 0xA6 and <= 0xB7 or Veille;
}

public static class Regles
{
    /// <summary>Raccourci affiché dans le menu et les réglages.</summary>
    public const string Raccourci = "Ctrl+Win+W";

    /// <summary>Cœur de la garde. Ce que fabriquent les agents passe toujours ; seul le matériel est filtré.</summary>
    public static Decision Decider(Entree e, EtatGarde etat)
    {
        if (!e.EstPhysique) return Decision.Laisser;
        return etat switch
        {
            EtatGarde.Libre => e.EstRaccourci && e.Genre == Genre.Touche ? Decision.Poser : Decision.Laisser,
            EtatGarde.Garde => e.Genre == Genre.Autre ? Decision.Bloquer : Decision.Reveiller,
            // Rien du matériel n'atteint les apps : les touches nourrissent le mot de passe, le reste est avalé.
            _ => e.Genre == Genre.Touche ? Decision.Saisir : Decision.Bloquer,
        };
    }

    /// <summary>Ctrl+Win+W, sans Alt ni Maj. Sous Windows le code virtuel d'une lettre suit la disposition du clavier :
    /// c'est donc la touche marquée W, en AZERTY comme en QWERTY. Windows n'utilise pas Ctrl+Win+W, et ce raccourci
    /// ne touche pas AltGr (= Ctrl+Alt), indispensable aux claviers français.</summary>
    public static bool EstRaccourci(int vk, bool ctrl, bool win, bool alt, bool maj, int touche = Touches.W)
        => vk == touche && ctrl && win && !alt && !maj;

    /// <summary>Touches qui feraient perdre le clavier à la fenêtre d'identification : touche Windows (seule ou
    /// combinée), Alt+Tab, Alt+Échap, Alt+F4, Alt+Espace, Ctrl+Échap, Ctrl+Maj+Échap. AltGr (Ctrl+Alt) reste permis :
    /// il sert à taper « @ » ou « € » dans un mot de passe. Ctrl+Alt+Suppr et Win+L ne passent jamais par ici :
    /// Windows les réserve, et c'est très bien ainsi.</summary>
    public static bool EstCommandeSysteme(int vk, bool ctrl, bool alt, bool maj, bool win)
    {
        if (win || Touches.EstWin(vk) || vk == Touches.Applications) return true;
        if (alt && !ctrl && vk is Touches.Tab or Touches.Echap or Touches.F4 or Touches.Espace) return true;
        return ctrl && vk == Touches.Echap;
    }

    /// <summary>Nom d'agent reçu par URL : lettres et chiffres ASCII, espace, point, tiret, souligné ; 40 octets au
    /// plus (comme <c>Regles.nomAgentPropre</c> sur macOS). Il ne sert qu'à l'affichage local, jamais dans une
    /// notification.</summary>
    public static string NomAgentPropre(string? brut)
    {
        var filtre = new string((brut ?? "").Where(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '.' or '_' or '-').ToArray());
        var net = filtre[..Math.Min(40, filtre.Length)].Trim(' ');
        return net.Length == 0 ? "agent" : net;
    }
}

/// <summary>Le drone vole en permanence sous la garde et se pose de temps en temps : 4 à 8 s toutes les 4 à 6 minutes
/// (comme sur macOS).</summary>
public static class ReglesVol
{
    public static TimeSpan Attente(Random r) => TimeSpan.FromSeconds(r.Next(240, 361));
    public static TimeSpan Pause(Random r) => TimeSpan.FromSeconds(r.Next(4, 9));
}

public static class ReglesGarde
{
    /// <summary>Mots de passe faux : 4 essais libres, puis 3 s, 6 s, 9 s… plafonné à 2 min (comme sur macOS).</summary>
    public static TimeSpan? AttenteApres(int echecs)
        => echecs < 5 ? null : TimeSpan.FromSeconds(Math.Min((echecs - 4) * 3, 120));

    /// <summary>Une minute sans rien taper dans l'identification : le drone retourne surveiller.</summary>
    public static readonly TimeSpan SaisieAbandonnee = TimeSpan.FromMinutes(1);
}

/// <summary>Résultat du filtre pour un événement, avec l'information « répétition automatique ».</summary>
public readonly record struct Verdict(Decision Decision, bool Repetition = false);

/// <summary>
/// État du clavier et de la souris PHYSIQUES vu par le filtre : modificateurs tenus, touches dont l'appui est passé.
/// Spécifique à Windows : un crochet bas niveau voit chaque appui et chaque relâchement séparément, et Windows ne
/// signale pas la répétition automatique. Deux pièges évités ici :
/// <list type="bullet">
/// <item>un relâchement dont l'appui est passé passe toujours, sinon la touche resterait « enfoncée » pour Windows
/// (Ctrl ou Win collés : la moindre lettre tapée par un agent deviendrait un raccourci, Win+L compris) ;</item>
/// <item>la répétition automatique d'une touche tenue ne doit poser la garde qu'une fois.</item>
/// </list>
/// Non thread-safe : appelé seulement par le fil du crochet.
/// </summary>
public sealed class SuiviEntrees
{
    private readonly HashSet<int> passees = new();
    private readonly HashSet<int> tenues = new();
    private const int DecalageSouris = 0x1000;

    public bool Ctrl => tenues.Any(Touches.EstCtrl);
    public bool Alt => tenues.Any(Touches.EstAlt);
    public bool Maj => tenues.Any(Touches.EstMaj);
    public bool Win => tenues.Any(Touches.EstWin);
    /// <summary>Touches physiques tenues, pour traduire une touche en caractère pendant la saisie du mot de passe.</summary>
    public IReadOnlyCollection<int> Tenues => tenues;

    /// <summary>Session verrouillée (Win+L, Ctrl+Alt+Suppr) ou réveil de veille : les relâchements se font sur l'écran
    /// de verrouillage, que le crochet ne voit pas. Sans cet oubli, la touche Windows restait « tenue » et chaque
    /// lettre du mot de passe passait pour un raccourci : rien ne s'écrivait.</summary>
    public void Oublier()
    {
        tenues.Clear();
        passees.Clear();
    }

    public Verdict Clavier(int vk, bool enfoncee, bool injectee, EtatGarde etat)
    {
        if (injectee) return new(Decision.Laisser);
        if (!enfoncee)
        {
            tenues.Remove(vk);
            if (passees.Remove(vk)) return new(Decision.Laisser);
            return new(Regles.Decider(new Entree(true, Genre.Autre), etat));
        }
        bool repetition = !tenues.Add(vk);
        // Un modificateur seul ne réveille pas (comme sur macOS), sauf la touche Windows : seule, elle ouvre le menu
        // Démarrer et ferait perdre le clavier à la fenêtre d'identification.
        // Les touches multimédia (volume, lecture…) sont avalées sous la garde sans la réveiller.
        var genre = (Touches.EstModificateur(vk) && !Touches.EstWin(vk)) || Touches.EstMultimedia(vk) ? Genre.Autre : Genre.Touche;
        bool ctrl = Ctrl, alt = Alt, maj = Maj, win = Win;
        var entree = new Entree(true, genre,
            EstRaccourci: genre == Genre.Touche && Regles.EstRaccourci(vk, ctrl, win, alt, maj),
            AvecSysteme: Regles.EstCommandeSysteme(vk, ctrl, alt, maj, win));
        var d = Regles.Decider(entree, etat);
        if (d == Decision.Laisser) passees.Add(vk); else passees.Remove(vk);
        return new(d, repetition);
    }

    /// <summary><paramref name="bouton"/> : 0 gauche, 1 droit, 2 milieu, 3-4 boutons latéraux ; null pour un
    /// mouvement ou la molette.</summary>
    public Verdict Souris(int? bouton, bool enfonce, bool injectee, EtatGarde etat)
    {
        if (injectee) return new(Decision.Laisser);
        if (bouton is not int b) return new(Regles.Decider(new Entree(true, Genre.Autre), etat));
        int code = DecalageSouris + b;
        if (!enfonce)
        {
            if (passees.Remove(code)) return new(Decision.Laisser);
            return new(Regles.Decider(new Entree(true, Genre.Autre), etat));
        }
        var d = Regles.Decider(new Entree(true, Genre.Clic), etat);
        if (d == Decision.Laisser) passees.Add(code);
        return new(d);
    }
}
