using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Hoverguard.Coeur;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace Hoverguard;

internal static class Programme
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "--autotest")
            return AutoTest.Lancer(args.Length >= 2 ? args[1] : "autotest");

        var url = args.FirstOrDefault(a => a.StartsWith("hoverguard:", StringComparison.OrdinalIgnoreCase));
        using var canal = new Canal();
        if (!canal.EstPremier)
        {
            // Hoverguard tourne déjà : on lui passe l'URL (hook d'un agent) et on s'arrête.
            if (url is not null) Canal.Transmettre(url);
            return 0;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var hote = new Hote(app);
        canal.Ecouter(texte => app.Dispatcher.BeginInvoke(() => hote.RecevoirUrl(texte)));
        app.Startup += (_, _) =>
        {
            hote.Demarrer();
            if (url is not null) hote.RecevoirUrl(url);
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
        garde.SurChangement = RafraichirIcone;
        eveil.SurChangement = RafraichirIcone;
        garde.Demarrer();
        // Mode Windows clair ou sombre changé : l'icône de notification suit la couleur de la barre des tâches.
        SystemEvents.UserPreferenceChanged += ThemeChange;
        RafraichirIcone();
        icone.Visible = true;
        Journal.Info($"Hoverguard démarré ({Environment.OSVersion.Version})");
    }

    public void FinDeSession() => garde.FinDeSession();

    /// <summary>Quitter est refusé pendant la garde : seuls Windows Hello et le mot de passe la lèvent.</summary>
    private void Quitter()
    {
        if (garde.Etat != EtatGarde.Libre) { Journal.Info("Quitter refusé pendant la garde"); return; }
        app.Shutdown();
    }

    public void Arreter()
    {
        SystemEvents.UserPreferenceChanged -= ThemeChange;
        eveil.Desactiver();
        garde.Deverrouiller();
        garde.Filtre.Dispose();
        icone.Visible = false;
        icone.Dispose();
    }

    public void RecevoirUrl(string texte)
    {
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
        icone.Text = IconesNotification.InfoBulle(gardee, garde.PrincipauxActifs, garde.Agents.SousAgents());
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
        menu.Items.Add(Titre(Textes.Compteur(g.PrincipauxActifs, g.Agents.SousAgents())));
        if (ReglesDetection.LigneMenu(g.Detectes) is string ouverts) menu.Items.Add(Titre(ouverts));
        if (g.Agents.EnAttente().Count > 0) menu.Items.Add(Titre(Textes.StatutAgents([], g.Agents.EnAttente())));
        if (g.Etat == EtatGarde.Libre)
        {
            var poser = Item("Poser la garde", () => _ = g.Verrouiller());
            poser.ShortcutKeyDisplayString = Regles.Raccourci;
            menu.Items.Add(poser);
        }
        else
        {
            // Sous la garde, le menu ne propose rien d'autre : ni lever la garde, ni quitter, ni réglages. La souris
            // physique y est bloquée, et un clic d'agent (qui, lui, passe) ne doit rien pouvoir y changer.
            menu.Items.Add(Titre("Windows Hello ou mot de passe pour lever la garde"));
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
        menu.Items.Add(Item("À propos de Hoverguard", () => Reglages_(FenetreReglages.Onglet.APropos)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Item("Quitter Hoverguard", Quitter));
    }

    private void Reglages_(FenetreReglages.Onglet onglet)
    {
        fenetreReglages ??= new FenetreReglages(garde, eveil, reglages, Enregistrer);
        fenetreReglages.Montrer(onglet);
    }
}
