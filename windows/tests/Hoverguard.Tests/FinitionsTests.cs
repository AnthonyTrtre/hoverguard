using System.Text.RegularExpressions;

namespace Hoverguard.Tests;

public class PhotosIntrusTests
{
    static readonly DateTime Maintenant = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void LesPhotosDePlusDe30JoursSontEffacees()
    {
        var fichiers = new (string, DateTime?)[]
        {
            ("recente.jpg", Maintenant.AddDays(-1)),
            ("limite.jpg", Maintenant.AddDays(-30).AddMinutes(1)),
            ("vieille.jpg", Maintenant.AddDays(-31)),
            ("inconnue.jpg", null),  // dans le doute, on garde
        };
        Assert.Equal(["vieille.jpg"], PhotosIntrus.AEffacer(fichiers, Maintenant));
        Assert.Equal(30, PhotosIntrus.JoursDeConservation);
    }
}

public class IconesNotificationTests
{
    [Theory]
    [InlineData(false, false, "notification-libre-sombre.ico")]
    [InlineData(false, true, "notification-libre-clair.ico")]
    [InlineData(true, false, "notification-garde-sombre.ico")]
    [InlineData(true, true, "notification-garde-clair.ico")]
    public void DeuxEtatsDeuxThemes(bool garde, bool clair, string nom)
    {
        Assert.Equal(nom, IconesNotification.Nom(garde, clair));
        // Le fichier existe bien dans windows/ressources (il est embarqué dans l'exe par Hoverguard.csproj).
        var chemin = Path.Combine(Depot.Racine, "windows", "ressources", nom);
        Assert.True(File.Exists(chemin), chemin);
        var octets = File.ReadAllBytes(chemin);
        Assert.Equal(new byte[] { 0, 0, 1, 0, 4, 0 }, octets[..6]);  // .ico, 4 images (16, 20, 24, 32)
    }

    [Fact]
    public void InfoBulleCommeSurMac()
    {
        Assert.Equal("Hoverguard : garde posée", IconesNotification.InfoBulle(true, 3, 2));
        Assert.Equal("Hoverguard : Aucun agent au travail", IconesNotification.InfoBulle(false, 0, 0));
        Assert.Equal("Hoverguard : 3 agents au travail (1 principal + 2 sous-agents)", IconesNotification.InfoBulle(false, 1, 2));
        Assert.True(IconesNotification.InfoBulle(false, 100, 900).Length <= 63);
    }
}

/// <summary>Les textes du voile doivent rester ceux de <c>macos/Sources/Regles.swift</c> (enum <c>Textes</c>),
/// « Mac » devenant « PC ». Ce test lit le source Swift : toute divergence le fait échouer.</summary>
public class TextesAlignesSurMacTests
{
    static readonly string Swift = File.ReadAllText(Path.Combine(Depot.Racine, "macos", "Sources", "Regles.swift"));

    static string EnumTextes()
    {
        int debut = Swift.IndexOf("enum Textes", StringComparison.Ordinal);
        Assert.True(debut >= 0, "enum Textes introuvable dans Regles.swift");
        return Swift[debut..];
    }

    static List<string> Chaines(string bloc)
        => Regex.Matches(bloc, "\"((?:[^\"\\\\]|\\\\.)*)\"").Select(m => m.Groups[1].Value).ToList();

    /// <summary>Le texte entre <paramref name="ouverture"/> et la première <paramref name="fermeture"/> qui suit.</summary>
    static string Bloc(string source, string ouverture, string fermeture = "]")
    {
        int debut = source.IndexOf(ouverture, StringComparison.Ordinal);
        Assert.True(debut >= 0, ouverture);
        int fin = source.IndexOf(fermeture, debut + ouverture.Length, StringComparison.Ordinal);
        Assert.True(fin >= 0, fermeture);
        return source[debut..fin];
    }

    static string Pc(string mac) => mac.Replace("Mac", "PC");

    [Fact]
    public void PhrasesDuVoile()
    {
        var mac = Chaines(Bloc(EnumTextes(), "static let phrases = [")).Select(Pc).ToList();
        Assert.Equal(mac, Textes.Phrases);
    }

    [Fact]
    public void PhraseDEquipe()
    {
        var textes = EnumTextes();
        Assert.Contains($"\"{Textes.Equipe(0)}\"", textes);
        Assert.Contains($"\"{Textes.Equipe(1)}\"", textes);
        var variantes = Chaines(Bloc(textes, "let variantes = ["));
        Assert.Equal(4, variantes.Count);
        foreach (var n in new[] { 2, 3, 4, 5, 12 })
            Assert.Equal(variantes[n % variantes.Count].Replace("\\(total)", n.ToString()), Textes.Equipe(n));
    }

    [Fact]
    public void Compteur()
    {
        var textes = EnumTextes();
        Assert.Contains($"\"{Textes.Compteur(0, 0)}\"", textes);
        Assert.Equal("1 agent au travail", Textes.Compteur(1, 0));
        Assert.Equal("4 agents au travail", Textes.Compteur(4, 0));
        Assert.Equal("3 agents au travail (1 principal + 2 sous-agents)", Textes.Compteur(1, 2));
        Assert.Equal("3 agents au travail (2 principaux + 1 sous-agent)", Textes.Compteur(2, 1));
        // Mêmes morceaux que la version Swift.
        foreach (var morceau in new[] { "principaux", "principal", " sous-agent", " au travail" })
            Assert.Contains(morceau, textes);
    }

    [Fact]
    public void BandeauBatterie()
    {
        var mac = Chaines(Bloc(EnumTextes(), "static func batterie", "\n    }")).Where(c => c.Contains("batterie", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, mac.Count);
        Assert.Equal(Pc(mac[0]).Replace("\\(n)", "12"), Textes.Batterie(new EtatBatterie(12, false)));
        Assert.Equal(Pc(mac[0]).Replace("\\(n)", "20"), Textes.Batterie(new EtatBatterie(20, false)));
        Assert.Equal(Pc(mac[1]).Replace("\\(n)", "64"), Textes.Batterie(new EtatBatterie(64, false)));
        Assert.Null(Textes.Batterie(new EtatBatterie(64, true)));
        Assert.Null(Textes.Batterie(new EtatBatterie(null, false)));
    }
}

static class Depot
{
    /// <summary>Racine du dépôt : le premier dossier parent qui contient <c>macos</c> et <c>windows</c>.</summary>
    public static readonly string Racine = Trouver();

    static string Trouver()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "macos")) && Directory.Exists(Path.Combine(d.FullName, "windows")))
                return d.FullName;
        throw new DirectoryNotFoundException("Racine du dépôt introuvable");
    }
}
