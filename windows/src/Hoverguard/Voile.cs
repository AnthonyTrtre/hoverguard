using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Forms = System.Windows.Forms;

namespace Hoverguard;

/// <summary>Ce que montre le voile ; partagé par les fenêtres de tous les écrans.</summary>
internal sealed class EtatVoile
{
    public string Phrase = "", Compteur = "", Equipe = "";
    public string? Batterie;
    public int AgentsActifs;
    /// <summary>Quelqu'un a touché le clavier : le drone fonce vers lui et l'identification s'affiche.</summary>
    public bool Approche;

    public string EnJson(bool principal) => JsonSerializer.Serialize(new
    {
        phrase = Phrase, compteur = Compteur, equipe = Equipe, batterie = Batterie,
        enVol = AgentsActifs > 0, approche = Approche, principal,
    });
}

/// <summary>
/// Une fenêtre par écran, au-dessus de tout. Réglages (voir windows/README.md, « Le voile et Chrome ») :
/// <list type="bullet">
/// <item>calque par pixel (<c>WS_EX_LAYERED</c> posé par WPF, <c>AllowsTransparency</c>) d'opacité 254/255 (99,6 %) et
/// non 255 : Chrome et Edge ne jugent jamais « cachées » les fenêtres sous un tel calque (native window occlusion),
/// la page dessous continue de vivre. Mesuré sur le runner de la CI : on ne peut PAS ajouter WS_EX_LAYERED à une
/// fenêtre WPF ordinaire après coup (Windows ignore le bit), d'où AllowsTransparency dès la création ;</item>
/// <item><c>WS_EX_TRANSPARENT</c> : transparente aux clics ; ceux des agents atteignent l'app dessous, ceux d'une
/// main humaine sont arrêtés par le <see cref="Filtre"/> ;</item>
/// <item><c>WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE</c> : absente de la barre des tâches et d'Alt+Tab, ne prend jamais le
/// clavier à l'app où travaille l'agent ;</item>
/// <item><c>SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)</c> : absente des captures d'écran (Windows 10 2004 ou
/// plus récent) ; l'agent voit l'app, pas le voile.</item>
/// </list>
/// </summary>
internal sealed class FenetreVoile : Window
{
    public const byte Opacite = 254;

    private readonly Forms.Screen ecran;
    public readonly bool Principal;
    private readonly bool apercu;
    /// <summary>Contrôle « composition » : la page est dessinée dans l'arbre visuel WPF, seule façon de l'afficher
    /// dans une fenêtre AllowsTransparency (le contrôle WebView2 classique, fenêtre enfant, n'y apparaît pas).</summary>
    private WebView2CompositionControl? vue;
    private readonly Grid racine = new();
    private readonly StackPanel secours;
    private bool pret;
    public IntPtr Hwnd { get; private set; }
    public bool AffiniteAppliquee { get; private set; }
    /// <summary>Mesure envoyée par la page (images par seconde), pour le test automatique.</summary>
    public double ImagesParSeconde { get; private set; }
    public bool Drone3D => pret;

    public FenetreVoile(Forms.Screen ecran, bool principal, bool apercu)
    {
        this.ecran = ecran;
        Principal = principal;
        this.apercu = apercu;
        Title = "Hoverguard — voile";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(Opacite, 0, 0, 0));
        racine.Opacity = Opacite / 255.0;  // la page du drone est opaque : on la ramène elle aussi à 254/255
        Left = ecran.Bounds.Left; Top = ecran.Bounds.Top; Width = 400; Height = 300;  // corrigé en pixels plus bas
        secours = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        racine.Children.Add(secours);
        Content = racine;
        SourceInitialized += (_, _) => Preparer();
    }

    private void Preparer()
    {
        Hwnd = new WindowInteropHelper(this).Handle;
        // Les styles du voile ne doivent jamais disparaître (transparence aux clics, absence d'Alt+Tab…).
        HwndSource.FromHwnd(Hwnd)?.AddHook(GarderStyles);
        AppliquerStyles();
        Noter("styles posés");
        AffiniteAppliquee = Natif.SetWindowDisplayAffinity(Hwnd, apercu ? Natif.WDA_NONE : Natif.WDA_EXCLUDEFROMCAPTURE);
        Noter("affinité");
        Placer();
        Noter("placée");
    }

    /// <summary>Relevé des styles à chaque étape, lu par le test automatique.</summary>
    public readonly System.Collections.Generic.List<string> Releve = new();
    public void Noter(string etape)
    {
        bool ok = Natif.GetLayeredWindowAttributes(Hwnd, out _, out byte a, out uint d);
        Releve.Add($"{etape} : ex=0x{Natif.StyleEtendu(Hwnd):X} calque={(ok ? $"{a}/{d}" : $"non ({System.Runtime.InteropServices.Marshal.GetLastWin32Error()})")}");
    }

    private const long StylesVoile = Natif.WS_EX_LAYERED | Natif.WS_EX_TRANSPARENT | Natif.WS_EX_TOOLWINDOW | Natif.WS_EX_NOACTIVATE;

    private void AppliquerStyles()
    {
        // Pas de SetLayeredWindowAttributes : WPF dessine ce calque par pixel (UpdateLayeredWindow).
        Natif.AjouterStyleEtendu(Hwnd, StylesVoile);
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct STYLESTRUCT { public uint Ancien, Nouveau; }

    private IntPtr GarderStyles(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool traite)
    {
        const int WM_STYLECHANGING = 0x7C;
        if (message == WM_STYLECHANGING && wParam.ToInt32() == Natif.GWL_EXSTYLE)
        {
            var s = System.Runtime.InteropServices.Marshal.PtrToStructure<STYLESTRUCT>(lParam);
            s.Nouveau |= (uint)StylesVoile;
            System.Runtime.InteropServices.Marshal.StructureToPtr(s, lParam, false);
        }
        return IntPtr.Zero;
    }

    /// <summary>Vrai si la fenêtre a encore tous ses réglages (vérifié aussi par le test automatique).</summary>
    public bool StylesIntacts
    {
        get
        {
            return (Natif.StyleEtendu(Hwnd) & StylesVoile) == StylesVoile;
        }
    }

    /// <summary>Recouvre exactement l'écran (en pixels physiques) et repasse au-dessus des autres fenêtres
    /// « toujours au premier plan », sans jamais prendre le clavier.</summary>
    public void Placer()
    {
        if (Hwnd == IntPtr.Zero) return;
        if (!StylesIntacts)
        {
            Journal.Erreur("Styles du voile perdus : réappliqués");
            AppliquerStyles();
        }
        var b = ecran.Bounds;
        Natif.SetWindowPos(Hwnd, Natif.HWND_TOPMOST, b.Left, b.Top, b.Width, b.Height, Natif.SWP_NOACTIVATE | Natif.SWP_SHOWWINDOW);
    }

    public async Task DemarrerDrone(CoreWebView2Environment? environnement, string etatJson)
    {
        Secours(etatJson);
        if (environnement is null) return;
        try
        {
            vue = new WebView2CompositionControl { DefaultBackgroundColor = System.Drawing.Color.Black, Focusable = false };
            racine.Children.Add(vue);
            Noter("avant WebView2");
            await vue.EnsureCoreWebView2Async(environnement);
            Noter("après WebView2");
            var c = vue.CoreWebView2;
            var s = c.Settings;
            s.AreDevToolsEnabled = false;
            s.AreDefaultContextMenusEnabled = false;
            s.AreDefaultScriptDialogsEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = false;
            s.IsStatusBarEnabled = false;
            s.IsZoomControlEnabled = false;
            s.IsPinchZoomEnabled = false;
            s.IsSwipeNavigationEnabled = false;
            s.IsPasswordAutosaveEnabled = false;
            s.IsGeneralAutofillEnabled = false;
            s.IsBuiltInErrorPageEnabled = false;
            s.IsWebMessageEnabled = true;
            // Aucun réseau : toutes les requêtes passent par ici ; seules les pages embarquées répondent.
            c.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            c.WebResourceRequested += (_, e) => e.Response = Ressources.Repondre(environnement, e.Request.Uri);
            c.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith(Ressources.Origine, StringComparison.Ordinal)) e.Cancel = true; };
            c.NewWindowRequested += (_, e) => e.Handled = true;
            c.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            c.WebMessageReceived += (_, e) =>
            {
                try
                {
                    using var d = JsonDocument.Parse(e.WebMessageAsJson);
                    if (d.RootElement.TryGetProperty("ips", out var ips)) ImagesParSeconde = ips.GetDouble();
                    if (d.RootElement.TryGetProperty("pret", out JsonElement _))
                    {
                        pret = true;
                        secours.Visibility = Visibility.Collapsed;
                    }
                }
                catch (JsonException) { }
            };
            c.Navigate($"{Ressources.Origine}drone.html?principal={(Principal ? 1 : 0)}");
            Placer();  // revérifie les styles après la création de la vue WebView2
            Noter("après navigation");
            Actualiser(etatJson);
        }
        catch (Exception e)
        {
            Journal.Erreur($"Drone 3D indisponible ({e.GetType().Name}) : textes seuls");
            if (vue is not null) { racine.Children.Remove(vue); vue.Dispose(); vue = null; }
        }
    }

    public void Actualiser(string etatJson)
    {
        Secours(etatJson);
        try { vue?.CoreWebView2?.PostWebMessageAsJson(etatJson); }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    }

    /// <summary>Sans WebView2 (runtime absent, panne), le voile reste opaque et affiche au moins les textes.</summary>
    private void Secours(string etatJson)
    {
        using var d = JsonDocument.Parse(etatJson);
        var r = d.RootElement;
        secours.Children.Clear();
        if (r.GetProperty("approche").GetBoolean()) return;
        void Ligne(string? texte, double taille, Brush couleur)
        {
            if (string.IsNullOrEmpty(texte)) return;
            secours.Children.Add(new TextBlock
            {
                Text = texte, FontSize = taille, Foreground = couleur, TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap, MaxWidth = 900, Margin = new Thickness(0, 6, 0, 6),
            });
        }
        Ligne(r.GetProperty("phrase").GetString(), 32, Brushes.White);
        Ligne(r.GetProperty("compteur").GetString(), 22, r.GetProperty("enVol").GetBoolean() ? Brushes.Cyan : Brushes.Gray);
        Ligne(r.GetProperty("equipe").GetString(), 18, new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)));
        Ligne(r.GetProperty("batterie").GetString(), 18, Brushes.Orange);
        Ligne("Appuyez sur une touche pour vous identifier", 14, new SolidColorBrush(Color.FromArgb(115, 255, 255, 255)));
    }

    protected override void OnClosed(EventArgs e)
    {
        vue?.Dispose();
        base.OnClosed(e);
    }
}

/// <summary>Pages du drone, embarquées dans l'exe et servies à WebView2 sans aucun accès disque ni réseau.</summary>
internal static class Ressources
{
    public const string Origine = "https://hoverguard.local/";
    private static readonly Assembly Assemblage = typeof(Ressources).Assembly;
    private static readonly Dictionary<string, string> Noms = Assemblage.GetManifestResourceNames()
        .Where(n => n.StartsWith("Web/", StringComparison.Ordinal) || n.StartsWith("Web\\", StringComparison.Ordinal))
        .ToDictionary(n => n[4..].Replace('\\', '/'), n => n, StringComparer.Ordinal);

    public static bool Existe(string chemin) => Noms.ContainsKey(chemin);

    public static CoreWebView2WebResourceResponse Repondre(CoreWebView2Environment env, string uri)
    {
        if (uri.StartsWith(Origine, StringComparison.Ordinal))
        {
            var chemin = uri[Origine.Length..].Split('?', '#')[0];
            if (Noms.TryGetValue(chemin, out var nom) && Assemblage.GetManifestResourceStream(nom) is Stream flux)
            {
                var type = Path.GetExtension(chemin) switch
                {
                    ".html" => "text/html; charset=utf-8",
                    ".js" => "text/javascript; charset=utf-8",
                    ".css" => "text/css; charset=utf-8",
                    _ => "application/octet-stream",
                };
                return env.CreateWebResourceResponse(flux, 200, "OK",
                    $"Content-Type: {type}\r\nCache-Control: no-store\r\nContent-Security-Policy: default-src 'self' 'unsafe-inline'; connect-src 'none'; img-src 'self' data: blob:");
            }
        }
        return env.CreateWebResourceResponse(null, 403, "Refusé", "");
    }
}

/// <summary>Le voile sur tous les écrans.</summary>
internal sealed class Voile
{
    private readonly List<FenetreVoile> fenetres = new();
    private CoreWebView2Environment? environnement;
    private bool environnementEssaye;
    public readonly EtatVoile Etat = new();

    public bool EstPose => fenetres.Count > 0;
    public IReadOnlyList<FenetreVoile> Fenetres => fenetres;

    /// <summary><c>HOVERGUARD_APERCU=1</c> : voile visible dans les captures, pour vérifier son rendu (développement et
    /// test automatique seulement). La garde se lève alors toute seule au bout de 12 s.</summary>
    public static bool Apercu => Environment.GetEnvironmentVariable("HOVERGUARD_APERCU") is "1";

    /// <summary>Faux si Windows refuse d'exclure le voile des captures (Windows 10 antérieur à 2004) : les agents
    /// ne verraient alors qu'un écran noir.</summary>
    /// <param name="fenetresPosees">Appelé dès que les fenêtres noires couvrent les écrans, avant le chargement du
    /// drone (≈ 1 s) : la garde bloque le clavier sans attendre WebView2.</param>
    public async Task<bool> Poser(bool? apercu = null, Action? fenetresPosees = null)
    {
        Retirer();
        var devant = Natif.GetForegroundWindow();
        var principal = Forms.Screen.PrimaryScreen;
        foreach (var ecran in Forms.Screen.AllScreens)
        {
            var f = new FenetreVoile(ecran, ecran.Equals(principal), apercu ?? Apercu);
            f.Show();
            fenetres.Add(f);
        }
        if (fenetres.Any(f => !f.AffiniteAppliquee))
        {
            Journal.Erreur("SetWindowDisplayAffinity refusé : voile retiré");
            Retirer();
            return false;
        }
        RendreLaMain(devant);
        fenetresPosees?.Invoke();
        var env = await Environnement();
        foreach (var f in fenetres.ToList())
            await f.DemarrerDrone(env, Etat.EnJson(f.Principal));
        RendreLaMain(devant);  // par prudence : WebView2 ne doit pas garder le clavier
        return true;
    }

    private static void RendreLaMain(IntPtr fenetre)
    {
        var devant = Natif.GetForegroundWindow();
        Natif.GetWindowThreadProcessId(devant, out uint pid);
        if (fenetre != IntPtr.Zero && devant != fenetre && pid == Environment.ProcessId) Natif.Activer(fenetre);
    }

    private async Task<CoreWebView2Environment?> Environnement()
    {
        if (environnementEssaye) return environnement;
        environnementEssaye = true;
        try
        {
            var options = new CoreWebView2EnvironmentOptions(
                // Pas de services réseau d'arrière-plan du moteur Edge (mises à jour de composants, rapports, synchro…).
                "--disable-background-networking --disable-component-update --disable-domain-reliability --disable-sync " +
                "--no-pings --disable-breakpad --disable-features=msSmartScreenProtection,SpareRendererForSitePerProcess");
            environnement = await CoreWebView2Environment.CreateAsync(null, Dossiers.WebView2, options);
        }
        catch (Exception e)
        {
            Journal.Erreur($"WebView2 indisponible : {e.Message}");
        }
        return environnement;
    }

    public void Actualiser()
    {
        foreach (var f in fenetres) f.Actualiser(Etat.EnJson(f.Principal));
    }

    /// <summary>Repasse au-dessus des fenêtres « toujours au premier plan » apparues entre-temps.</summary>
    public void Replacer()
    {
        foreach (var f in fenetres) f.Placer();
    }

    public void Retirer()
    {
        foreach (var f in fenetres) f.Close();
        fenetres.Clear();
    }
}
