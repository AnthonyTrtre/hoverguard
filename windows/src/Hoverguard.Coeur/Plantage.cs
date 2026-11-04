using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Hoverguard.Coeur;

/// <summary>Rapport de plantage, sans serveur ni envoi automatique : les gestionnaires d'erreur écrivent
/// <c>plantage.txt</c> à côté du journal, et au lancement suivant Hoverguard propose d'ouvrir une issue GitHub
/// pré-remplie et anonymisée. Seul le clic de l'utilisateur ouvre le navigateur.</summary>
public static class RapportPlantage
{
    public const int TailleMaxUrl = 6000;
    /// <summary>Windows ouvre mal les adresses de plus de 2 048 caractères (ShellExecute) : bornée plus court.</summary>
    public const int TailleMaxUrlWindows = 2000;
    public const string Tronque = "(rapport tronqué, joindre le fichier)";
    public const string AdresseIssues = "https://github.com/AnthonyTrtre/hoverguard/issues/new";
    public const int LignesDePile = 15;

    /// <summary>Contenu de <c>plantage.txt</c>. <paramref name="fatal"/> : l'app s'est arrêtée (sinon l'erreur a été
    /// rattrapée et Hoverguard a continué).</summary>
    public static string Formater(Exception e, bool fatal, string version, DateTime date) =>
        $"""
        Hoverguard {version}
        Date : {date:yyyy-MM-dd HH:mm:ss}
        Fatal : {(fatal ? "oui" : "non")}
        Type : {e.GetType().FullName}
        Message : {e.Message.ReplaceLineEndings(" ")}
        Pile :
        {e}
        """;

    /// <summary>Le rapport vient-il d'un arrêt de l'app ? (Un fichier illisible compte comme un arrêt.)</summary>
    public static bool EstFatal(string contenu) => !contenu.Split('\n').Any(l => l.TrimEnd() == "Fatal : non");

    /// <summary>L'essentiel : version, type, message et les premières lignes de la pile.</summary>
    public static string Extrait(string contenu)
    {
        var lignes = contenu.ReplaceLineEndings("\n").Split('\n');
        var pile = Array.FindIndex(lignes, l => l == "Pile :");
        if (pile < 0) return contenu.Length > 1500 ? contenu[..1500] : contenu;
        var entete = lignes.Take(pile).Where(l => l.StartsWith("Hoverguard ") || l.StartsWith("Type : ") || l.StartsWith("Message : "))
            .Select(l => l.StartsWith("Hoverguard ") ? $"Version au plantage : {l["Hoverguard ".Length..]}" : l);
        var corps = lignes.Skip(pile + 1).Where(l => l.Trim().Length > 0).Take(LignesDePile);
        return string.Join("\n", entete.Append("Pile :").Concat(corps));
    }

    /// <summary>Retire ce qui identifie la personne : dossier personnel → <c>~</c>, noms de machine →
    /// <c>&lt;machine&gt;</c>, nom d'utilisateur → <c>&lt;utilisateur&gt;</c>. Les noms de moins de 3 caractères sont
    /// laissés (ils abîmeraient tout le texte).</summary>
    public static string Anonymiser(string texte, string dossierPersonnel, IEnumerable<string> utilisateurs, IEnumerable<string> machines)
    {
        var t = texte;
        if (dossierPersonnel.Length > 3) t = t.Replace(dossierPersonnel, "~", StringComparison.OrdinalIgnoreCase);
        foreach (var m in machines.Where(m => m.Length >= 3).OrderByDescending(m => m.Length))
            t = t.Replace(m, "<machine>", StringComparison.OrdinalIgnoreCase);
        foreach (var u in utilisateurs.Where(u => u.Length >= 3).OrderByDescending(u => u.Length))
            t = t.Replace(u, "<utilisateur>", StringComparison.OrdinalIgnoreCase);
        return t;
    }

    /// <summary>« Un souci ou une idée ? » du menu : une issue vierge avec la version et le système, rien d'autre.</summary>
    public static string UrlRetour(string version, string systeme)
    {
        var corps = $"**Version** : {version}\n**Système** : {systeme}\n\nCe qui s'est passé (ou votre idée) :\n\n\nCe que vous attendiez :\n\n";
        return $"{AdresseIssues}?title={Uri.EscapeDataString($"Retour sur Hoverguard {version}")}&body={Uri.EscapeDataString(corps)}&labels=retour";
    }

    /// <summary>Adresse de la nouvelle issue, bornée à <paramref name="tailleMax"/> caractères : au-delà, l'extrait
    /// est tronqué.</summary>
    public static string Url(string version, string systeme, string architecture, string extrait, int tailleMax = TailleMaxUrl)
    {
        string Adresse(string e)
        {
            var corps = $"**Version** : {version}\n**Système** : {systeme}\n**Architecture** : {architecture}\n\n```\n{e}\n```\n";
            return $"{AdresseIssues}?title={Uri.EscapeDataString($"Plantage de Hoverguard {version}")}&body={Uri.EscapeDataString(corps)}&labels=plantage";
        }
        var e = extrait;
        if (Adresse(e).Length > tailleMax)
        {
            do e = e[..Math.Max(0, e.Length - 100)];
            while (e.Length > 0 && Adresse(e + "\n" + Tronque).Length > tailleMax);
            e += "\n" + Tronque;
        }
        return Adresse(e);
    }
}
