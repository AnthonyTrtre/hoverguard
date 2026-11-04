using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Hoverguard;

/// <summary>Appels Win32 propres à la sécurité de la garde (verrouillage de la session, lecteur d'écran, traduction
/// des touches, luminosité). Séparés de <see cref="Natif"/> pour garder ce module autonome.</summary>
internal static class NatifGarde
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool LockWorkStation();

    [DllImport("user32.dll")]
    public static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll")]
    public static extern short GetKeyState(int nVirtKey);

    // CharSet.Unicode indispensable : sans lui, le tampon char[] est marshalé en ANSI (1 octet par caractère) alors que
    // ToUnicodeEx y écrit cchBuff caractères de 2 octets, d'où une corruption du tas (0xC0000374) à chaque frappe
    // pendant la garde (signalé par Mathéo, 1.1.1).
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte[] lpKeyState,
        [Out] char[] pwszBuff, int cchBuff, uint wFlags, IntPtr dwhkl);

    public const uint SPI_GETSCREENREADER = 0x0046;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, out bool pvParam, uint fWinIni);

    private const int WM_SYSCOMMAND = 0x0112, SC_MONITORPOWER = 0xF170;
    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam, uint flags,
        uint timeout, out IntPtr result);

    /// <summary>Verrouille la vraie session Windows (écran de connexion). Si Windows refuse, l'écran est au moins
    /// éteint.</summary>
    public static void VerrouillerSession()
    {
        if (LockWorkStation()) return;
        Journal.Erreur($"LockWorkStation refusé (erreur {Marshal.GetLastWin32Error()}) : écran éteint");
        SendMessageTimeout(HWND_BROADCAST, WM_SYSCOMMAND, new IntPtr(SC_MONITORPOWER), new IntPtr(2), 0x2 /* SMTO_ABORTIFHUNG */, 1000, out _);
    }

    /// <summary>Le Narrateur (ou un autre lecteur d'écran) est actif : il lirait à voix haute ce qui est sous le voile.</summary>
    public static bool LecteurEcranActif()
    {
        if (SystemParametersInfo(SPI_GETSCREENREADER, 0, out bool actif, 0) && actif) return true;
        var narrateurs = Process.GetProcessesByName("Narrator");
        foreach (var p in narrateurs) p.Dispose();
        return narrateurs.Length > 0;
    }

    // ── Luminosité ─────────────────────────────────────────────────────────────────────────────────────────────────
    private static readonly Guid SousGroupeVideo = new("7516b95f-f776-4464-8c53-06167f40cc99");
    private static readonly Guid LuminositeVideo = new("aded5e82-b909-4619-9949-f5d71dac0bcb");

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroup, ref Guid setting, out uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroup, ref Guid setting, out uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroup, ref Guid setting, uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteDCValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroup, ref Guid setting, uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);

    private static Guid? SchemaActif()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0 || ptr == IntPtr.Zero) return null;
        try { return Marshal.PtrToStructure<Guid>(ptr); }
        finally { LocalFree(ptr); }
    }

    /// <summary>Luminosité de l'écran intégré (secteur, batterie) dans le schéma d'alimentation actif.</summary>
    public static (uint Secteur, uint Batterie)? LireLuminosite()
    {
        if (SchemaActif() is not Guid s) return null;
        Guid g = SousGroupeVideo, r = LuminositeVideo;
        if (PowerReadACValueIndex(IntPtr.Zero, ref s, ref g, ref r, out uint ac) != 0) return null;
        if (PowerReadDCValueIndex(IntPtr.Zero, ref s, ref g, ref r, out uint dc) != 0) return null;
        return (ac, dc);
    }

    public static void EcrireLuminosite((uint Secteur, uint Batterie) valeur)
    {
        if (SchemaActif() is not Guid s) return;
        Guid g = SousGroupeVideo, r = LuminositeVideo;
        PowerWriteACValueIndex(IntPtr.Zero, ref s, ref g, ref r, valeur.Secteur);
        PowerWriteDCValueIndex(IntPtr.Zero, ref s, ref g, ref r, valeur.Batterie);
        PowerSetActiveScheme(IntPtr.Zero, ref s);
    }
}

/// <summary>Les touches de luminosité des portables ne passent pas par le crochet clavier (le microprogramme ou le
/// pilote du constructeur les traite). Faute de pouvoir les avaler, Hoverguard note la luminosité à la pose de la
/// garde et la remet si elle a changé. Au mieux : certains pilotes ne passent pas par le schéma d'alimentation.</summary>
internal sealed class GardienLuminosite
{
    private (uint Secteur, uint Batterie)? reference;

    public void Noter()
    {
        try { reference = NatifGarde.LireLuminosite(); }
        catch (Exception e) { reference = null; Journal.Erreur($"Luminosité illisible : {e.Message}"); }
    }

    public void Verifier()
    {
        if (reference is not { } r) return;
        try
        {
            if (NatifGarde.LireLuminosite() is { } actuelle && actuelle != r)
            {
                NatifGarde.EcrireLuminosite(r);
                Journal.Info("Luminosité changée sous la garde : remise");
            }
        }
        catch (Exception e) { Journal.Erreur($"Luminosité : {e.Message}"); reference = null; }
    }

    public void Oublier() => reference = null;
}

/// <summary>Fichier témoin posé pendant la garde et retiré quand elle se lève. S'il existe au lancement, Hoverguard
/// s'est arrêté en pleine garde (plantage, arrêt forcé) : la session est verrouillée.</summary>
internal static class TemoinGarde
{
    private static string Chemin => Path.Combine(Dossiers.Local, "garde-en-cours");

    public static bool Present => File.Exists(Chemin);

    public static void Poser()
    {
        try
        {
            Directory.CreateDirectory(Dossiers.Local);
            File.WriteAllText(Chemin, DateTime.UtcNow.ToString("O"));
        }
        catch (Exception e) { Journal.Erreur($"Témoin de garde : {e.Message}"); }
    }

    public static void Retirer()
    {
        try { File.Delete(Chemin); }
        catch (Exception e) { Journal.Erreur($"Témoin de garde : {e.Message}"); }
    }
}

/// <summary>
/// Gardien de la garde : un second exemplaire de l'exe (<c>Hoverguard.exe --gardien &lt;pid&gt;</c>), sans fenêtre,
/// lancé à chaque garde. Si Hoverguard disparaît pendant la garde (plantage, Gestionnaire des tâches…), il verrouille
/// aussitôt la vraie session Windows et relance Hoverguard : l'écran ne reste jamais ouvert. Il s'arrête seul quand la
/// garde est levée normalement (le témoin disparaît).
/// </summary>
internal static class Gardien
{
    public const string Argument = "--gardien";

    public static void Lancer()
    {
        try
        {
            if (Environment.ProcessPath is not string exe) return;
            Process.Start(new ProcessStartInfo(exe, $"{Argument} {Environment.ProcessId}") { UseShellExecute = false, CreateNoWindow = true });
        }
        catch (Exception e) { Journal.Erreur($"Gardien : {e.Message}"); }
    }

    public static int Veiller(int pid)
    {
        Process hoverguard;
        try { hoverguard = Process.GetProcessById(pid); }
        catch (ArgumentException) { return 0; }
        while (TemoinGarde.Present)
        {
            if (!hoverguard.WaitForExit(1000)) continue;
            if (!TemoinGarde.Present) return 0;  // garde levée juste avant la sortie
            VerrouillerSession();
            Journal.Erreur("Hoverguard s'est arrêté pendant la garde : session Windows verrouillée par le gardien");
            try
            {
                if (Environment.ProcessPath is string exe)
                    Process.Start(new ProcessStartInfo(exe, Coeur.ReglesLancement.ArgumentDemarrage) { UseShellExecute = false });
            }
            catch (Exception e) { Journal.Erreur($"Gardien, relance : {e.Message}"); }
            return 0;
        }
        return 0;
    }

    private static void VerrouillerSession() => NatifGarde.VerrouillerSession();
}
