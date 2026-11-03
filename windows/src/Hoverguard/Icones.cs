using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;
using Hoverguard.Coeur;
using Microsoft.Win32;

namespace Hoverguard;

/// <summary>Logo officiel « Sentinelle » (<c>docs/logo/logo.svg</c>) et icônes de la zone de notification, embarqués
/// dans l'exe depuis <c>windows/ressources</c>. Les icônes de notification se régénèrent avec
/// <c>windows/ressources/generer-icones.py</c>.</summary>
internal static class Icones
{
    /// <summary>Ancien jeu de formes, gardé pour compatibilité : il n'y a plus que deux états, comme sur macOS
    /// (libre : drone posé ; garde : bouclier plein). Seul <see cref="Cadenas"/> donne l'icône de garde.</summary>
    public enum Forme
    {
        Bouclier,
        Demi,
        Plein,
        Cadenas,
    }

    private static readonly Dictionary<string, Icon> Cache = new();
    private static readonly Dictionary<int, BitmapSource> CacheLogo = new();

    private static Stream Ressource(string nom)
        => Assembly.GetExecutingAssembly().GetManifestResourceStream($"Icones/{nom}")
           ?? throw new InvalidOperationException($"Ressource absente de l'exe : {nom}");

    /// <summary>Vrai si la barre des tâches est claire (réglage « Choisir votre mode Windows » = Clair).</summary>
    public static bool BarreClaire
    {
        get
        {
            try
            {
                using var cle = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return cle?.GetValue("SystemUsesLightTheme") is int v && v == 1;
            }
            catch (Exception) { return false; }  // par défaut, Windows 11 a une barre sombre
        }
    }

    /// <summary>Icône de la zone de notification, à la taille des petites icônes du système (16 à 32 px selon
    /// l'échelle d'affichage).</summary>
    public static Icon Notification(bool garde)
    {
        var nom = IconesNotification.Nom(garde, BarreClaire);
        if (Cache.TryGetValue(nom, out var icone)) return icone;
        using var flux = Ressource(nom);
        icone = new Icon(flux, System.Windows.Forms.SystemInformation.SmallIconSize);
        Cache[nom] = icone;
        return icone;
    }

    /// <summary>Le logo de l'app (même fichier que l'icône de l'exe), pour les fenêtres et l'écran « À propos ».</summary>
    public static BitmapSource Logo(int taille = 32)
    {
        if (CacheLogo.TryGetValue(taille, out var image)) return image;
        using var flux = Ressource("hoverguard.ico");
        var decodeur = new IconBitmapDecoder(flux, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        // La plus petite image au moins aussi grande que demandé, sinon la plus grande.
        BitmapFrame? choisie = null;
        foreach (var f in decodeur.Frames)
        {
            bool assez = f.PixelWidth >= taille;
            if (choisie is null
                || (assez && (choisie.PixelWidth < taille || f.PixelWidth < choisie.PixelWidth))
                || (!assez && choisie.PixelWidth < taille && f.PixelWidth > choisie.PixelWidth))
                choisie = f;
        }
        image = choisie ?? decodeur.Frames[0];
        image.Freeze();
        CacheLogo[taille] = image;
        return image;
    }

    /// <summary>Compatibilité : icône de notification selon l'ancienne forme.</summary>
    public static Icon Icone(Forme forme, int taille = 32) => Notification(forme == Forme.Cadenas);

    /// <summary>Compatibilité : le logo, quelle que soit l'ancienne forme.</summary>
    public static BitmapSource Image(Forme forme, int taille = 32) => Logo(taille);
}
