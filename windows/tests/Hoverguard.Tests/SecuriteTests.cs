using System.Runtime.InteropServices;
using System.Security;

namespace Hoverguard.Tests;

/// <summary>Saisie du mot de passe par le crochet (aucune frappe n'atteint les apps).</summary>
public class SaisieTests
{
    static ActionSaisie? Touche(int vk, string car = "", bool ctrl = false, bool alt = false, bool win = false)
        => ActionSaisie.Depuis(vk, car.AsSpan(), ctrl, alt, win);

    [Fact]
    public void ToucheDeCommande()
    {
        Assert.Equal(GenreSaisie.Effacer, Touche(Touches.Effacement)?.Genre);
        Assert.Equal(GenreSaisie.Effacer, Touche(Touches.Suppr)?.Genre);
        Assert.Equal(GenreSaisie.Valider, Touche(Touches.Entree, "\r")?.Genre);
        Assert.Equal(GenreSaisie.Annuler, Touche(Touches.Echap, "\u001b")?.Genre);
        Assert.Equal(GenreSaisie.Hello, Touche(Touches.F2)?.Genre);
    }

    [Fact]
    public void Caracteres()
    {
        Assert.Equal(ActionSaisie.Caractere('a'), Touche(0x41, "a"));
        Assert.Equal(ActionSaisie.Caractere('A'), Touche(0x41, "A"));
        Assert.Equal(ActionSaisie.Caractere('@'), Touche(0x30, "@", ctrl: true, alt: true));  // AltGr+à en AZERTY
        Assert.Equal(ActionSaisie.Caractere('ê'), Touche(0x45, "ê"));  // ^ puis e
        Assert.Equal(ActionSaisie.Caractere('^', 'z'), Touche(0x5A, "^z"));  // touche morte qui ne compose pas
        var emoji = "😀";
        Assert.Equal(2, Touche(0x41, emoji)?.Longueur);
    }

    [Fact]
    public void RaccourcisEtToucheMorteNeTapentRien()
    {
        Assert.Null(Touche(0x43, "\u0003", ctrl: true));      // Ctrl+C
        Assert.Null(Touche(0x56, "v", ctrl: true));           // Ctrl+V : rien n'est collé
        Assert.Null(Touche(0x44, "d", win: true));            // Win+D
        Assert.Null(Touche(Touches.Tab, "\t", alt: true));    // Alt+Tab
        Assert.Null(Touche(Touches.Tab, "\t"));               // tabulation : caractère de contrôle
        Assert.Null(Touche(0xDD, ""));                        // touche morte « ^ » en attente
        Assert.Null(Touche(Touches.F4));
        Assert.Null(Touche(0x41, "abc"));
    }

    static string Lire(SecureString s)
    {
        var p = Marshal.SecureStringToGlobalAllocUnicode(s);
        try { return Marshal.PtrToStringUni(p) ?? ""; }
        finally { Marshal.ZeroFreeGlobalAllocUnicode(p); }
    }

    [Fact]
    public void TamponRempliParLeCrochet()
    {
        using var t = new TamponMotDePasse();
        foreach (var c in "mot") t.Ajouter(ActionSaisie.Caractere(c));
        t.Ajouter(ActionSaisie.Caractere('^', 'z'));
        t.Ajouter(ActionSaisie.Caractere('\uD83D', '\uDE00'));
        Assert.Equal(5, t.Longueur);
        t.Effacer();  // retire l'émoji entier
        t.Effacer();  // retire « ^z », tapé par une seule touche
        Assert.Equal(3, t.Longueur);
        t.Ajouter(new ActionSaisie(GenreSaisie.Valider));  // ignoré
        using var pris = t.Prendre();
        Assert.Equal("mot", Lire(pris));
        Assert.Equal(0, t.Longueur);
        t.Effacer();  // sans effet sur un tampon vide
        Assert.Equal(0, t.Longueur);
    }

    [Fact]
    public void TamponBorne()
    {
        using var t = new TamponMotDePasse();
        for (int i = 0; i < 300; i++) t.Ajouter(ActionSaisie.Caractere('x'));
        Assert.Equal(TamponMotDePasse.Maximum, t.Longueur);
        using var pris = t.Prendre();
        Assert.Equal(TamponMotDePasse.Maximum, pris.Length);
    }
}

/// <summary>Mots de passe faux : 4 essais libres, puis 3 s, 6 s… 2 min.</summary>
public class EchecsTests
{
    static readonly DateTime T0 = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void QuatreEssaisLibresPuisAttenteCroissante()
    {
        var c = new CompteurEchecs();
        for (int i = 0; i < 4; i++)
        {
            Assert.Null(c.Echec(T0));
            Assert.False(c.EstBloque(T0));
        }
        Assert.Equal(TimeSpan.FromSeconds(3), c.Echec(T0));
        Assert.True(c.EstBloque(T0.AddSeconds(2)));
        Assert.False(c.EstBloque(T0.AddSeconds(3)));
        Assert.Equal(TimeSpan.FromSeconds(6), c.Echec(T0.AddSeconds(10)));
        Assert.Equal(TimeSpan.FromSeconds(4), c.Restant(T0.AddSeconds(12)));
        for (int i = 0; i < 100; i++) c.Echec(T0);
        Assert.Equal(TimeSpan.FromMinutes(2), c.Restant(T0));
    }

    [Fact]
    public void LAttenteNeDependPasDeLaFenetre()
    {
        // Échap pendant la vérification ferme la fenêtre, pas le compteur : l'échec suivant compte et l'attente reste.
        var c = new CompteurEchecs();
        for (int i = 0; i < 5; i++) c.Echec(T0);
        Assert.Equal(5, c.Echecs);
        Assert.True(c.EstBloque(T0.AddSeconds(1)));
        c.Reussite();
        Assert.Equal(0, c.Echecs);
        Assert.False(c.EstBloque(T0.AddSeconds(1)));
    }
}

/// <summary>URL <c>hoverguard://</c> : nom filtré, verrouillage limité, sessions bornées.</summary>
public class UrlTests
{
    static readonly DateTime T0 = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("Claude a1b2", "Claude a1b2")]
    [InlineData("codex_1.2-b", "codex_1.2-b")]
    [InlineData("<script>alert(1)</script>", "scriptalert1script")]
    [InlineData("Zoé", "Zo")]
    [InlineData("a‮b", "ab")]   // inversion bidirectionnelle retirée
    [InlineData("  x  ", "x")]
    [InlineData("☠☠", "agent")]
    [InlineData("", "agent")]
    [InlineData(null, "agent")]
    public void NomAgentPropre(string? brut, string attendu) => Assert.Equal(attendu, Regles.NomAgentPropre(brut));

    [Fact]
    public void NomAgent40OctetsAuPlus()
    {
        Assert.Equal(40, Regles.NomAgentPropre(new string('x', 500)).Length);
        var o = Assert.IsType<Ordre.Agent>(Ordre.Lire("hoverguard://agent?nom=%E2%80%AEPayez%20ici%20https%3A%2F%2Fx&etat=attente"));
        Assert.Equal("Payez ici httpsx", o.Nom);
    }

    [Fact]
    public void VerrouillerUneFoisParMinute()
    {
        var l = new LimiteurVerrouillage();
        Assert.True(l.Autoriser(T0));
        Assert.False(l.Autoriser(T0.AddSeconds(1)));
        Assert.False(l.Autoriser(T0.AddSeconds(60)));
        Assert.True(l.Autoriser(T0.AddSeconds(61)));
        Assert.False(l.Autoriser(T0.AddSeconds(90)));
    }

    [Fact]
    public void CinquanteSessionsAuPlus()
    {
        var s = new SuiviAgents();
        for (int i = 0; i < 1000; i++) s.Signaler($"a{i}", EtatAgent.Travail, T0.AddSeconds(i));
        Assert.Equal(SuiviAgents.MaxSessions, s.Sessions.Count);
        Assert.Contains("a999", s.Sessions.Keys);
        Assert.DoesNotContain("a0", s.Sessions.Keys);  // les plus anciennes sont oubliées
        // Une session connue se met à jour sans rien évincer.
        s.Signaler("a999", EtatAgent.Attente, T0.AddSeconds(2000));
        Assert.Equal(SuiviAgents.MaxSessions, s.Sessions.Count);
    }

    [Fact]
    public void LesSessionsMuettesPartentEnPremier()
    {
        var s = new SuiviAgents();
        s.Signaler("vieille", EtatAgent.Travail, T0);
        for (int i = 1; i < SuiviAgents.MaxSessions; i++) s.Signaler($"a{i}", EtatAgent.Travail, T0.AddMinutes(30));
        s.Signaler("nouvelle", EtatAgent.Travail, T0.AddMinutes(31));
        Assert.DoesNotContain("vieille", s.Sessions.Keys);
        Assert.Contains("nouvelle", s.Sessions.Keys);
        Assert.Equal(SuiviAgents.MaxSessions, s.Sessions.Count);
    }
}

/// <summary>Toucher et stylet : ramenés au matériel même s'ils arrivent marqués « injectés ».</summary>
public class ProvenanceTests
{
    [Fact]
    public void SignatureToucher()
    {
        Assert.True(Provenance.EstMaterielle(false, 0));
        Assert.False(Provenance.EstMaterielle(true, 0));
        Assert.False(Provenance.EstMaterielle(true, 0x486F7672));
        Assert.True(Provenance.EstMaterielle(true, 0xFF515700));
        Assert.True(Provenance.EstMaterielle(true, 0xFF515780));
    }
}
