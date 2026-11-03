using System;
using System.Windows.Threading;
using Hoverguard.Coeur;

namespace Hoverguard;

/// <summary>Garder le PC éveillé pour une durée, sans poser la garde (ex-Veilleur) : session en cours et réglages.</summary>
internal sealed class ControleurEveil
{
    private readonly Reglages reglages;
    private readonly Action enregistrer;
    private readonly DispatcherTimer minuteur = new() { Interval = TimeSpan.FromSeconds(15) };
    public SessionEveil? Session { get; private set; }
    public Action SurChangement = () => { };

    public ControleurEveil(Reglages reglages, Action enregistrer)
    {
        this.reglages = reglages;
        this.enregistrer = enregistrer;
        minuteur.Tick += (_, _) => Verifier(DateTime.UtcNow, Batterie.Lire());
    }

    public bool EstActif => Session is not null;

    public bool EcranAllume
    {
        get => reglages.EcranAllume;
        set
        {
            reglages.EcranAllume = value;
            enregistrer();
            if (Session is not null) Alimentation.Eveil(true, value);
        }
    }

    public int SeuilBatterie
    {
        get => reglages.SeuilBatterie;
        set { reglages.SeuilBatterie = value; enregistrer(); }
    }

    public void Activer(Duree duree)
    {
        Demarrer(SessionEveil.Commencer(duree, DateTime.UtcNow));
    }

    private void Demarrer(SessionEveil session)
    {
        var duree = session.Duree;
        Session = session;
        reglages.DureeParDefaut = duree;
        Memoriser(session);
        Alimentation.Eveil(true, EcranAllume);
        minuteur.Start();
        Journal.Info($"Éveil activé : {duree.Libelle()}");
        SurChangement();
    }

    public void Desactiver()
    {
        Suspendre();
        if (Session is not null) Journal.Info("Éveil arrêté");
        Session = null;
        Memoriser(null);
        SurChangement();
    }

    /// <summary>Sortie de l'app sans que l'utilisateur ait arrêté l'éveil (mise à jour, extinction) : le PC peut se
    /// mettre en veille, mais la session reste mémorisée et reprend au prochain lancement.</summary>
    public void Suspendre()
    {
        minuteur.Stop();
        Alimentation.Eveil(false, false);
    }

    /// <summary>Au lancement : reprend la session mémorisée si elle n'est pas finie.</summary>
    public void Restaurer()
    {
        if (!reglages.EveilActif) return;
        var memorisee = new SessionEveil(reglages.EveilDuree, reglages.EveilFinUtc);
        if (memorisee.EstExpiree(DateTime.UtcNow)) { Memoriser(null); return; }
        Demarrer(memorisee);
    }

    private void Memoriser(SessionEveil? s)
    {
        reglages.EveilActif = s is not null;
        reglages.EveilDuree = s?.Duree ?? Duree.Indefinie;
        reglages.EveilFinUtc = s?.Fin;
        enregistrer();
    }

    /// <summary>Fin de durée ou batterie trop basse : on rend la main à la veille normale.</summary>
    public void Verifier(DateTime maintenant, EtatBatterie batterie)
    {
        if (Session is null) return;
        if (Session.EstExpiree(maintenant) || ReglesEveil.DoitArreter(batterie, SeuilBatterie)) Desactiver();
        else SurChangement();
    }
}
