namespace Hoverguard.Tests;

public class FiltreTests
{
    static Entree Humain(Genre g, bool raccourci = false, bool systeme = false) => new(true, g, raccourci, systeme);
    static Entree Claude(Genre g) => new(false, g);

    [Fact]
    public void CeQueFabriquentLesAgentsPasseToujours()
    {
        foreach (var etat in Enum.GetValues<EtatGarde>())
            foreach (var g in Enum.GetValues<Genre>())
                Assert.Equal(Decision.Laisser, Regles.Decider(Claude(g), etat));
    }

    [Fact]
    public void SousLaGardeUneToucheOuUnClicReveilleLeResteEstBloque()
    {
        Assert.Equal(Decision.Reveiller, Regles.Decider(Humain(Genre.Touche), EtatGarde.Garde));
        Assert.Equal(Decision.Reveiller, Regles.Decider(Humain(Genre.Clic), EtatGarde.Garde));
        Assert.Equal(Decision.Bloquer, Regles.Decider(Humain(Genre.Autre), EtatGarde.Garde));
        Assert.Equal(Decision.Reveiller, Regles.Decider(Humain(Genre.Touche, raccourci: true), EtatGarde.Garde));
    }

    [Fact]
    public void PendantLeMotDePasseAucuneEntreePhysiqueNAtteintLesApps()
    {
        Assert.Equal(Decision.Saisir, Regles.Decider(Humain(Genre.Touche), EtatGarde.Authentification));
        Assert.Equal(Decision.Saisir, Regles.Decider(Humain(Genre.Touche, systeme: true), EtatGarde.Authentification));
        Assert.Equal(Decision.Bloquer, Regles.Decider(Humain(Genre.Autre), EtatGarde.Authentification));
        Assert.Equal(Decision.Bloquer, Regles.Decider(Humain(Genre.Clic), EtatGarde.Authentification));
    }

    [Fact]
    public void HorsGardeSeulLeRaccourciPhysiquePoseLaGarde()
    {
        Assert.Equal(Decision.Poser, Regles.Decider(Humain(Genre.Touche, raccourci: true), EtatGarde.Libre));
        Assert.Equal(Decision.Laisser, Regles.Decider(Humain(Genre.Touche), EtatGarde.Libre));
        Assert.Equal(Decision.Laisser, Regles.Decider(Humain(Genre.Clic), EtatGarde.Libre));
        Assert.Equal(Decision.Laisser, Regles.Decider(new Entree(false, Genre.Touche, EstRaccourci: true), EtatGarde.Libre));
    }

    [Fact]
    public void RaccourciCtrlWinW()
    {
        Assert.True(Regles.EstRaccourci(Touches.W, ctrl: true, win: true, alt: false, maj: false));
        Assert.False(Regles.EstRaccourci(Touches.W, ctrl: false, win: true, alt: false, maj: false));
        Assert.False(Regles.EstRaccourci(Touches.W, ctrl: true, win: false, alt: false, maj: false));
        Assert.False(Regles.EstRaccourci(Touches.W, ctrl: true, win: true, alt: true, maj: false));  // AltGr ne pose rien
        Assert.False(Regles.EstRaccourci(Touches.W, ctrl: true, win: true, alt: false, maj: true));
        Assert.False(Regles.EstRaccourci(0x5A, ctrl: true, win: true, alt: false, maj: false));
    }

    [Fact]
    public void CommandesSystemeBloqueesPendantLeMotDePasseMaisPasAltGr()
    {
        Assert.True(Regles.EstCommandeSysteme(Touches.WinGauche, false, false, false, false));
        Assert.True(Regles.EstCommandeSysteme(0x44, false, false, false, win: true));     // Win+D
        Assert.True(Regles.EstCommandeSysteme(Touches.Tab, false, alt: true, false, false));
        Assert.True(Regles.EstCommandeSysteme(Touches.F4, false, alt: true, false, false));
        Assert.True(Regles.EstCommandeSysteme(Touches.Echap, ctrl: true, false, maj: true, false));
        Assert.False(Regles.EstCommandeSysteme(0x30, ctrl: true, alt: true, false, false)); // AltGr+à = @
        Assert.False(Regles.EstCommandeSysteme(0x41, false, false, maj: true, false));
        Assert.False(Regles.EstCommandeSysteme(Touches.Tab, false, false, false, false));
    }

    [Fact]
    public void EssaisDeMotDePasseGenereux()
    {
        Assert.All(Enumerable.Range(1, 4), n => Assert.Null(ReglesGarde.AttenteApres(n)));
        Assert.Equal(TimeSpan.FromSeconds(3), ReglesGarde.AttenteApres(5));
        Assert.Equal(TimeSpan.FromSeconds(9), ReglesGarde.AttenteApres(7));
        Assert.Equal(TimeSpan.FromSeconds(120), ReglesGarde.AttenteApres(44));
        Assert.Equal(TimeSpan.FromSeconds(120), ReglesGarde.AttenteApres(1000));
    }
}

public class SuiviEntreesTests
{
    [Fact]
    public void LeRaccourciPoseUneSeuleFoisMalgreLaRepetition()
    {
        var s = new SuiviEntrees();
        Assert.Equal(Decision.Laisser, s.Clavier(Touches.CtrlGauche, true, false, EtatGarde.Libre).Decision);
        Assert.Equal(Decision.Laisser, s.Clavier(Touches.WinGauche, true, false, EtatGarde.Libre).Decision);
        var premier = s.Clavier(Touches.W, true, false, EtatGarde.Libre);
        Assert.Equal(new Verdict(Decision.Poser, false), premier);
        // Windows répète l'appui tant que la touche est tenue ; la garde est déjà posée entre-temps.
        Assert.True(s.Clavier(Touches.W, true, false, EtatGarde.Garde).Repetition);
        // W n'est jamais passé : son relâchement est bloqué. Ctrl et Win étaient passés : leur relâchement passe,
        // sinon Windows les croirait toujours enfoncés et chaque lettre d'un agent deviendrait un raccourci.
        Assert.Equal(Decision.Bloquer, s.Clavier(Touches.W, false, false, EtatGarde.Garde).Decision);
        Assert.Equal(Decision.Laisser, s.Clavier(Touches.WinGauche, false, false, EtatGarde.Garde).Decision);
        Assert.Equal(Decision.Laisser, s.Clavier(Touches.CtrlGauche, false, false, EtatGarde.Garde).Decision);
        Assert.False(s.Ctrl || s.Win);
    }

    [Fact]
    public void LesModificateursInjectesNeComptentPas()
    {
        var s = new SuiviEntrees();
        s.Clavier(Touches.CtrlGauche, true, injectee: true, EtatGarde.Libre);
        s.Clavier(Touches.WinGauche, true, injectee: true, EtatGarde.Libre);
        Assert.Equal(Decision.Laisser, s.Clavier(Touches.W, true, false, EtatGarde.Libre).Decision);
    }

    [Fact]
    public void SousLaGardeUnModificateurSeulNeReveillePas()
    {
        var s = new SuiviEntrees();
        Assert.Equal(Decision.Bloquer, s.Clavier(Touches.MajGauche, true, false, EtatGarde.Garde).Decision);
        Assert.Equal(Decision.Reveiller, s.Clavier(0x41, true, false, EtatGarde.Garde).Decision);
        Assert.Equal(Decision.Laisser, s.Clavier(0x41, true, injectee: true, EtatGarde.Garde).Decision);
    }

    [Fact]
    public void PendantLeMotDePasseToutEstAvaleEtLesModificateursSontSuivis()
    {
        var s = new SuiviEntrees();
        var a = EtatGarde.Authentification;
        // AltGr = Ctrl gauche + Alt droite : avalés, mais retenus pour traduire « à » en « @ ».
        Assert.Equal(Decision.Bloquer, s.Clavier(Touches.CtrlGauche, true, false, a).Decision);
        Assert.Equal(Decision.Bloquer, s.Clavier(Touches.AltDroite, true, false, a).Decision);
        Assert.True(s.Ctrl && s.Alt);
        Assert.Contains(Touches.AltDroite, s.Tenues);
        Assert.Equal(Decision.Saisir, s.Clavier(0x30, true, false, a).Decision);
        Assert.Equal(Decision.Bloquer, s.Clavier(0x30, false, false, a).Decision);
        s.Clavier(Touches.CtrlGauche, false, false, a);
        s.Clavier(Touches.AltDroite, false, false, a);
        Assert.False(s.Ctrl || s.Alt);
        Assert.Equal(Decision.Saisir, s.Clavier(Touches.WinGauche, true, false, a).Decision);
        Assert.Equal(Decision.Saisir, s.Clavier(0x44, true, false, a).Decision);  // Win+D : avalé, rien n'est tapé
        // Ce que fabriquent les agents passe toujours, même pendant la saisie.
        Assert.Equal(Decision.Laisser, s.Clavier(0x41, true, injectee: true, a).Decision);
    }

    [Fact]
    public void ToucheTenueAvantLaDemandeEstRelachePourWindows()
    {
        var s = new SuiviEntrees();
        Assert.Equal(Decision.Laisser, s.Clavier(Touches.CtrlGauche, true, false, EtatGarde.Libre).Decision);
        Assert.Equal(Decision.Laisser, s.Clavier(Touches.CtrlGauche, false, false, EtatGarde.Authentification).Decision);
    }

    [Fact]
    public void ToucheMultimediaAvaleeSansReveiller()
    {
        var s = new SuiviEntrees();
        foreach (var vk in new[] { 0xAD, 0xAE, 0xAF, 0xB0, 0xB3, 0xA6, 0xB7, Touches.Veille })
        {
            Assert.True(Touches.EstMultimedia(vk));
            Assert.Equal(Decision.Bloquer, s.Clavier(vk, true, false, EtatGarde.Garde).Decision);
            Assert.Equal(Decision.Bloquer, s.Clavier(vk, false, false, EtatGarde.Garde).Decision);
            Assert.Equal(Decision.Bloquer, s.Clavier(vk, true, false, EtatGarde.Authentification).Decision);
            s.Clavier(vk, false, false, EtatGarde.Authentification);
            Assert.Equal(Decision.Laisser, s.Clavier(vk, true, false, EtatGarde.Libre).Decision);
            s.Clavier(vk, false, false, EtatGarde.Libre);
        }
        Assert.False(Touches.EstMultimedia(0x41));
        Assert.False(Touches.EstMultimedia(Touches.MajDroite));
    }

    [Fact]
    public void SourisBoutonTenuAvantLaGardeEstRelachePourWindows()
    {
        var s = new SuiviEntrees();
        Assert.Equal(Decision.Laisser, s.Souris(0, true, false, EtatGarde.Libre).Decision);
        // La garde est posée (URL d'un agent) pendant que le bouton est tenu : le relâchement passe quand même.
        Assert.Equal(Decision.Laisser, s.Souris(0, false, false, EtatGarde.Garde).Decision);
        Assert.Equal(Decision.Reveiller, s.Souris(0, true, false, EtatGarde.Garde).Decision);
        Assert.Equal(Decision.Bloquer, s.Souris(0, false, false, EtatGarde.Garde).Decision);
        Assert.Equal(Decision.Bloquer, s.Souris(null, false, false, EtatGarde.Garde).Decision);
        Assert.Equal(Decision.Laisser, s.Souris(null, false, injectee: true, EtatGarde.Garde).Decision);
        Assert.Equal(Decision.Laisser, s.Souris(1, true, injectee: true, EtatGarde.Garde).Decision);
        Assert.Equal(Decision.Bloquer, s.Souris(1, true, false, EtatGarde.Authentification).Decision);
    }

    [Fact]
    public void GardeAutomatiqueSurInactivitePhysique()
    {
        Assert.True(ReglesGardeAuto.DoitPoser(EtatGarde.Libre, 15, 1, TimeSpan.FromMinutes(16)));
        Assert.False(ReglesGardeAuto.DoitPoser(EtatGarde.Libre, 15, 1, TimeSpan.FromMinutes(14)));
        Assert.False(ReglesGardeAuto.DoitPoser(EtatGarde.Libre, 15, 0, TimeSpan.FromHours(3)));
        Assert.True(ReglesGardeAuto.DoitPoser(EtatGarde.Libre, 15, 0, TimeSpan.FromMinutes(16), memeSansAgent: true));
        Assert.False(ReglesGardeAuto.DoitPoser(EtatGarde.Garde, 15, 0, TimeSpan.FromHours(3), memeSansAgent: true));
        Assert.True(new Reglages().GardeAutoSansAgent);
        Assert.False(ReglesGardeAuto.DoitPoser(EtatGarde.Libre, 0, 3, TimeSpan.FromHours(3)));
        Assert.False(ReglesGardeAuto.DoitPoser(EtatGarde.Garde, 15, 1, TimeSpan.FromHours(3)));
    }

    [Fact]
    public void DroneSePoseQuelquesSecondesToutesLesCinqMinutes()
    {
        var r = new Random(1);
        for (int i = 0; i < 200; i++)
        {
            Assert.InRange(ReglesVol.Attente(r).TotalSeconds, 240, 360);
            Assert.InRange(ReglesVol.Pause(r).TotalSeconds, 4, 8);
        }
    }
}
