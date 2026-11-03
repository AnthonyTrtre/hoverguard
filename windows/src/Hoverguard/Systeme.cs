using System;
using System.IO;
using System.Text;
using Hoverguard.Coeur;
using Microsoft.Win32;

namespace Hoverguard;

/// <summary>Où Hoverguard range ses fichiers. Tout reste sur le disque de l'utilisateur.</summary>
internal static class Dossiers
{
    /// <summary><c>%APPDATA%\Utahpik\Hoverguard</c> : réglages.</summary>
    public static string Itinerant => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Utahpik", "Hoverguard");
    /// <summary><c>%LOCALAPPDATA%\Utahpik\Hoverguard</c> : journal, photos d'intrus, cache du moteur WebView2.</summary>
    public static string Local => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Utahpik", "Hoverguard");
    public static string Reglages => Path.Combine(Itinerant, "reglages.json");
    public static string Intrus => Path.Combine(Local, "intrus");
    public static string WebView2 => Path.Combine(Local, "WebView2");
    public static string JournalFichier => Path.Combine(Local, "journal.txt");
    public static string Utilisateur => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}

/// <summary>Petit journal local (1 Mo au plus, une génération gardée). Jamais de mot de passe, jamais de contenu
/// d'écran ; rien n'est envoyé nulle part.</summary>
internal static class Journal
{
    private static readonly object Verrou = new();

    public static void Info(string message) => Ecrire("info", message);
    public static void Erreur(string message) => Ecrire("ERREUR", message);

    private static void Ecrire(string niveau, string message)
    {
        try
        {
            lock (Verrou)
            {
                Directory.CreateDirectory(Dossiers.Local);
                var f = new FileInfo(Dossiers.JournalFichier);
                if (f.Exists && f.Length > 1_000_000) File.Move(f.FullName, f.FullName + ".1", overwrite: true);
                File.AppendAllText(f.FullName, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{niveau}] {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Un journal impossible à écrire ne doit jamais empêcher la garde.
        }
    }
}

internal static class Batterie
{
    public static EtatBatterie Lire()
    {
        if (!Natif.GetSystemPowerStatus(out var s)) return new EtatBatterie(null, true);
        bool sansBatterie = (s.BatteryFlag & 128) != 0 || s.BatteryFlag == 255;
        int? niveau = sansBatterie || s.BatteryLifePercent > 100 ? null : s.BatteryLifePercent;
        return new EtatBatterie(niveau, s.ACLineStatus != 0);  // 255 = inconnu : on suppose le secteur
    }
}

/// <summary>Empêche la veille d'inactivité (<c>SetThreadExecutionState</c>). La garde et le menu « Garder le PC
/// éveillé » s'additionnent : on applique l'union des deux. À appeler depuis le fil de l'interface, qui vit aussi
/// longtemps que l'app ; Windows libère tout si l'app quitte ou plante.</summary>
internal static class Alimentation
{
    private static bool gardeSysteme, gardeEcran, eveilSysteme, eveilEcran;

    public static void Garde(bool actif) { gardeSysteme = actif; gardeEcran = actif; Appliquer(); }
    public static void Eveil(bool actif, bool ecran) { eveilSysteme = actif; eveilEcran = actif && ecran; Appliquer(); }

    public static bool Actif => gardeSysteme || eveilSysteme;

    private static void Appliquer()
    {
        uint drapeaux = Natif.ES_CONTINUOUS;
        if (gardeSysteme || eveilSysteme) drapeaux |= Natif.ES_SYSTEM_REQUIRED;
        if (gardeEcran || eveilEcran) drapeaux |= Natif.ES_DISPLAY_REQUIRED;
        if (Natif.SetThreadExecutionState(drapeaux) == 0) Journal.Erreur("SetThreadExecutionState refusé");
    }
}

/// <summary>Inscriptions dans le registre de l'utilisateur (HKCU), sans droits d'administrateur.</summary>
internal static class Inscriptions
{
    private const string CleRun = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string CleProtocole = @"Software\Classes\hoverguard";
    private static string Exe => Environment.ProcessPath ?? "Hoverguard.exe";

    /// <summary>Le schéma <c>hoverguard://</c> ouvre cet exécutable (réinscrit à chaque lancement : l'exe a pu bouger).
    /// L'URL n'accepte que « poser la garde » et les signaux des agents : voir <see cref="Ordre"/>.</summary>
    public static void InscrireProtocole()
    {
        try
        {
            using var cle = Registry.CurrentUser.CreateSubKey(CleProtocole);
            cle.SetValue("", "URL:Hoverguard");
            cle.SetValue("URL Protocol", "");
            using (var icone = cle.CreateSubKey("DefaultIcon")) icone.SetValue("", $"\"{Exe}\",0");
            using var commande = cle.CreateSubKey(@"shell\open\command");
            commande.SetValue("", $"\"{Exe}\" \"%1\"");
        }
        catch (Exception e) { Journal.Erreur($"Inscription du protocole : {e.Message}"); }
    }

    public static bool OuvertureAuDemarrage
    {
        get
        {
            using var cle = Registry.CurrentUser.OpenSubKey(CleRun);
            return cle?.GetValue("Hoverguard") is string;
        }
        set
        {
            try
            {
                using var cle = Registry.CurrentUser.CreateSubKey(CleRun);
                if (value) cle.SetValue("Hoverguard", $"\"{Exe}\" --demarrage");
                else cle.DeleteValue("Hoverguard", throwOnMissingValue: false);
            }
            catch (Exception e) { Journal.Erreur($"Ouverture au démarrage : {e.Message}"); }
        }
    }
}
