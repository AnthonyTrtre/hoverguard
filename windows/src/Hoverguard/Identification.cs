using System;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace Hoverguard;

/// <summary>
/// Demande d'identification affichée sur l'écran principal quand le drone s'approche : le mot de passe Hoverguard,
/// choisi par l'utilisateur dans les réglages. Fenêtre WPF native (et non dans la page du drone), exclue des captures, qui NE PREND PAS le
/// clavier (<c>WS_EX_NOACTIVATE</c>) : le crochet clavier avale les touches et le contrôleur remplit le mot de passe
/// (<see cref="Hoverguard.Coeur.TamponMotDePasse"/>, un SecureString). La fenêtre n'affiche que des points.
/// </summary>
internal sealed class FenetreIdentification : Window
{
    private readonly Border cadreSaisie;
    private readonly TextBlock points, erreur, aide;
    private static readonly Brush Cyan = (Brush)new SolidColorBrush(Color.FromRgb(20, 204, 255)).GetAsFrozen();
    public IntPtr Hwnd { get; private set; }
    public DateTime DerniereActivite { get; private set; } = DateTime.UtcNow;

    public FenetreIdentification()
    {
        Title = "Hoverguard — identification";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        var blanc = (Brush)new SolidColorBrush(Color.FromArgb(178, 255, 255, 255)).GetAsFrozen();
        points = new TextBlock { FontSize = 18, Foreground = Brushes.White, Text = "", TextTrimming = TextTrimming.None };
        cadreSaisie = new Border
        {
            Width = 320, MinHeight = 42, Padding = new Thickness(14, 8, 14, 8), Margin = new Thickness(0, 14, 0, 0),
            Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), BorderBrush = Cyan,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Child = points,
        };
        erreur = new TextBlock { Foreground = Brushes.IndianRed, FontSize = 14, Margin = new Thickness(0, 10, 0, 0), TextAlignment = TextAlignment.Center, Visibility = Visibility.Collapsed };
        aide = new TextBlock
        {
            Foreground = (Brush)new SolidColorBrush(Color.FromArgb(115, 255, 255, 255)).GetAsFrozen(), FontSize = 13,
            Margin = new Thickness(0, 18, 0, 0), TextAlignment = TextAlignment.Center,
            Text = "Entrée pour valider · Échap pour laisser le drone surveiller",
        };
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(225, 8, 12, 18)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(90, 20, 204, 255)), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18), Padding = new Thickness(36, 26, 36, 22),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "Identification", FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock
                    {
                        Text = "Votre mot de passe Hoverguard",
                        Foreground = blanc, FontSize = 15, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Center,
                    },
                    cadreSaisie, erreur, aide,
                },
            },
        };
        SourceInitialized += (_, _) =>
        {
            Hwnd = new WindowInteropHelper(this).Handle;
            Natif.AjouterStyleEtendu(Hwnd, Natif.WS_EX_TOOLWINDOW | Natif.WS_EX_NOACTIVATE);
            if (!Voile.Apercu) Natif.SetWindowDisplayAffinity(Hwnd, Natif.WDA_EXCLUDEFROMCAPTURE);
        };
        Loaded += (_, _) => Centrer();
    }

    public static string NomAffiche()
    {
        uint taille = 256;
        var nom = new StringBuilder((int)taille);
        return Natif.GetUserNameEx(Natif.NameDisplay, nom, ref taille) && nom.Length > 0 ? nom.ToString() : Environment.UserName;
    }

    /// <summary>Centrée, dans le tiers bas de l'écran principal (le drone occupe le haut).</summary>
    private void Centrer()
    {
        var zone = Forms.Screen.PrimaryScreen!.Bounds;
        var dpi = VisualTreeHelper.GetDpi(this);
        Left = zone.Left / dpi.DpiScaleX + (zone.Width / dpi.DpiScaleX - ActualWidth) / 2;
        Top = zone.Top / dpi.DpiScaleY + zone.Height / dpi.DpiScaleY * 0.62;
    }

    /// <summary>Affiche la fenêtre au-dessus du voile SANS l'activer : la fenêtre de l'agent garde le clavier.</summary>
    public void Montrer()
    {
        if (!IsVisible) Show();
        Natif.SetWindowPos(Hwnd, Natif.HWND_TOPMOST, 0, 0, 0, 0, Natif.SWP_NOMOVE | Natif.SWP_NOSIZE | Natif.SWP_NOACTIVATE);
        DerniereActivite = DateTime.UtcNow;
    }

    /// <summary>Une touche a été lue par le crochet.</summary>
    public void Activite() => DerniereActivite = DateTime.UtcNow;

    /// <summary>Nombre de caractères tapés : seuls des points sont affichés.</summary>
    public int Points
    {
        set => points.Text = new string('•', Math.Min(value, 40));
    }

    /// <summary>Vérification ou attente en cours : la saisie est grisée.</summary>
    public bool Occupe
    {
        set => cadreSaisie.Opacity = value ? 0.5 : 1;
    }

    public string? Erreur
    {
        set
        {
            erreur.Text = value ?? "";
            erreur.Visibility = value is null ? Visibility.Collapsed : Visibility.Visible;
            cadreSaisie.BorderBrush = value is null ? Cyan : Brushes.IndianRed;
        }
    }
}
