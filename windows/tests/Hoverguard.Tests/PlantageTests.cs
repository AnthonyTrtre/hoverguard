using System.Text.RegularExpressions;

namespace Hoverguard.Tests;

public class PlantageTests
{
    static Exception Levee()
    {
        try { throw new InvalidOperationException("Voile introuvable\nsur l'écran 2"); }
        catch (Exception e) { return e; }
    }

    [Fact]
    public void LeFichierGardeTypeMessageEtPile()
    {
        var contenu = RapportPlantage.Formater(Levee(), fatal: true, "1.1.2", new DateTime(2026, 11, 4, 9, 0, 0));
        Assert.Contains("Hoverguard 1.1.2", contenu);
        Assert.Contains("Date : 2026-11-04 09:00:00", contenu);
        Assert.Contains("Type : System.InvalidOperationException", contenu);
        Assert.Contains("Message : Voile introuvable sur l'écran 2", contenu);
        Assert.True(RapportPlantage.EstFatal(contenu));
        Assert.False(RapportPlantage.EstFatal(RapportPlantage.Formater(Levee(), fatal: false, "1.1.2", DateTime.Now)));
        Assert.True(RapportPlantage.EstFatal("illisible"));
    }

    [Fact]
    public void ExtraitVersionTypeMessageEtDebutDeLaPile()
    {
        var pile = string.Join("\r\n", Enumerable.Range(0, 30).Select(i => $"   at Hoverguard.F{i}()"));
        var contenu = $"Hoverguard 1.1.1\r\nDate : x\r\nFatal : oui\r\nType : System.Exception\r\nMessage : boum\r\nPile :\r\nSystem.Exception: boum\r\n{pile}";
        var e = RapportPlantage.Extrait(contenu);
        Assert.StartsWith("Version au plantage : 1.1.1\nType : System.Exception\nMessage : boum\nPile :\nSystem.Exception: boum\n", e);
        Assert.Contains("F13()", e);
        Assert.DoesNotContain("F14()", e);  // 15 lignes de pile, la première est le message
        Assert.DoesNotContain("Date", e);
        Assert.Equal("pas un rapport", RapportPlantage.Extrait("pas un rapport"));
    }

    [Fact]
    public void AnonymisationDuDossierDeLUtilisateurEtDeLaMachine()
    {
        var texte = @"à C:\Users\jdupont\AppData\Local\Hoverguard sur PC-DE-JEAN, compte jdupont (Jean Dupont)";
        var a = RapportPlantage.Anonymiser(texte, @"C:\Users\jdupont", ["jdupont", "Jean Dupont"], ["PC-DE-JEAN"]);
        Assert.Equal(@"à ~\AppData\Local\Hoverguard sur <machine>, compte <utilisateur> (<utilisateur>)", a);
        Assert.Equal("abc", RapportPlantage.Anonymiser("abc", "", ["a"], [""]));
    }

    static Dictionary<string, string> Requete(string url) =>
        new Uri(url).Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

    [Fact]
    public void UrlDeLIssuePreRemplie()
    {
        var url = RapportPlantage.Url("1.1.2", "Windows 11", "x64", "Type : System.Exception");
        Assert.StartsWith("https://github.com/AnthonyTrtre/hoverguard/issues/new?", url);
        var q = Requete(url);
        Assert.Equal("plantage", q["labels"]);
        Assert.Equal("Plantage de Hoverguard 1.1.2", q["title"]);
        Assert.Contains("**Version** : 1.1.2", q["body"]);
        Assert.Contains("Type : System.Exception", q["body"]);
        Assert.DoesNotContain(RapportPlantage.Tronque, q["body"]);
    }

    [Fact]
    public void UnRapportTropLongEstTronque()
    {
        var url = RapportPlantage.Url("1.1.2", "Windows", "x64", string.Concat(Enumerable.Repeat("ligne é + & = #\n", 2000)));
        Assert.True(url.Length <= RapportPlantage.TailleMaxUrl);
        var corps = Requete(url)["body"];
        Assert.Contains(RapportPlantage.Tronque, corps);
        Assert.Contains("ligne é + & = #", corps);
        Assert.True(RapportPlantage.Url("1.1.2", "Windows", "x64", corps, RapportPlantage.TailleMaxUrlWindows).Length <= 2000);
    }
}

/// <summary>Garde-fou du plantage 0xC0000374 de la 1.1.1 : un appel Windows qui reçoit du texte (char[], string,
/// StringBuilder) doit déclarer CharSet, sinon .NET le marshale en ANSI et Windows déborde du tampon.</summary>
public class AppelsWindowsTests
{
    [Fact]
    public void ToutAppelAvecDuTexteDeclareSonCharSet()
    {
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !Directory.Exists(Path.Combine(dossier.FullName, "src", "Hoverguard"))) dossier = dossier.Parent;
        Assert.NotNull(dossier);
        var fautifs = new List<string>();
        foreach (var f in Directory.GetFiles(Path.Combine(dossier!.FullName, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var lignes = File.ReadAllLines(f);
            for (int i = 0; i < lignes.Length; i++)
            {
                if (!lignes[i].Contains("[DllImport(") || lignes[i].Contains("CharSet")) continue;
                var suite = string.Join(" ", lignes.Skip(i).Take(3));
                var declaration = suite[..(suite.IndexOf(';') is int fin and >= 0 ? fin : suite.Length)];
                if (Regex.IsMatch(declaration, @"char\[\]|\bstring\b|StringBuilder"))
                    fautifs.Add($"{Path.GetFileName(f)}:{i + 1}");
            }
        }
        Assert.Empty(fautifs);
        Assert.Contains("CharSet = CharSet.Unicode, ExactSpelling = true)]\n    public static extern int ToUnicodeEx",
            File.ReadAllText(Path.Combine(dossier.FullName, "src", "Hoverguard", "SecuriteGarde.cs")).ReplaceLineEndings("\n"));
    }
}
