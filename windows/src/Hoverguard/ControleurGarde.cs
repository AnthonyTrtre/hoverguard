using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Hoverguard.Coeur;
using Microsoft.Win32;

namespace Hoverguard;

/// <summary>Garde de l'écran : voile, filtre des entrées physiques, identification, PC éveillé, agents.
/// Même logique que <c>macos/Sources/ControleurGarde.swift</c>. Tout se passe sur le fil de l'interface.</summary>
internal sealed class ControleurGarde
{
    public EtatGarde Etat { get; private set; } = EtatGarde.Libre;
    public SuiviAgents Agents { get; } = new();
    /// <summary>Agents repérés parmi les processus du PC au dernier relevé (<see cref="Detecteur"/>).</summary>
    public IReadOnlyList<AgentDetecte> Detectes { get; private set; } = [];
    private readonly Detecteur detecteur = new();
    /// <summary>Agents principaux au travail : le plus grand des deux comptes (détection des processus, hooks).</summary>
    /// <summary>Conversations au travail d'après leurs journaux (Claude, Codex, Gemini), sous-agents compris.</summary>
    public ReleveJournaux Journaux { get; private set; } = ReleveJournaux.Vide;
    public int PrincipauxActifs => Math.Max(ReglesDetection.Principaux(Agents.Actifs().Count, Detectes), Journaux.TotalPrincipaux);
    public int SousAgentsActifs => Math.Max(Agents.SousAgents(), Journaux.SousAgents);
    public int TotalActifs => PrincipauxActifs + SousAgentsActifs;
    public readonly Voile Voile = new();
    public readonly Filtre Filtre = new();
    private readonly Reglages reglages;
    private readonly Action enregistrer;
    private readonly Dispatcher interface_;
    private readonly DispatcherTimer surveillance = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly DispatcherTimer premierPlan = new() { Interval = TimeSpan.FromSeconds(2) };
    /// <summary>Écrans changés, réveil : on attend que les changements se calment avant de reposer le voile.</summary>
    private readonly DispatcherTimer reposer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private FenetreIdentification? identification;
    private bool verification;
    /// <summary>Le mot de passe Hoverguard (empreinte) : lui seul lève la garde. Sans lui, la garde ne se pose pas.</summary>
    public MotDePasseHoverguard? MotDePasseApp { get; set; } = MotDePasseHoverguard.Charger(Dossiers.MotDePasse);
    /// <summary>Appelé quand on veut poser la garde sans avoir choisi de mot de passe.</summary>
    public Action? MotDePasseManquant { get; set; }
    /// <summary>Mot de passe rempli par le crochet clavier : aucune frappe physique n'atteint les apps.</summary>
    private readonly TamponMotDePasse motDePasse = new();
    private readonly CompteurEchecs echecs = new();
    private readonly LimiteurVerrouillage limiteurUrl = new();
    private readonly GardienLuminosite luminosite = new();
    private string phraseDuJour = Textes.Phrases[0];
    private JournalAbsence? journal;
    /// <summary>Dernier rapport de garde, consultable depuis le menu (« Rapport de la dernière garde… »).</summary>
    public JournalAbsence? DernierRapport { get; private set; }
    private DateTime finDernierRapport = DateTime.Now;
    private FenetreRapport? fenetreRapport;
    private readonly LimiteurAlertes limiteur = new();
    private bool enPose;
    public Action SurChangement = () => { };

    public ControleurGarde(Reglages reglages, Action enregistrer)
    {
        this.reglages = reglages;
        this.enregistrer = enregistrer;
        interface_ = Dispatcher.CurrentDispatcher;
        Filtre.SurPoser = () => interface_.BeginInvoke(() => { Filtre.Masquer(); _ = Verrouiller(); });
        Filtre.SurReveil = () => interface_.BeginInvoke(Reveiller);
        Filtre.SurPanne = () => interface_.BeginInvoke(PanneDuFiltre);
        Filtre.SurSaisie = a => interface_.BeginInvoke(() => Saisir(a));
        surveillance.Tick += (_, _) => Surveiller();
        premierPlan.Tick += (_, _) =>
        {
            Voile.Replacer();
            if (Etat == EtatGarde.Authentification && identification is { Hwnd: var h } && h != IntPtr.Zero)
                Natif.SetWindowPos(h, Natif.HWND_TOPMOST, 0, 0, 0, 0, Natif.SWP_NOMOVE | Natif.SWP_NOSIZE | Natif.SWP_NOACTIVATE);
            if (Etat == EtatGarde.Libre) return;
            Filtre.Sonder();
            luminosite.Verifier();
            // Le Narrateur lirait à voix haute ce qui est sous le voile : la garde n'est plus garantie.
            if (NatifGarde.LecteurEcranActif()) GarantirParLeSysteme("Narrateur ou lecteur d'écran activé");
        };
        reposer.Tick += (_, _) => { reposer.Stop(); _ = ReposerLeVoile(); };
    }

    private string Phrase => reglages.Phrase.Length == 0 ? phraseDuJour : reglages.Phrase;

    public void Demarrer()
    {
        if (TemoinGarde.Present)
        {
            // Plantage ou arrêt forcé pendant la garde : on ne laisse pas le PC ouvert.
            TemoinGarde.Retirer();
            Journal.Erreur("Hoverguard s'était arrêté pendant la garde : session Windows verrouillée");
            NatifGarde.VerrouillerSession();
        }
        Camera.Purger();
        if (!Filtre.Demarrer()) Journal.Erreur("Filtre d'entrées indisponible");
        // Écran branché, résolution changée, réveil de veille : le voile est reposé sur chaque écran.
        SystemEvents.DisplaySettingsChanged += (_, _) => interface_.BeginInvoke(DemanderRepose);
        SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == PowerModes.Resume) interface_.BeginInvoke(DemanderRepose);
        };
        surveillance.Start();
        Detecter();  // premier relevé : la référence des temps processeur
    }

    public async Task Verrouiller()
    {
        if (Etat != EtatGarde.Libre || enPose) return;
        if (MotDePasseApp is null)
        {
            Journal.Info("Garde non posée : aucun mot de passe Hoverguard choisi");
            MotDePasseManquant?.Invoke();
            return;
        }
        if (!Filtre.Actif && !Filtre.Demarrer())
        {
            Avertir("Filtre clavier et souris indisponible",
                "Windows a refusé les crochets clavier et souris : Hoverguard ne peut pas bloquer les entrées. Rien n'a été posé.");
            return;
        }
        enPose = true;
        try
        {
            phraseDuJour = Textes.Phrases[Random.Shared.Next(Textes.Phrases.Count)];
            Rafraichir(voile: false);
            void Garder()
            {
                Etat = EtatGarde.Garde;
                Filtre.Etat = Etat;
            }
            if (!await Voile.Poser(fenetresPosees: Garder))
            {
                Etat = EtatGarde.Libre;
                Filtre.Etat = Etat;
                Avertir("Windows 10 version 2004 ou plus récent nécessaire",
                    "Windows a refusé d'exclure le voile des captures d'écran : les agents ne verraient qu'un écran noir. Mettez Windows à jour.");
                return;
            }
            if (Etat == EtatGarde.Libre) return;  // levée entre-temps (garde non garantie)
            Alimentation.Garde(true);
            journal = new JournalAbsence();
            echecs.Reussite();
            luminosite.Noter();
            if (!Voile.Apercu)
            {
                TemoinGarde.Poser();
                Gardien.Lancer();
            }
            premierPlan.Start();
            Rafraichir();
            Journal.Info("Garde posée");
            if (!Voile.Apercu) Pro.DemarrerEssaiSiPremiereGarde();
            if (Voile.Apercu) _ = Task.Delay(12_000).ContinueWith(_ => interface_.BeginInvoke(Deverrouiller));  // l'aperçu ne dure pas
            SurChangement();
        }
        finally { enPose = false; }
    }

    public void Deverrouiller()
    {
        if (Etat == EtatGarde.Libre) return;
        Etat = EtatGarde.Libre;
        Filtre.Etat = Etat;
        FermerIdentification();
        premierPlan.Stop();
        luminosite.Oublier();
        Voile.Etat.Approche = false;
        Voile.Retirer();
        Alimentation.Garde(false);
        TemoinGarde.Retirer();
        Journal.Info("Garde levée");
        SurChangement();
        var j = journal;
        journal = null;
        if (j is null) return;
        DernierRapport = j;
        finDernierRapport = DateTime.Now;
        // Pas de fenêtre au déverrouillage par défaut (comme sur macOS) : le rapport attend dans le menu.
        if (reglages.RapportAuRetour) MontrerDernierRapport();
    }

    public void MontrerDernierRapport()
    {
        if (DernierRapport is not JournalAbsence j) return;
        fenetreRapport?.Close();
        fenetreRapport = new FenetreRapport(j, finDernierRapport);
        fenetreRapport.Closed += (_, _) => fenetreRapport = null;
        fenetreRapport.Show();
    }

    /// <summary>Arrêt de Windows ou fermeture de session pendant la garde : ce n'est pas un plantage, le témoin est
    /// retiré pour que le prochain lancement ne verrouille pas la session.</summary>
    public void FinDeSession() => TemoinGarde.Retirer();

    /// <summary>Une touche ou un clic sous la garde : le drone fonce vers l'utilisateur et demande le mot de passe
    /// Hoverguard. La fenêtre d'identification ne prend pas le clavier : le crochet le lit pour elle.</summary>
    private void Reveiller()
    {
        if (Etat != EtatGarde.Garde) return;
        Etat = EtatGarde.Authentification;
        Filtre.Etat = Etat;
        journal?.Reveil();
        Voile.Etat.Approche = true;
        Voile.Actualiser();
        motDePasse.Vider();
        identification = new FenetreIdentification();
        identification.Montrer();
        _ = AfficherAttente();  // attente imposée encore en cours après des mots de passe faux
        SurChangement();
    }

    /// <summary>Échap ou une minute sans rien taper : le drone retourne surveiller.</summary>
    public void AnnulerReveil()
    {
        if (Etat != EtatGarde.Authentification) return;
        Etat = EtatGarde.Garde;
        Filtre.Etat = Etat;
        FermerIdentification();
        Voile.Etat.Approche = false;
        Voile.Actualiser();
        SurChangement();
    }

    private void FermerIdentification()
    {
        motDePasse.Vider();
        if (identification is null) return;
        identification.Close();
        identification = null;
    }

    /// <summary>Touches lues par le crochet pendant la demande de mot de passe.</summary>
    private void Saisir(ActionSaisie action)
    {
        if (Etat != EtatGarde.Authentification || identification is not { } f) return;
        f.Activite();
        bool occupe = verification || echecs.EstBloque(DateTime.UtcNow);
        switch (action.Genre)
        {
            case GenreSaisie.Caractere: if (!occupe) motDePasse.Ajouter(action); break;
            case GenreSaisie.Effacer: if (!occupe) motDePasse.Effacer(); break;
            case GenreSaisie.Valider: _ = ValiderMotDePasse(); break;
            case GenreSaisie.Annuler: AnnulerReveil(); return;
        }
        f.Points = motDePasse.Longueur;
    }

    private async Task ValiderMotDePasse()
    {
        var f = identification;
        if (Etat != EtatGarde.Authentification || f is null || verification || echecs.EstBloque(DateTime.UtcNow)) return;
        if (motDePasse.Longueur == 0) return;
        verification = true;
        f.Occupe = true;
        f.Erreur = null;
        bool ok;
        var attendu = MotDePasseApp;
        using (var saisi = motDePasse.Prendre()) ok = attendu is not null && await Task.Run(() => attendu.Verifier(saisi));
        verification = false;
        f.Points = 0;
        if (ok)
        {
            if (Etat != EtatGarde.Authentification) return;
            echecs.Reussite();
            Deverrouiller();
            return;
        }
        // L'échec compte même si quelqu'un a appuyé sur Échap pendant la vérification.
        var attente = echecs.Echec(DateTime.UtcNow);
        journal?.MotDePasseFaux();
        if (journal?.FautPhotographier == true) _ = PhotographierIntrus();
        if (Etat != EtatGarde.Authentification || identification != f) return;
        // 4 essais libres, puis une attente croissante (3 s, 6 s… 2 min au plus), portée par la garde : Échap puis
        // un nouveau réveil ne la raccourcit pas.
        if (attente is null) { f.Occupe = false; f.Erreur = "Mot de passe incorrect"; return; }
        await AfficherAttente();
    }

    /// <summary>Pendant une attente imposée, la fenêtre affiche le temps restant puis redevient disponible.</summary>
    private async Task AfficherAttente()
    {
        var f = identification;
        var reste = echecs.Restant(DateTime.UtcNow);
        if (f is null || reste <= TimeSpan.Zero) return;
        f.Occupe = true;
        f.Erreur = $"Mot de passe incorrect. Patientez {(int)Math.Ceiling(reste.TotalSeconds)} s.";
        await Task.Delay(reste);
        if (identification != f) return;
        f.Occupe = false;
        f.Erreur = "Mot de passe incorrect";
    }

    private async Task PhotographierIntrus()
    {
        Alerter(Alerte.Intrus, $"{journal?.MotsDePasseFaux ?? 0} mots de passe faux");
        if (!reglages.PhotoIntrus) return;
        if (await Camera.Photographier() is string chemin) journal?.Photo(chemin);
    }

    private void DemanderRepose()
    {
        if (Etat == EtatGarde.Libre) return;
        reposer.Stop();
        reposer.Start();
    }

    private async Task ReposerLeVoile()
    {
        if (Etat == EtatGarde.Libre) return;
        if (enPose) { reposer.Start(); return; }  // une pose est en cours : on réessaie ensuite
        enPose = true;
        try
        {
            Journal.Info("Écrans changés ou réveil : voile reposé");
            if (!await Voile.Poser()) { GarantirParLeSysteme("voile impossible à reposer"); return; }
            if (Etat == EtatGarde.Libre) { Voile.Retirer(); return; }
            Rafraichir();
            if (identification is { Hwnd: var h } && h != IntPtr.Zero)
                Natif.SetWindowPos(h, Natif.HWND_TOPMOST, 0, 0, 0, 0, Natif.SWP_NOMOVE | Natif.SWP_NOSIZE | Natif.SWP_NOACTIVATE);
        }
        finally { enPose = false; }
    }

    private void Surveiller()
    {
        // Personne ne tape depuis une minute : retour à la garde.
        if (Etat == EtatGarde.Authentification && identification is not null && !verification
            && DateTime.UtcNow - identification.DerniereActivite > ReglesGarde.SaisieAbandonnee)
            AnnulerReveil();
        Detecter();
        journal?.SousAgents(SousAgentsActifs);
        var b = Batterie.Lire();
        if (Etat != EtatGarde.Libre && !b.SurSecteur && b.Niveau is int n && n <= 20) Alerter(Alerte.BatterieFaible, $"Plus que {n} % de batterie.");
        if (ReglesGardeAuto.DoitPoser(Etat, reglages.GardeAutoMinutes, TotalActifs, Filtre.InactivitePhysique))
        {
            Journal.Info($"Garde posée automatiquement après {reglages.GardeAutoMinutes} min d'inactivité");
            _ = Verrouiller();
        }
        Rafraichir();
    }

    /// <summary>Relève les processus hors du fil de l'interface, puis met à jour compteur, voile et icône.</summary>
    private void Detecter()
        => RelevesDetection.Lancer(detecteur, (releve, journaux) => interface_.BeginInvoke(() =>
        {
            Detectes = releve;
            Journaux = journaux;
            Rafraichir();
            SurChangement();
        }));

    /// <summary>Sans filtre, plus rien ne bloque ni ne réveille : on ne lève pas la garde en laissant le PC ouvert, on
    /// verrouille la vraie session Windows (les agents en ligne de commande continuent ; le propriétaire entre avec
    /// son mot de passe Windows).</summary>
    private void PanneDuFiltre()
    {
        if (Etat == EtatGarde.Libre) return;
        GarantirParLeSysteme("crochet clavier ou souris retiré par le système");
    }

    private void GarantirParLeSysteme(string raison)
    {
        if (Etat == EtatGarde.Libre) return;
        Journal.Erreur($"Garde non garantie ({raison}) : session Windows verrouillée");
        NatifGarde.VerrouillerSession();
        journal = null;  // pas de rapport : la session verrouillée prend le relais
        Deverrouiller();
    }

    /// <summary>Notifications téléphone : emplacement réservé. TODO : aucun canal local sous Windows pour l'instant
    /// (pas d'iMessage) ; on note seulement l'alerte dans le journal.</summary>
    private void Alerter(Alerte alerte, string detail)
    {
        if (!reglages.Notifications || !limiteur.Autoriser(alerte)) return;
        Journal.Info($"Alerte non envoyée (pas de canal) : {alerte.Texte(detail)}");
    }

    public void Recevoir(Ordre ordre)
    {
        switch (ordre)
        {
            case Ordre.Agent(var nom, var e):
                int avant = TotalActifs;
                Agents.Signaler(nom, e);
                if (e == EtatAgent.Fini && Etat != EtatGarde.Libre) journal?.AgentTermine(nom);
                if (e == EtatAgent.Attente) Alerter(Alerte.AgentAttend, "Un agent");  // jamais le nom reçu par URL
                if (e == EtatAgent.Fini && avant > 0 && TotalActifs == 0)
                    Alerter(Alerte.AgentsFinis, journal is null ? "" : $"{journal.AgentsTermines.Count} terminé(s) pendant ton absence.");
                Rafraichir();
                SurChangement();
                break;
            case Ordre.Verrouiller:
                // Une app ou une page peut ouvrir l'URL en boucle pour gêner : une fois par minute au plus.
                if (!limiteurUrl.Autoriser(DateTime.UtcNow)) { Journal.Info("hoverguard://verrouiller ignoré (moins d'une minute)"); break; }
                _ = Verrouiller();
                break;
            case Ordre.ActiverLicence(var cle):
                Pro.ActiverDepuisLien(cle, afficher: Etat == EtatGarde.Libre);
                break;
        }
    }

    private void Rafraichir(bool voile = true)
    {
        int actifs = PrincipauxActifs, sous = SousAgentsActifs;
        var v = Voile.Etat;
        v.Phrase = Phrase;
        v.AgentsActifs = actifs + sous;
        v.Compteur = Textes.Compteur(actifs, sous);
        v.Equipe = Textes.Equipe(actifs + sous);
        v.Batterie = Etat == EtatGarde.Libre ? null : Textes.Batterie(Batterie.Lire());
        if (voile) Voile.Actualiser();
    }

    public void PhraseChangee() => Rafraichir();

    private static void Avertir(string titre, string texte)
        => MessageBox.Show(texte, titre, MessageBoxButton.OK, MessageBoxImage.Warning);
}
