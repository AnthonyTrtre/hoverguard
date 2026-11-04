using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Hoverguard.Coeur;
using Forms = System.Windows.Forms;

namespace Hoverguard;

/// <summary>Côté Windows du rapport de plantage : écrit par les gestionnaires d'erreur, proposé au lancement suivant.
/// Rien n'est envoyé : « Signaler sur GitHub » ouvre seulement une issue pré-remplie dans le navigateur.</summary>
internal static class Plantage
{
    public static string Fichier => Path.Combine(Dossiers.Local, "plantage.txt");
    /// <summary>Le rapport déjà proposé, gardé pour « Afficher le rapport ».</summary>
    public static string Precedent => Path.Combine(Dossiers.Local, "plantage-precedent.txt");

    public static void Noter(Exception e, bool fatal)
    {
        try
        {
            Directory.CreateDirectory(Dossiers.Local);
            File.WriteAllText(Fichier, RapportPlantage.Formater(e, fatal, FenetreReglages.VersionActuelle, DateTime.Now), Encoding.UTF8);
        }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException)
        {
            // Un rapport impossible à écrire ne doit jamais empêcher la garde.
        }
    }

    /// <summary>Rapport laissé par le lancement précédent : mis de côté pour n'être proposé qu'une fois.</summary>
    public static string? Chercher()
    {
        try
        {
            if (!File.Exists(Fichier)) return null;
            File.Move(Fichier, Precedent, overwrite: true);
            return Precedent;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Rien n'est envoyé : le navigateur ouvre l'issue, l'utilisateur l'écrit et l'envoie lui-même.</summary>
    public static void OuvrirRetour()
        => Process.Start(new ProcessStartInfo(RapportPlantage.UrlRetour(FenetreReglages.VersionActuelle, Environment.OSVersion.VersionString))
            { UseShellExecute = true });

    public static void Proposer(string fichier)
    {
        string contenu;
        try { contenu = File.ReadAllText(fichier); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        var signaler = new Forms.TaskDialogButton("Signaler sur GitHub");
        var afficher = new Forms.TaskDialogButton("Afficher le rapport");
        var page = new Forms.TaskDialogPage
        {
            Caption = "Hoverguard",
            Heading = RapportPlantage.EstFatal(contenu)
                ? "Hoverguard s'est fermé de façon inattendue. Envoyer le rapport ?"
                : "Hoverguard a rencontré une erreur inattendue. Envoyer le rapport ?",
            Text = "« Signaler sur GitHub » ouvre une issue pré-remplie dans votre navigateur, sans votre nom ni celui du PC. Vous la relisez avant de l'envoyer : rien ne part sans votre clic.",
            Buttons = { signaler, afficher, new Forms.TaskDialogButton("Ignorer") },
        };
        var choix = Forms.TaskDialog.ShowDialog(page);
        if (choix == signaler)
        {
            var extrait = RapportPlantage.Anonymiser(RapportPlantage.Extrait(contenu), Dossiers.Utilisateur,
                [Environment.UserName], [Environment.MachineName]);
            var url = RapportPlantage.Url(FenetreReglages.VersionActuelle, Environment.OSVersion.VersionString,
                RuntimeInformation.ProcessArchitecture.ToString(), extrait, RapportPlantage.TailleMaxUrlWindows);
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        else if (choix == afficher)
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{fichier}\"") { UseShellExecute = true });
    }
}
