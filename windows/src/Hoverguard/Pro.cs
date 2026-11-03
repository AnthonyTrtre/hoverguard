using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Hoverguard.Coeur;

namespace Hoverguard;

/// <summary>Licence, essai et cadeau de lancement (équivalent de <c>enum Pro</c> dans <c>macos/Sources/Licence.swift</c>).
/// Tout est vérifié sur ce PC : aucune connexion, aucune télémétrie.</summary>
internal static class Pro
{
    /// <summary>Pages de vente et de soutien : vides tant que la boutique n'est pas ouverte (les liens restent cachés).</summary>
    public static readonly Uri? UrlAchat = null;
    public static readonly Uri? UrlSoutien = null;

    /// <summary>Édition de lancement : la première version publiée (compilée avec <c>-p:EditionLancement=true</c>, qui
    /// définit <c>EDITION_LANCEMENT</c>) offre le Pro à vie, tout seul, au premier lancement. Quand les 1 000 premiers
    /// téléchargements sont atteints, la version suivante est publiée sans ce cadeau ; ceux qui l'ont reçu le gardent.
    /// Aucune connexion, rien à valider à la main.</summary>
#if EDITION_LANCEMENT
    public const bool EditionLancement = true;
#else
    public const bool EditionLancement = false;
#endif

    public static string Chemin => Path.Combine(Dossiers.Itinerant, "pro.json");

    private static MemoirePro? memoire;
    private static MemoirePro Memoire => memoire ??= MemoirePro.Charger(Chemin);

    /// <summary>Date de compilation, inscrite dans l'exe par le .csproj (<c>DateDeVersion</c>). La date du fichier ne
    /// convient pas : un navigateur la remplace par celle du téléchargement. Elle ne sert qu'en secours.</summary>
    public static DateTime DateDeVersion
    {
        get
        {
            var texte = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "DateDeVersion")?.Value;
            if (texte is not null && Licence.Jour(texte) is DateTime d) return d;
            try { return Environment.ProcessPath is string exe ? File.GetLastWriteTimeUtc(exe) : DateTime.UtcNow; }
            catch (Exception) { return DateTime.UtcNow; }
        }
    }

    public static EtatPro Etat => Memoire.Etat(DateDeVersion);
    public static Licence? LicenceActive => Memoire.LicenceValide;

    public static void AccorderProLancementSiEdition()
    {
        if (Memoire.AccorderProLancementSiEdition(EditionLancement)) Enregistrer();
    }

    /// <summary>Appelé quand une vraie garde est posée (pas l'aperçu).</summary>
    public static void DemarrerEssaiSiPremiereGarde()
    {
        if (Memoire.DemarrerEssaiSiPremiereGarde()) Enregistrer();
    }

    /// <summary>Vrai si la licence collée est valide (elle est alors enregistrée).</summary>
    public static bool Activer(string? texte)
    {
        if (!Memoire.Activer(texte)) return false;
        Enregistrer();
        return true;
    }

    /// <summary><c>hoverguard://licence?cle=HG1-…</c>. Pendant la garde, aucune fenêtre : elle s'ouvrirait sous le voile
    /// et seul un agent pourrait la fermer ; la licence est quand même activée si elle est valide.</summary>
    public static void ActiverDepuisLien(string cle, bool afficher)
    {
        bool ok = Activer(cle);
        Journal.Info(ok ? "Licence Pro activée par lien" : "Lien de licence refusé (signature invalide)");
        if (!afficher) return;
        if (ok && LicenceActive is Licence l)
            MessageBox.Show($"Licence de {l.Email}. Merci !", "Hoverguard Pro est activé 🎉", MessageBoxButton.OK, MessageBoxImage.Information);
        else
            MessageBox.Show("Le lien semble incomplet. Vous pouvez aussi coller la licence dans Réglages › Pro.",
                "Licence non reconnue", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static void Enregistrer()
    {
        try { Memoire.Enregistrer(Chemin); }
        catch (Exception e) { Journal.Erreur($"Pro : {e.Message}"); }
    }
}

/// <summary>Onglet « Pro » des réglages : statut, fonctions à venir, champ pour coller une licence.</summary>
internal sealed class PagePro : ScrollViewer
{
    private static readonly (string Titre, string Detail)[] Fonctions =
    [
        ("Tableau de bord des agents", "temps de travail, sessions, sous-agents, chronologie"),
        ("Coût des agents", "jetons et euros par agent et par jour, alerte de budget"),
        ("Journal de l'absence", "ce que les agents ont fait pendant que vous n'étiez pas là"),
        ("Notifications partout", "ntfy, Pushover, Slack, Discord, Telegram, webhook"),
        ("Vidéo de l'intrus", "quelques secondes au lieu d'une photo"),
        ("Drones, thèmes et sons", "autres modèles, couleurs des lasers, votre logo sur le voile"),
    ];

    private static readonly Brush Vert = new SolidColorBrush(Color.FromRgb(0x1E, 0x8E, 0x3E));
    private static readonly Brush Cyan = new SolidColorBrush(Color.FromRgb(0x00, 0x83, 0x9E));

    private bool erreur;

    public PagePro()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Construire("");
    }

    private void Construire(string saisieEnCours)
    {
        var p = new StackPanel { Margin = new Thickness(18) };
        var etat = Pro.Etat;
        p.Children.Add(new TextBlock
        {
            Text = etat.Libelle, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4),
            FontWeight = etat.Actif ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = etat switch { EtatPro.Essai => Cyan, EtatPro.Pro or EtatPro.Lancement => Vert, _ => SystemColors.ControlTextBrush },
        });
        if (etat is EtatPro.Pro { Licence: var l } && l.Offre != Offre.Fondateur)
            p.Children.Add(Note($"Mises à jour comprises jusqu'au {l.FinMisesAJour.ToString("d MMMM yyyy", CultureInfo.GetCultureInfo("fr-FR"))}."));

        p.Children.Add(Titre("Ce que le Pro ajoutera"));
        foreach (var (titre, detail) in Fonctions)
        {
            var ligne = new StackPanel { Margin = new Thickness(0, 3, 0, 3) };
            ligne.Children.Add(new TextBlock { Text = "•  " + titre });
            ligne.Children.Add(new TextBlock { Text = detail, Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(14, 0, 0, 0) });
            p.Children.Add(ligne);
        }
        p.Children.Add(Note("En préparation : les fonctions arrivent avec les prochaines versions."));

        p.Children.Add(Titre("Licence"));
        var champ = new TextBox { Text = saisieEnCours, MinWidth = 380, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Collez votre licence (HG1-…)" };
        var activer = new Button { Content = "Activer", Padding = new Thickness(14, 2, 14, 2), Margin = new Thickness(8, 0, 0, 0), IsEnabled = saisieEnCours.Length > 0 };
        champ.TextChanged += (_, _) => activer.IsEnabled = champ.Text.Trim().Length > 0;
        activer.Click += (_, _) =>
        {
            erreur = !Pro.Activer(champ.Text);
            Construire(erreur ? champ.Text : "");
        };
        var ligneLicence = new DockPanel();
        DockPanel.SetDock(activer, Dock.Right);
        ligneLicence.Children.Add(activer);
        ligneLicence.Children.Add(champ);
        p.Children.Add(new TextBlock { Text = "Collez votre licence (HG1-…) :", Margin = new Thickness(0, 0, 0, 4) });
        p.Children.Add(ligneLicence);
        if (erreur) p.Children.Add(new TextBlock { Text = "Licence non reconnue. Vérifiez qu'elle est complète.", Foreground = Brushes.Firebrick, Margin = new Thickness(0, 6, 0, 0) });
        if (Pro.UrlAchat is Uri achat) p.Children.Add(Lien("Acheter Hoverguard Pro", achat));
        if (Pro.UrlSoutien is Uri soutien) p.Children.Add(Lien("Soutenir Utahpik", soutien));
        p.Children.Add(Note("La licence est vérifiée sur ce PC, sans connexion internet."));
        Content = p;
    }

    private static TextBlock Titre(string t) => new() { Text = t, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 14, 0, 6) };
    private static TextBlock Note(string t) => new() { Text = t, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 11, Margin = new Thickness(0, 6, 0, 4) };

    private static TextBlock Lien(string texte, Uri adresse)
    {
        var lien = new Hyperlink(new Run(texte)) { NavigateUri = adresse };
        // Seules des adresses https écrites dans le code peuvent être ouvertes, par le navigateur de l'utilisateur.
        lien.RequestNavigate += (_, e) =>
        {
            if (e.Uri.Scheme == Uri.UriSchemeHttps) Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        };
        return new TextBlock(lien) { Margin = new Thickness(0, 6, 0, 0) };
    }
}
