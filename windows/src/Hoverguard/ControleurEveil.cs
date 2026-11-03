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
        Session = SessionEveil.Commencer(duree, DateTime.UtcNow);
        reglages.DureeParDefaut = duree;
        enregistrer();
        Alimentation.Eveil(true, EcranAllume);
        minuteur.Start();
        Journal.Info($"Éveil activé : {duree.Libelle()}");
        SurChangement();
    }

    public void Desactiver()
    {
        minuteur.Stop();
        Alimentation.Eveil(false, false);
        if (Session is not null) Journal.Info("Éveil arrêté");
        Session = null;
        SurChangement();
    }

    /// <summary>Fin de durée ou batterie trop basse : on rend la main à la veille normale.</summary>
    public void Verifier(DateTime maintenant, EtatBatterie batterie)
    {
        if (Session is null) return;
        if (Session.EstExpiree(maintenant) || ReglesEveil.DoitArreter(batterie, SeuilBatterie)) Desactiver();
        else SurChangement();
    }
}
