using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Hoverguard.Coeur;

namespace Hoverguard;

/// <summary>Fenêtre de réglages : tout ce que le menu de l'icône propose, rangé par onglets, et « À propos ».</summary>
internal sealed class FenetreReglages : Window
{
    public enum Onglet { Garde, Agents, Alertes, Eveil, Pro, APropos }

    private readonly TabControl onglets = new() { Margin = new Thickness(12) };
    private readonly ControleurGarde garde;
    private readonly ControleurEveil eveil;
    private readonly Reglages reglages;
    private readonly Action enregistrer;

    /// <summary>Bouton « Rechercher des mises à jour maintenant » : branché par l'hôte (même action que le menu).</summary>
    public Action? RechercherMisesAJour { get; set; }

    public FenetreReglages(ControleurGarde garde, ControleurEveil eveil, Reglages reglages, Action enregistrer)
    {
        this.garde = garde;
        this.eveil = eveil;
        this.reglages = reglages;
        this.enregistrer = enregistrer;
        Title = "Réglages de Hoverguard";
        Width = 600; Height = 500;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = Icones.Logo();
        Content = onglets;
        Construire();
    }

    public void Montrer(Onglet onglet)
    {
        Construire();
        onglets.SelectedIndex = (int)onglet;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void Construire()
    {
        onglets.Items.Clear();
        onglets.Items.Add(new TabItem { Header = "Garde", Content = OngletGarde() });
        onglets.Items.Add(new TabItem { Header = "Agents", Content = OngletAgents() });
        onglets.Items.Add(new TabItem { Header = "Alertes", Content = OngletAlertes() });
        onglets.Items.Add(new TabItem { Header = "Éveil", Content = OngletEveil() });
        onglets.Items.Add(new TabItem { Header = "Pro", Content = new PagePro() });
        onglets.Items.Add(new TabItem { Header = "À propos", Content = APropos() });
    }

    private static StackPanel Page() => new() { Margin = new Thickness(18) };
    private static TextBlock Titre(string t) => new() { Text = t, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 12, 0, 6) };
    private static TextBlock Note(string t) => new() { Text = t, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, Margin = new Thickness(0, 4, 0, 4) };

    private static ComboBox Choix<T>(System.Collections.Generic.IEnumerable<T> valeurs, T actuelle, Func<T, string> libelle, Action<T> choisi)
    {
        var c = new ComboBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var v in valeurs) c.Items.Add(new ComboBoxItem { Content = libelle(v), Tag = v, IsSelected = Equals(v, actuelle) });
        c.SelectionChanged += (_, _) => { if (c.SelectedItem is ComboBoxItem { Tag: T v }) choisi(v); };
        return c;
    }

    private CheckBox Case(string texte, bool valeur, Action<bool> change)
    {
        var c = new CheckBox { Content = texte, IsChecked = valeur, Margin = new Thickness(0, 6, 0, 6) };
        c.Click += (_, _) => change(c.IsChecked == true);
        return c;
    }

    private UIElement OngletGarde()
    {
        var p = Page();
        p.Children.Add(Titre("Mot de passe Hoverguard"));
        var etat = new TextBlock
        {
            Text = garde.MotDePasseApp is null ? "Choisissez un mot de passe : sans lui, la garde ne peut pas être posée." : "Mot de passe choisi ✓",
            Foreground = garde.MotDePasseApp is null ? Brushes.IndianRed : Brushes.SeaGreen, Margin = new Thickness(0, 0, 0, 6),
        };
        p.Children.Add(etat);
        var nouveau = new PasswordBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 4) };
        var confirmation = new PasswordBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 4) };
        p.Children.Add(new TextBlock { Text = garde.MotDePasseApp is null ? "Mot de passe :" : "Nouveau mot de passe :" });
        p.Children.Add(nouveau);
        p.Children.Add(new TextBlock { Text = "Confirmation :" });
        p.Children.Add(confirmation);
        Button? enregistrerMdp = null;
        enregistrerMdp = Bouton("Enregistrer le mot de passe", async () =>
        {
            using var a = nouveau.SecurePassword;
            using var b = confirmation.SecurePassword;
            if (a.Length < MotDePasseHoverguard.LongueurMinimale)
            {
                etat.Text = $"{MotDePasseHoverguard.LongueurMinimale} caractères au moins."; etat.Foreground = Brushes.IndianRed; return;
            }
            enregistrerMdp!.IsEnabled = false;
            var m = await System.Threading.Tasks.Task.Run(() => MotDePasseHoverguard.Creer(a));
            bool identiques = await System.Threading.Tasks.Task.Run(() => m.Verifier(b));
            enregistrerMdp!.IsEnabled = true;
            if (!identiques) { etat.Text = "Les deux mots de passe sont différents."; etat.Foreground = Brushes.IndianRed; return; }
            try
            {
                m.Enregistrer(Dossiers.MotDePasse);
                garde.MotDePasseApp = m;
                nouveau.Clear(); confirmation.Clear();
                etat.Text = "Mot de passe choisi ✓"; etat.Foreground = Brushes.SeaGreen;
                Journal.Info("Mot de passe Hoverguard enregistré");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                etat.Text = $"Impossible d'enregistrer : {e.Message}"; etat.Foreground = Brushes.IndianRed;
            }
        });
        p.Children.Add(enregistrerMdp);
        p.Children.Add(Note("C'est ce mot de passe, et lui seul, qui lève la garde (4 caractères au moins ; il peut être le même que votre code Windows). Il n'est jamais enregistré en clair. Oublié ? Choisissez-en un autre ici quand l'écran est libre ; sous la garde, Ctrl + Alt + Suppr › Se déconnecter ferme tout."));
        p.Children.Add(Titre("Poser la garde"));
        p.Children.Add(new TextBlock { Text = $"Raccourci : {Regles.Raccourci}   (ou le menu de l'icône)" });
        p.Children.Add(new TextBlock { Text = "Toute seule après :", Margin = new Thickness(0, 10, 0, 4) });
        p.Children.Add(Choix(Reglages.ChoixGardeAuto, reglages.GardeAutoMinutes, m => m == 0 ? "Jamais" : $"{m} min sans activité",
            m => { reglages.GardeAutoMinutes = m; enregistrer(); }));
        p.Children.Add(Case("Même si aucun agent ne travaille", reglages.GardeAutoSansAgent, v => { reglages.GardeAutoSansAgent = v; enregistrer(); }));
        p.Children.Add(Note("Activée, la garde se pose comme un économiseur d'écran dès que vous laissez le PC. Seuls le vrai clavier et la vraie souris comptent : l'activité des agents ne retarde pas la garde."));
        p.Children.Add(Titre("Voile"));
        var phrase = new TextBox { Text = reglages.Phrase, MaxLength = 200 };
        phrase.LostFocus += (_, _) => { reglages.Phrase = phrase.Text.Trim(); enregistrer(); garde.PhraseChangee(); };
        p.Children.Add(phrase);
        p.Children.Add(Note("Vide = une phrase drôle au hasard à chaque garde."));
        p.Children.Add(Titre("Lever la garde"));
        p.Children.Add(Note("Appuyez sur une touche : le drone s'approche et demande votre mot de passe Hoverguard. Échap renvoie le drone surveiller."));
        p.Children.Add(Case("Ouvrir le rapport de la garde au déverrouillage", reglages.RapportAuRetour, v => { reglages.RapportAuRetour = v; enregistrer(); }));
        p.Children.Add(Note("Sinon, le rapport attend dans le menu de l'icône : « Rapport de la dernière garde… »."));
        p.Children.Add(Case("Ouvrir Hoverguard à l'ouverture de session", Inscriptions.OuvertureAuDemarrage, v => Inscriptions.OuvertureAuDemarrage = v));
        p.Children.Add(Titre("Mises à jour"));
        p.Children.Add(Case("Installer les mises à jour automatiquement", reglages.MisesAJourAuto, v => { reglages.MisesAJourAuto = v; enregistrer(); }));
        p.Children.Add(Bouton("Rechercher des mises à jour maintenant", () => RechercherMisesAJour?.Invoke()));
        p.Children.Add(Note("Une fois par jour, Hoverguard regarde la dernière version publiée sur GitHub (seule connexion réseau de l'app). Elle s'installe quand le PC est inutilisé depuis 10 minutes ou quand vous quittez Hoverguard, jamais pendant une garde."));
        return new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private UIElement OngletAgents()
    {
        var p = Page();
        p.Children.Add(Titre("En ce moment"));
        p.Children.Add(new TextBlock { Text = Textes.Compteur(garde.PrincipauxActifs, garde.SousAgentsActifs) });
        foreach (var (nom, auTravail, ouverts) in ReglesDetection.ParOutil(garde.Detectes, garde.Journaux.Principaux))
        {
            var ligneOutil = new DockPanel { Margin = new Thickness(0, 2, 0, 2), LastChildFill = false };
            ligneOutil.Children.Add(new TextBlock { Text = nom, Width = 160 });
            ligneOutil.Children.Add(new TextBlock { Text = ReglesDetection.Detail(auTravail, ouverts) });
            p.Children.Add(ligneOutil);
        }
        p.Children.Add(Note("Détection automatique de tous les agents qui tournent sur ce PC (Claude Code et les conversations de l'app Claude, Codex, Gemini, Cursor, Aider, OpenCode, Goose…). Les hooks ajoutent le compte des sous-agents. Les sessions dans le cloud ne sont pas visibles d'ici."));
        p.Children.Add(Titre("Outils suivis"));
        var home = Dossiers.Utilisateur;
        foreach (var outil in BranchementAgents.Tous)
        {
            var ligne = new DockPanel { Margin = new Thickness(0, 4, 0, 4), LastChildFill = false };
            ligne.Children.Add(new TextBlock { Text = outil.Nom, Width = 160, VerticalAlignment = VerticalAlignment.Center });
            System.Text.Json.Nodes.JsonObject? reglagesOutil = null;
            string? erreur = null;
            try { reglagesOutil = outil.Lire(home); } catch (Exception e) { erreur = e.Message; }
            if (!outil.Present(home))
                ligne.Children.Add(Etat("non installé", Brushes.Gray));
            else if (reglagesOutil is null)
            {
                // Fichier illisible (commentaires, racine qui n'est pas un objet…) : on n'y touche pas, on explique.
                ligne.Children.Add(Etat("⚠ fichier illisible", Brushes.DarkOrange));
                ligne.Children.Add(Bouton("Pourquoi ?", () =>
                    MessageBox.Show(this, erreur, $"{outil.Nom} : fichier non modifié", MessageBoxButton.OK, MessageBoxImage.Warning)));
            }
            else
            {
                if (outil.EstAJour(reglagesOutil))
                    ligne.Children.Add(Etat("✔ branché", Brushes.SeaGreen));
                else
                    ligne.Children.Add(Bouton(outil.EstBranche(reglagesOutil) ? "Mettre à jour…" : "Brancher…",
                        () => { if (Brancher(outil)) Montrer(Onglet.Agents); }));
                if (outil.ContientHoverguard(reglagesOutil))
                    ligne.Children.Add(Bouton("Débrancher", () => { if (Debrancher(outil)) Montrer(Onglet.Agents); }));
            }
            p.Children.Add(ligne);
        }
        p.Children.Add(Note("Chaque outil prévient Hoverguard par une URL locale hoverguard:// lancée par PowerShell ; rien ne sort du PC. " +
                            "« Débrancher » retire seulement ce que Hoverguard a ajouté, vos propres hooks restent."));
        return p;
    }

    private static TextBlock Etat(string texte, Brush couleur)
        => new() { Text = texte, Foreground = couleur, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };

    private static Button Bouton(string texte, Action action)
    {
        var b = new Button { Content = texte, Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 8, 0), HorizontalAlignment = HorizontalAlignment.Left };
        b.Click += (_, _) => action();
        return b;
    }

    private bool Brancher(BranchementAgents outil)
    {
        var fichier = outil.FichierAffiche(outil.Fichier(Dossiers.Utilisateur));
        var etats = outil.Evenements.Select(e => e.Etat).ToHashSet();
        var texte = $"Hoverguard ajoute ses hooks dans {fichier} (début de travail, fin" +
                    (etats.Contains(EtatAgent.Attente) ? ", attente" : "") + (etats.Contains(EtatAgent.SousAgentDebut) ? ", sous-agents" : "") +
                    ") pour compter les agents et faire voler le drone. Une copie du fichier est gardée à côté (….avant-hoverguard). " +
                    "Seuls l'état et 4 caractères de l'identifiant de session sont transmis, rien ne sort du PC.";
        if (outil == BranchementAgents.Codex) texte += "\n\nDans Codex, tapez une fois /hooks pour approuver les nouveaux hooks.";
        if (outil == BranchementAgents.Cursor) texte += "\n\nCursor ne signale pas quand il attend votre réponse : seulement travail et fin.";
        if (MessageBox.Show(this, texte, $"Brancher {outil.Nom} à Hoverguard ?", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return false;
        return Modifier(outil, fichier, r => outil.Brancher(r), "Branchement");
    }

    private bool Debrancher(BranchementAgents outil)
    {
        var fichier = outil.FichierAffiche(outil.Fichier(Dossiers.Utilisateur));
        var texte = $"Hoverguard retire ses hooks de {fichier}. Vos propres hooks et le reste du fichier ne changent pas ; " +
                    "une copie est gardée à côté (….avant-hoverguard).";
        if (MessageBox.Show(this, texte, $"Débrancher {outil.Nom} ?", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return false;
        return Modifier(outil, fichier, r => outil.Debrancher(r), "Débranchement");
    }

    private bool Modifier(BranchementAgents outil, string fichier, Func<System.Text.Json.Nodes.JsonObject, System.Text.Json.Nodes.JsonObject> changement, string action)
    {
        try
        {
            outil.Ecrire(Dossiers.Utilisateur, changement(outil.Lire(Dossiers.Utilisateur)));
            return true;
        }
        catch (FichierIllisible e)
        {
            MessageBox.Show(this, e.Message, $"{outil.Nom} : fichier non modifié", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        catch (Exception e)
        {
            Journal.Erreur($"{action} {outil.Nom} : {e.Message}");
            MessageBox.Show(this, $"Impossible de modifier {fichier} : {e.Message}", "Hoverguard", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private UIElement OngletAlertes()
    {
        var p = Page();
        p.Children.Add(Titre("Intrus"));
        p.Children.Add(Case("Photo par la caméra intégrée après 2 mots de passe faux", reglages.PhotoIntrus, v => { reglages.PhotoIntrus = v; enregistrer(); }));
        p.Children.Add(Note($"Désactivée par défaut. Photographier une personne peut être encadré par la loi (droit à l'image, surveillance au travail) : vérifiez avant d'activer. Caméra intégrée uniquement (jamais une webcam USB ni un téléphone). Photos gardées {PhotosIntrus.JoursDeConservation} jours sur ce PC, puis effacées."));
        var dossier = new Button { Content = "Ouvrir le dossier des photos", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 2, 10, 2) };
        dossier.Click += (_, _) => OuvrirDossier(Dossiers.Intrus);
        p.Children.Add(dossier);
        p.Children.Add(Note("Windows ne demande pas d'autorisation par application : si « Autoriser les applications de bureau à accéder à votre caméra » est coupé, aucune photo n'est prise."));
        p.Children.Add(Titre("Notifications sur le téléphone"));
        p.Children.Add(new CheckBox { Content = "Activer (bientôt)", IsEnabled = false, IsChecked = false });
        p.Children.Add(Note("Sous macOS, Hoverguard prévient l'iPhone par iMessage. Windows n'a pas d'équivalent local sans service tiers : cet emplacement est réservé, en attendant un canal qui respecte « aucune connexion réseau »."));
        return p;
    }

    private UIElement OngletEveil()
    {
        var p = Page();
        p.Children.Add(Titre("Garder le PC éveillé (sans poser la garde)"));
        p.Children.Add(Note("Choisissez la durée dans le menu de l'icône."));
        p.Children.Add(Case("Garder aussi l'écran allumé", eveil.EcranAllume, v => eveil.EcranAllume = v));
        p.Children.Add(new TextBlock { Text = "Arrêter sur batterie :", Margin = new Thickness(0, 10, 0, 4) });
        p.Children.Add(Choix(Reglages.ChoixSeuilBatterie, eveil.SeuilBatterie, s => s == 0 ? "Jamais" : $"À {s} %", s => eveil.SeuilBatterie = s));
        p.Children.Add(Titre("Pendant la garde"));
        p.Children.Add(Note("Le PC et l'écran restent toujours éveillés, et le voile demande d'être branché quand le PC passe sur batterie. Un économiseur d'écran avec mot de passe verrouille quand même la session : désactivez-le si les agents doivent travailler longtemps."));
        return p;
    }

    public static string VersionActuelle =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "?";

    private static UIElement APropos()
    {
        var version = VersionActuelle;
        var p = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24) };
        p.Children.Add(new Image { Source = Icones.Logo(128), Width = 96, Height = 96 });
        p.Children.Add(new TextBlock { Text = "Hoverguard", FontSize = 30, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center });
        p.Children.Add(new TextBlock { Text = $"Version {version}", Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center });
        p.Children.Add(new TextBlock { Text = "Un drone garde votre écran pendant que vos agents IA travaillent.", Margin = new Thickness(0, 12, 0, 12), HorizontalAlignment = HorizontalAlignment.Center });
        p.Children.Add(new Separator { Width = 220 });
        p.Children.Add(new TextBlock { Text = "Développé par Utahpik", FontWeight = FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, 12, 0, 6), HorizontalAlignment = HorizontalAlignment.Center });
        p.Children.Add(new TextBlock
        {
            Text = "© 2026 Utahpik",
            Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 10),
        });
        p.Children.Add(new TextBlock
        {
            Text = "Hoverguard cache l'écran et bloque le clavier et la souris, mais ce n'est pas un verrou de sécurité : pour protéger vos données, verrouillez la session avec ⊞ + L.",
            Foreground = Brushes.Gray, FontSize = 11, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxWidth = 420,
        });
        return p;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;  // on la cache, on la garde pour la prochaine fois
        Hide();
    }

    public static void OuvrirDossier(string dossier)
    {
        Directory.CreateDirectory(dossier);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dossier}\"") { UseShellExecute = true });
    }
}

/// <summary>Au retour : ce qui s'est passé pendant l'absence, avec les photos éventuelles.</summary>
internal sealed class FenetreRapport : Window
{
    public FenetreRapport(JournalAbsence j, DateTime fin)
    {
        Title = "Hoverguard";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        Icon = Icones.Logo();
        var p = new StackPanel { Margin = new Thickness(24), MaxWidth = 560 };
        p.Children.Add(new TextBlock { Text = "Pendant la dernière garde…", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        p.Children.Add(new TextBlock { Text = j.Rapport(fin), TextWrapping = TextWrapping.Wrap, LineHeight = 22 });
        var photos = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        foreach (var chemin in j.Photos.Take(3).Where(File.Exists))
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(chemin);
                image.DecodePixelWidth = 320;
                image.EndInit();
                photos.Children.Add(new Image { Source = image, Width = 160, Height = 120, Margin = new Thickness(0, 0, 8, 0) });
            }
            catch (Exception) { }
        }
        if (photos.Children.Count > 0) p.Children.Add(photos);
        var boutons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        if (j.Photos.Count > 0)
        {
            var voir = new Button { Content = "Voir les photos", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
            voir.Click += (_, _) => FenetreReglages.OuvrirDossier(Dossiers.Intrus);
            boutons.Children.Add(voir);
        }
        var ok = new Button { Content = "OK", IsDefault = true, Padding = new Thickness(18, 4, 18, 4) };
        ok.Click += (_, _) => Close();
        boutons.Children.Add(ok);
        p.Children.Add(boutons);
        Content = p;
        Loaded += (_, _) => Activate();
    }
}
