using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Hoverguard.Coeur;
using Forms = System.Windows.Forms;

namespace Hoverguard;

/// <summary>
/// <c>Hoverguard.exe --autotest dossier</c> : mesures sur un vrai Windows, lancées par la CI (runner windows-latest).
/// Ne touche ni au registre ni aux réglages. Écrit <c>resultats.txt</c> et des captures PNG dans le dossier.
/// Ce test ne remplace pas un essai à la main : les entrées physiques (vrai clavier, vraie souris), le mot de passe Hoverguard,
/// la webcam et plusieurs écrans n'existent pas sur le runner.
/// </summary>
internal static class AutoTest
{
    private static readonly StringBuilder Lignes = new();
    private static string dossier = "";
    private static int echecs;

    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);

    public static int Lancer(string chemin)
    {
        dossier = Path.GetFullPath(chemin);
        Directory.CreateDirectory(dossier);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try { await Scenario(); }
            catch (Exception e) { Noter(false, $"exception : {e}"); }
            finally
            {
                Ecrire($"\nBILAN : {(echecs == 0 ? "tout est conforme" : $"{echecs} écart(s)")}");
                File.WriteAllText(Path.Combine(dossier, "resultats.txt"), Lignes.ToString(), Encoding.UTF8);
                app.Shutdown(echecs == 0 ? 0 : 1);
            }
        };
        return app.Run();
    }

    private static void Ecrire(string texte) => Lignes.AppendLine(texte);
    private static void Noter(bool ok, string texte) { if (!ok) echecs++; Ecrire($"{(ok ? "OK    " : "ÉCART ")} {texte}"); }
    private static Task Attendre(double secondes) => Task.Delay(TimeSpan.FromSeconds(secondes));

    private static Bitmap Capturer(string? nom)
    {
        var zone = Forms.Screen.PrimaryScreen!.Bounds;
        var image = new Bitmap(zone.Width, zone.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(image)) g.CopyFromScreen(zone.Location, System.Drawing.Point.Empty, zone.Size, CopyPixelOperation.SourceCopy);
        if (nom is not null) image.Save(Path.Combine(dossier, nom), ImageFormat.Png);
        return image;
    }

    private static bool EstMagenta(System.Drawing.Color c) => c.R > 200 && c.G < 60 && c.B > 200;

    private static double Part(Bitmap image, Rectangle zone, Func<System.Drawing.Color, bool> test)
    {
        int total = 0, oui = 0;
        for (int y = zone.Top; y < zone.Bottom; y += 7)
            for (int x = zone.Left; x < zone.Right; x += 7)
            {
                if (x < 0 || y < 0 || x >= image.Width || y >= image.Height) continue;
                total++;
                if (test(image.GetPixel(x, y))) oui++;
            }
        return total == 0 ? 0 : (double)oui / total;
    }

    private static async Task Scenario()
    {
        Ecrire($"Hoverguard — test automatique, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Ecrire($"Windows {Environment.OSVersion.Version}, {Forms.Screen.AllScreens.Length} écran(s), principal {Forms.Screen.PrimaryScreen!.Bounds}");
        Ecrire($"Session interactive : {Environment.UserInteractive}, utilisateur {Environment.UserName}");
        Ecrire($"Ressources embarquées : drone.html={Ressources.Existe("drone.html")}, three={Ressources.Existe("three/three.module.min.js")}");
        Noter(Ressources.Existe("drone.html") && Ressources.Existe("drone.js") && Ressources.Existe("three/three.module.min.js"), "pages du drone embarquées dans l'exe");
        Ecrire("");


        // ── Fenêtre cible : joue le rôle de l'app pilotée par l'agent ────────────────────────────────────────────────
        int clics = 0;
        var saisie = new TextBox { FontSize = 30, Margin = new Thickness(40), Background = System.Windows.Media.Brushes.Transparent, BorderThickness = new Thickness(0) };
        var cible = new Window
        {
            Title = "Cible Hoverguard", Left = 80, Top = 80, Width = 640, Height = 400,
            Background = System.Windows.Media.Brushes.Magenta, Content = saisie, WindowStyle = WindowStyle.ToolWindow,
        };
        cible.PreviewMouseDown += (_, _) => clics++;
        cible.Show();
        cible.Activate();
        saisie.Focus();
        await Attendre(1.5);
        var hCible = new WindowInteropHelper(cible).Handle;
        Natif.GetWindowRect(hCible, out var r);
        var zoneCible = Rectangle.FromLTRB(r.Left + 20, r.Top + 40, r.Right - 20, r.Bottom - 20);
        using (var avant = Capturer("1-avant.png"))
        {
            double m = Part(avant, zoneCible, EstMagenta);
            Ecrire($"Capture témoin : {m:P0} de magenta dans la fenêtre cible");
            if (m < 0.5) Ecrire("       (capture d'écran inexploitable sur ce runner : les mesures visuelles ci-dessous ne valent rien)");
        }

        // ── Voile réel, exclu des captures ─────────────────────────────────────────────────────────────────────────────
        var voile = new Voile();
        voile.Etat.Phrase = Textes.Phrases[0];
        voile.Etat.Compteur = Textes.Compteur(2, 3);
        voile.Etat.Equipe = Textes.Equipe(5);
        voile.Etat.AgentsActifs = 5;
        var chrono = Stopwatch.StartNew();
        bool pose = await voile.Poser(apercu: false);
        Noter(pose, $"voile posé sur {voile.Fenetres.Count} écran(s) en {chrono.ElapsedMilliseconds} ms");
        await Attendre(8);
        foreach (var f in voile.Fenetres)
        {
            f.Noter("fin de pose (+8 s)");
            foreach (var l in f.Releve) Ecrire($"       relevé · {l}");
            Natif.GetWindowDisplayAffinity(f.Hwnd, out uint affinite);
            Noter(affinite == Natif.WDA_EXCLUDEFROMCAPTURE, $"affinité d'affichage = 0x{affinite:X} (attendu 0x11 WDA_EXCLUDEFROMCAPTURE)");
            Noter(f.StylesIntacts, "styles et opacité toujours en place après le chargement de WebView2");
            long ex = Natif.StyleEtendu(f.Hwnd);
            Noter((ex & Natif.WS_EX_LAYERED) != 0 && (ex & Natif.WS_EX_TRANSPARENT) != 0 && (ex & Natif.WS_EX_TOOLWINDOW) != 0
                  && (ex & Natif.WS_EX_NOACTIVATE) != 0 && (ex & Natif.WS_EX_TOPMOST) != 0, $"styles étendus = 0x{ex:X}");
            // Calque par pixel (UpdateLayeredWindow) : GetLayeredWindowAttributes échoue, et c'est précisément ce que
            // Chromium traite comme « transparent ». L'opacité 254 se lit sur la capture en aperçu.
            Noter(!Natif.GetLayeredWindowAttributes(f.Hwnd, out _, out _, out _), "calque par pixel (pas d'attributs LWA, comme attendu par Chromium)");
            Noter(f.Drone3D, $"drone 3D chargé dans WebView2 (images/s mesurées par la page : {f.ImagesParSeconde:0.#})");
        }
        Noter(Natif.GetForegroundWindow() == hCible, "la fenêtre cible a gardé le clavier après la pose du voile");
        using (var pendant = Capturer("2-voile-exclu.png"))
        {
            double m = Part(pendant, zoneCible, EstMagenta);
            Noter(m > 0.5, $"capture pendant la garde : {m:P0} de magenta (l'agent voit l'app, pas le voile)");
        }

        // ── Filtre d'entrées : ce que fabriquent les agents passe ───────────────────────────────────────────────────
        using var filtre = new Filtre();
        Noter(filtre.Demarrer(), "crochets WH_KEYBOARD_LL et WH_MOUSE_LL posés");
        filtre.Etat = EtatGarde.Garde;
        bool panne = false;
        filtre.SurPanne = () => panne = true;
        int x = (zoneCible.Left + zoneCible.Right) / 2, y = (zoneCible.Top + zoneCible.Bottom) / 2;
        SetCursorPos(x, y);
        Natif.Envoyer(Natif.Souris(0, 0, Natif.MOUSEEVENTF_LEFTDOWN), Natif.Souris(0, 0, Natif.MOUSEEVENTF_LEFTUP));
        await Attendre(1);
        Noter(clics > 0, $"clic injecté traversant le voile jusqu'à la cible ({clics} reçu)");
        cible.Activate();
        saisie.Focus();
        Natif.Envoyer(Natif.Touche(0x48, false), Natif.Touche(0x48, true), Natif.Touche(0x47, false), Natif.Touche(0x47, true));
        await Attendre(1);
        Noter(saisie.Text.Contains("hg", StringComparison.OrdinalIgnoreCase), $"frappes injectées reçues par la cible : « {saisie.Text} »");
        Noter(filtre.EvenementsVus > 0, $"le filtre a vu {filtre.EvenementsVus} événement(s)");
        filtre.Sonder();
        await Attendre(4);
        Noter(!panne, "la sonde du crochet répond");
        Ecrire($"Inactivité physique mesurée par le filtre : {filtre.InactivitePhysique.TotalSeconds:0} s (les entrées injectées ne comptent pas)");
        filtre.Dispose();
        voile.Retirer();

        // ── Rendu du drone, voile visible dans les captures (aperçu) ───────────────────────────────────────────────────
        cible.Close();
        await Attendre(0.5);
        await voile.Poser(apercu: true);
        await Attendre(10);
        foreach (var f in voile.Fenetres) Ecrire($"Aperçu : images/s du drone = {f.ImagesParSeconde:0.#}");
        var ecran = Forms.Screen.PrimaryScreen!.Bounds;
        var tout = new Rectangle(0, 0, ecran.Width, ecran.Height);
        using (var vol = Capturer("3-drone-en-vol.png"))
            Noter(Part(vol, tout, c => c.R + c.G + c.B > 60) > 0.01, $"drone en vol dessiné : {Part(vol, tout, c => c.R + c.G + c.B > 60):P1} de pixels non noirs");
        voile.Etat.Approche = true;
        voile.Actualiser();
        await Attendre(4);
        Capturer("4-drone-approche.png").Dispose();
        voile.Etat.Approche = false;
        voile.Etat.AgentsActifs = 0;
        voile.Etat.Compteur = Textes.Compteur(0, 0);
        voile.Etat.Equipe = Textes.Equipe(0);
        voile.Etat.Batterie = Textes.Batterie(new EtatBatterie(15, false));
        voile.Actualiser();
        await Attendre(4);
        Capturer("5-drone-pose.png").Dispose();
        voile.Retirer();

        await Occlusion(voile);
    }

    /// <summary>Edge (Chromium) calcule l'occlusion de ses fenêtres (« native window occlusion ») : une page jugée
    /// cachée passe en <c>visibilityState = hidden</c> et cesse de dessiner. On mesure l'effet de chaque voile.</summary>
    private static async Task Occlusion(Voile voile)
    {
        Ecrire("\nOcclusion des fenêtres d'Edge sous chaque type de fenêtre plein écran :");
        var edge = new[] { @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", @"C:\Program Files\Microsoft\Edge\Application\msedge.exe" }
            .FirstOrDefault(File.Exists);
        if (edge is null) { Ecrire("       Edge introuvable : mesure impossible"); return; }
        var page = Path.Combine(dossier, "sonde.html");
        File.WriteAllText(page, """
            <!doctype html><meta charset="utf-8"><title>SONDE</title><body style="background:#246">
            <script>
            let n = 0; const f = () => { n++; requestAnimationFrame(f); }; requestAnimationFrame(f);
            const maj = () => { document.title = 'SONDE ' + document.visibilityState + ' ' + n; n = 0; };
            setInterval(maj, 1000); document.addEventListener('visibilitychange', maj);
            </script>
            """);
        var profil = Path.Combine(dossier, "profil-edge");
        using var processus = Process.Start(new ProcessStartInfo(edge,
            $"--user-data-dir=\"{profil}\" --no-first-run --no-default-browser-check --disable-sync --start-maximized --new-window \"{new Uri(page).AbsoluteUri}\"")
        { UseShellExecute = false });
        IntPtr fenetre = IntPtr.Zero;
        bool mesurable = true;
        for (int i = 0; i < 30 && fenetre == IntPtr.Zero; i++) { await Attendre(1); fenetre = Trouver("SONDE"); }
        if (fenetre == IntPtr.Zero) { Ecrire("       fenêtre d'Edge introuvable : mesure impossible"); return; }
        Natif.Activer(fenetre);
        await Attendre(3);
        Ecrire($"       sans rien devant          : {Titre(fenetre)}");

        var essais = new (string Nom, long Styles, byte? Alpha, bool Attendu)[]
        {
            ("témoin opaque (fenêtre normale)", 0, null, false),
            ("calque 255/255", Natif.WS_EX_LAYERED, 255, false),
            ("calque 254/255", Natif.WS_EX_LAYERED, 254, true),
            ("calque 255/255 + transparent + outil", Natif.WS_EX_LAYERED | Natif.WS_EX_TRANSPARENT | Natif.WS_EX_TOOLWINDOW | Natif.WS_EX_NOACTIVATE, 255, true),
        };
        foreach (var (nom, styles, alpha, attendu) in essais)
        {
            var w = new Window
            {
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, Topmost = true, ShowInTaskbar = false,
                Background = System.Windows.Media.Brushes.Black, ShowActivated = false, Left = 0, Top = 0, Width = 100, Height = 100,
            };
            w.SourceInitialized += (_, _) =>
            {
                var h = new WindowInteropHelper(w).Handle;
                if (styles != 0) Natif.AjouterStyleEtendu(h, styles);
                if (alpha is byte a) Natif.SetLayeredWindowAttributes(h, 0, a, Natif.LWA_ALPHA);
                var b = Forms.Screen.PrimaryScreen!.Bounds;
                Natif.SetWindowPos(h, Natif.HWND_TOPMOST, b.Left, b.Top, b.Width, b.Height, Natif.SWP_NOACTIVATE | Natif.SWP_SHOWWINDOW);
            };
            w.Show();
            await Attendre(6);
            var t = Titre(fenetre);
            w.Close();
            if (styles == 0 && t.Contains("visible"))
            {
                // Le témoin opaque devrait cacher Edge. S'il ne le fait pas, Edge ne calcule pas l'occlusion ici
                // (session distante du runner, fonction désactivée…) : la mesure ne prouve rien dans un sens ni dans l'autre.
                mesurable = false;
                Ecrire($"INFO   {nom,-38}: {t} — Edge ne calcule pas l'occlusion sur cette machine : mesures suivantes indicatives");
            }
            else if (mesurable) Noter(t.Contains("visible") == attendu, $"{nom,-38}: {t} (attendu : {(attendu ? "visible" : "hidden")})");
            else Ecrire($"INFO   {nom,-38}: {t}");
            await Attendre(4);
        }
        // Le vrai voile, drone compris.
        voile.Etat.AgentsActifs = 3;
        await voile.Poser(apercu: false);
        await Attendre(8);
        var titre = Titre(fenetre);
        if (mesurable) Noter(titre.Contains("visible"), $"{"voile Hoverguard réel",-38}: {titre} (attendu : visible)");
        else Ecrire($"INFO   {"voile Hoverguard réel",-38}: {titre}");
        voile.Retirer();
        try { processus?.Kill(entireProcessTree: true); } catch (Exception) { }
    }

    private static string Titre(IntPtr h)
    {
        var s = new StringBuilder(256);
        Natif.GetWindowText(h, s, s.Capacity);
        return s.ToString();
    }

    private static IntPtr Trouver(string debut)
    {
        IntPtr trouvee = IntPtr.Zero;
        Natif.EnumWindows((h, _) =>
        {
            if (Natif.IsWindowVisible(h) && Titre(h).StartsWith(debut, StringComparison.Ordinal)) { trouvee = h; return false; }
            return true;
        }, IntPtr.Zero);
        return trouvee;
    }
}
