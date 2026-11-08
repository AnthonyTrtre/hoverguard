using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Hoverguard.Coeur;
using Microsoft.Win32;
using Velopack;
using Forms = System.Windows.Forms;

namespace Hoverguard;

internal static class Programme
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Velopack d'abord : l'installateur, les mises à jour et la désinstallation lancent l'exe avec leurs arguments.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => Inscriptions.Retirer())
            .Run();
        if (args.Length >= 2 && args[0] == Gardien.Argument && int.TryParse(args[1], out int pid))
            return Gardien.Veiller(pid);
        if (args.Length >= 1 && args[0] == "--autotest")
            return AutoTest.Lancer(args.Length >= 2 ? args[1] : "autotest");

        var url = args.FirstOrDefault(a => a.StartsWith("hoverguard:", StringComparison.OrdinalIgnoreCase));
        using var canal = new Canal();
        if (!canal.EstPremier)
        {
            // Hoverguard tourne déjà : on lui passe l'URL (hook d'un agent) et on s'arrête.
            if (url is not null) Canal.Transmettre(url);
            // Relancé à la main (double-clic, menu Démarrer) : l'instance en cours se montre.
            else if (ReglesLancement.LanceALaMain(args)) Canal.Transmettre(ReglesLancement.Montrer);
            return 0;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // Une erreur imprévue ne doit jamais fermer Hoverguard pendant une garde : elle est notée dans le journal et
        // l'app continue. (Si elle s'arrête quand même, le gardien verrouille la session.)
        // Chaque erreur laisse aussi plantage.txt, proposé au lancement suivant (jamais envoyé sans un clic).
        app.DispatcherUnhandledException += (_, e) =>
        {
            Journal.Erreur($"Erreur inattendue : {e.Exception}");
            Plantage.Noter(e.Exception, fatal: false);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Journal.Erreur($"Erreur fatale : {e.ExceptionObject}");
            if (e.ExceptionObject is Exception ex) Plantage.Noter(ex, fatal: e.IsTerminating);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Journal.Erreur($"Erreur en arrière-plan : {e.Exception}");
            Plantage.Noter(e.Exception, fatal: false);
            e.SetObserved();
        };
        var hote = new Hote(app);
        canal.Ecouter(texte => app.Dispatcher.BeginInvoke(() => hote.RecevoirUrl(texte)));
        app.Startup += (_, _) =>
        {
            hote.Demarrer();
            if (url is not null) hote.RecevoirUrl(url);
            else if (ReglesLancement.LanceALaMain(args)) hote.Montrer();
        };
        // Arrêt de Windows ou fermeture de session : seul cas où Hoverguard s'arrête pendant la garde.
        app.SessionEnding += (_, _) => hote.FinDeSession();
        app.Exit += (_, _) => hote.Arreter();
        return app.Run();
    }
}

/// <summary>L'application : icône près de l'horloge et son menu (clic gauche ou droit), contrôleurs, réglages.</summary>
internal sealed class Hote
{
    private readonly Application app;
    private readonly Reglages reglages = Reglages.Charger(Dossiers.Reglages);
    private readonly ControleurGarde garde;
    private readonly ControleurEveil eveil;
    private readonly Forms.NotifyIcon icone = new() { Text = "Hoverguard" };
    private readonly Forms.ContextMenuStrip menu = new() { ShowCheckMargin = true, ShowImageMargin = false };
    private FenetreReglages? fenetreReglages;
    private bool quitterDemande;
    private readonly MisesAJour misesAJour = new();
    // Première recherche 2 minutes après le lancement, puis un passage toutes les 30 minutes (recherche au plus 1 fois
    // par jour ; le reste du temps, on regarde seulement si une mise à jour prête peut s'installer).
    private readonly DispatcherTimer minuterieMaj = new() { Interval = TimeSpan.FromMinutes(2) };
    private DateTime derniereRecherche = DateTime.MinValue;
    /// <summary>Rapport de plantage du lancement précédent : proposé écran libre seulement, jamais sous la garde.</summary>
    private string? plantage;

    public Hote(Application app)
    {
        this.app = app;
        garde = new ControleurGarde(reglages, Enregistrer);
        eveil = new ControleurEveil(reglages, Enregistrer);
    }

    private void Enregistrer()
    {
        try { reglages.Enregistrer(Dossiers.Reglages); }
        catch (Exception e) { Journal.Erreur($"Réglages : {e.Message}"); }
    }

    public void Demarrer()
    {
        Forms.Application.EnableVisualStyles();
        Inscriptions.InscrireProtocole();
        Pro.AccorderProLancementSiEdition();
        if (!reglages.PremierLancementFait)
        {
            Inscriptions.OuvertureAuDemarrage = true;
            reglages.PremierLancementFait = true;
            Enregistrer();
        }
        menu.Opening += (_, _) => Construire();
        icone.ContextMenuStrip = menu;
        icone.MouseClick += (_, e) =>
        {
            // Le menu s'ouvre aussi au clic gauche, comme dans la barre des menus du Mac.
            if (e.Button == Forms.MouseButtons.Left)
                typeof(Forms.NotifyIcon).GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(icone, null);
        };
        garde.SurChangement = () => { RafraichirIcone(); app.Dispatcher.BeginInvoke(ProposerPlantage); };
        garde.MotDePasseManquant = DemanderMotDePasse;
        eveil.SurChangement = RafraichirIcone;
        eveil.Restaurer();
        garde.Demarrer();
        // Mode Windows clair ou sombre changé : l'icône de notification suit la couleur de la barre des tâches.
        SystemEvents.UserPreferenceChanged += ThemeChange;
        RafraichirIcone();
        icone.Visible = true;
        minuterieMaj.Tick += (_, _) => _ = MiseAJourAutomatique();
        minuterieMaj.Start();
        Journal.Info($"Hoverguard démarré ({Environment.OSVersion.Version})");
        if (garde.MotDePasseApp is null) DemanderMotDePasse();
        plantage = Plantage.Chercher();
        app.Dispatcher.BeginInvoke(ProposerPlantage);
    }

    private void ProposerPlantage()
    {
        if (plantage is not string fichier || garde.Etat != EtatGarde.Libre) return;
        plantage = null;
        Plantage.Proposer(fichier);
    }

    public void FinDeSession() => garde.FinDeSession();

    /// <summary>Lancé à la main : sans fenêtre, l'icône (souvent cachée derrière la flèche ^ de la zone de
    /// notification) laisserait croire que rien ne s'est passé. Jamais sous la garde.</summary>
    public void Montrer()
    {
        if (garde.Etat != EtatGarde.Libre) return;
        Bulle("Hoverguard est lancé", $"Son icône est près de l'horloge (sous la flèche ^ si elle est cachée). {Regles.Raccourci} pose la garde.");
        Reglages_(FenetreReglages.Onglet.Garde);
    }

    /// <summary>Premier lancement (ou garde demandée sans mot de passe) : les réglages s'ouvrent sur le choix du mot de
    /// passe Hoverguard, sans lequel la garde ne se pose pas.</summary>
    private void DemanderMotDePasse()
    {
        Bulle("Choisissez votre mot de passe Hoverguard", "C'est lui qui lèvera la garde. Réglages › Garde.");
        Reglages_(FenetreReglages.Onglet.Garde);
    }

    private void Bulle(string titre, string texte) => icone.ShowBalloonTip(8000, titre, texte, Forms.ToolTipIcon.None);

    private async Task MiseAJourAutomatique()
    {
        minuterieMaj.Interval = TimeSpan.FromMinutes(30);
        if (!reglages.MisesAJourAuto || !misesAJour.Installee) return;
        if (DateTime.UtcNow - derniereRecherche > TimeSpan.FromHours(24))
        {
            derniereRecherche = DateTime.UtcNow;
            await misesAJour.Preparer();
        }
        if (misesAJour.VersionPrete is not null
            && ReglesMiseAJour.PeutInstaller(garde.Etat, garde.Filtre.InactivitePhysique, demandeeALaMain: false))
            misesAJour.InstallerEtRelancer();
    }

    private async void RechercherMiseAJour()
    {
        if (!misesAJour.Installee)
        {
            Bulle("Mises à jour", "Cette copie n'a pas été installée avec Hoverguard-Setup.exe : téléchargez-le pour recevoir les mises à jour.");
            Process.Start(new ProcessStartInfo(MisesAJour.PageDesVersions) { UseShellExecute = true });
            return;
        }
        Bulle("Mises à jour", "Recherche d'une nouvelle version…");
        derniereRecherche = DateTime.UtcNow;
        if (await misesAJour.Preparer() is not string version)
        {
            Bulle("Hoverguard est à jour", $"Version {FenetreReglages.VersionActuelle}");
            return;
        }
        if (ReglesMiseAJour.PeutInstaller(garde.Etat, garde.Filtre.InactivitePhysique, demandeeALaMain: true))
            misesAJour.InstallerEtRelancer();
        else
            Bulle("Mise à jour prête", $"La version {version} s'installera après la garde.");
    }

    /// <summary>Quitter est refusé pendant la garde : seul le mot de passe Hoverguard la lève.</summary>
    private void Quitter()
    {
        if (garde.Etat != EtatGarde.Libre) { Journal.Info("Quitter refusé pendant la garde"); return; }
        quitterDemande = true;
        misesAJour.InstallerALaSortie();
        app.Shutdown();
    }

    public void Arreter()
    {
        SystemEvents.UserPreferenceChanged -= ThemeChange;
        // « Quitter Hoverguard » arrête vraiment l'éveil ; une mise à jour ou l'extinction le laisse reprendre.
        if (quitterDemande) eveil.Desactiver(); else eveil.Suspendre();
        garde.Deverrouiller();
        garde.Filtre.Dispose();
        icone.Visible = false;
        icone.Dispose();
    }

    public void RecevoirUrl(string texte)
    {
        if (texte == ReglesLancement.Montrer) { Montrer(); return; }
        if (Ordre.Lire(texte) is Ordre o) garde.Recevoir(o);
        else Journal.Info("URL ignorée");
    }

    private void ThemeChange(object? _, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General) app.Dispatcher.BeginInvoke(RafraichirIcone);
    }

    /// <summary>Logo « Sentinelle », deux états comme dans la barre des menus du Mac : drone posé quand l'écran est
    /// libre, bouclier plein sous la garde.</summary>
    private void RafraichirIcone()
    {
        bool gardee = garde.Etat != EtatGarde.Libre;
        icone.Icon = Icones.Notification(gardee);
        icone.Text = gardee ? "Hoverguard : garde posée" : "Hoverguard : écran libre";
    }

    private static Forms.ToolStripMenuItem Item(string texte, Action action, bool coche = false)
    {
        var item = new Forms.ToolStripMenuItem(texte) { Checked = coche };
        item.Click += (_, _) => Dispatcher.CurrentDispatcher.BeginInvoke(action);
        return item;
    }

    private static Forms.ToolStripMenuItem Titre(string texte) => new(texte) { Enabled = false };

    private void Construire()
    {
        menu.Items.Clear();
        var g = garde;
        menu.Items.Add(Titre(g.Etat == EtatGarde.Libre ? "Écran libre" : "Garde posée"));
        // Plus de compteur d'agents affiché (peu fiable) : la détection sert seulement au drone et à la garde automatique.
        if (g.Agents.EnAttente().Count > 0) menu.Items.Add(Titre(Textes.StatutAgents([], g.Agents.EnAttente())));
        if (g.Etat == EtatGarde.Libre)
        {
            var poser = Item("Poser la garde", () => _ = g.Verrouiller());
            poser.ShortcutKeyDisplayString = Regles.Raccourci;
            menu.Items.Add(poser);
            if (reglages.GardeAutoMinutes > 0)
                menu.Items.Add(Item("Pas de garde automatique jusqu'à la prochaine garde",
                    () => g.GardeAutoSuspendue = !g.GardeAutoSuspendue, g.GardeAutoSuspendue));
        }
        else
        {
            // Sous la garde, le menu ne propose rien d'autre : ni lever la garde, ni quitter, ni réglages. La souris
            // physique y est bloquée, et un clic d'agent (qui, lui, passe) ne doit rien pouvoir y changer.
            menu.Items.Add(Titre("Mot de passe Hoverguard pour lever la garde"));
            return;
        }

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Titre("Garder le PC éveillé"));
        if (eveil.Session is SessionEveil s)
        {
            var reste = s.Restant(DateTime.UtcNow) is TimeSpan r ? $"encore {ReglesEveil.TexteRestant(r)}" : "jusqu'à l'arrêt";
            menu.Items.Add(Titre($"Éveillé, {reste}"));
            menu.Items.Add(Item("Arrêter l'éveil", eveil.Desactiver));
        }
        foreach (var duree in Durees.Toutes)
            menu.Items.Add(Item(duree.Libelle(), () => eveil.Activer(duree), eveil.Session?.Duree == duree));
        menu.Items.Add(Item("Garder aussi l'écran allumé", () => eveil.EcranAllume = !eveil.EcranAllume, eveil.EcranAllume));
        var batterie = new Forms.ToolStripMenuItem("Arrêter l'éveil sur batterie");
        foreach (var seuil in Reglages.ChoixSeuilBatterie)
            batterie.DropDownItems.Add(Item(seuil == 0 ? "Jamais" : $"À {seuil} %", () => eveil.SeuilBatterie = seuil, eveil.SeuilBatterie == seuil));
        menu.Items.Add(batterie);

        menu.Items.Add(new Forms.ToolStripSeparator());
        if (!g.Filtre.Actif) menu.Items.Add(Titre("⚠ Filtre clavier et souris indisponible"));
        // Seulement écran libre : sous la garde, le rapport (et ses photos) ne doit pas s'afficher devant un intrus.
        if (g.DernierRapport is not null && g.Etat == EtatGarde.Libre)
            menu.Items.Add(Item("Rapport de la dernière garde…", g.MontrerDernierRapport));
        menu.Items.Add(Item("Réglages…", () => Reglages_(FenetreReglages.Onglet.Garde)));
        menu.Items.Add(Item("Rechercher des mises à jour…", RechercherMiseAJour));
        menu.Items.Add(Item("À propos de Hoverguard", () => Reglages_(FenetreReglages.Onglet.APropos)));
        menu.Items.Add(Item("Un souci ou une idée ? Dites-le-nous…", Plantage.OuvrirRetour));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Item("Quitter Hoverguard", Quitter));
    }

    private void Reglages_(FenetreReglages.Onglet onglet)
    {
        fenetreReglages ??= new FenetreReglages(garde, eveil, reglages, Enregistrer) { RechercherMisesAJour = RechercherMiseAJour };
        fenetreReglages.Montrer(onglet);
    }
}
