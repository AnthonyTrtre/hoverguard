using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

namespace Hoverguard.Tests;

/// <summary>Mêmes cas que <c>LicenceTests</c> de la version macOS, avec la même licence réelle.</summary>
public class LicenceTests
{
    /// <summary>Licence réelle signée par la clé privée d'Utahpik (script privé), pour un e-mail d'essai.
    /// Copiée telle quelle de <c>macos/Tests/HoverguardTests.swift</c>.</summary>
    const string Vraie = "HG1-ZXNzYWlAZXhlbXBsZS5mcnxwcm98MjAyNi0xMS0wM3wyMDI3LTExLTAz.SNV8NntuHm9fb5myi5UZbD2N1SuTBBwve4jg-6eJ79Y5GocmcZ63iIe1pQtoLiVmdBennTB87Wh8KuyZZbfmBA";

    static DateTime Jour(string t) => Licence.Jour(t) ?? throw new FormatException(t);

    [Fact]
    public void LicenceSigneeAccepteeHorsLigne()
    {
        var l = Licence.Lire(Vraie);
        Assert.NotNull(l);
        Assert.Equal("essai@exemple.fr", l.Email);
        Assert.Equal(Offre.Pro, l.Offre);
        Assert.Equal(Jour("2026-11-03"), l.Achat);
        Assert.Equal(Jour("2027-11-03"), l.FinMisesAJour);
        // Espaces et retour à la ligne autour, comme après un copier-coller.
        Assert.NotNull(Licence.Lire("  " + Vraie + "\r\n"));
    }

    [Fact]
    public void LicenceModifieeOuInventeeRefusee()
    {
        // Changer une lettre des données casse la signature : impossible de fabriquer une licence sans la clé privée.
        var morceaux = Vraie.ToCharArray();
        morceaux[8] = morceaux[8] == 'A' ? 'B' : 'A';
        Assert.Null(Licence.Lire(new string(morceaux)));
        // Une lettre de la signature aussi.
        var signature = Vraie.ToCharArray();
        signature[^3] = signature[^3] == 'A' ? 'B' : 'A';
        Assert.Null(Licence.Lire(new string(signature)));
        Assert.Null(Licence.Lire("HG1-abc.def"));
        Assert.Null(Licence.Lire("n'importe quoi"));
        Assert.Null(Licence.Lire(""));
        Assert.Null(Licence.Lire(null));
        Assert.Null(Licence.Lire(Vraie + ".x"));
    }

    [Fact]
    public void AutreCleRefusee()
    {
        var generateur = new Ed25519KeyPairGenerator();
        generateur.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var autre = (Ed25519PublicKeyParameters)generateur.GenerateKeyPair().Public;
        Assert.Null(Licence.Lire(Vraie, Convert.ToBase64String(autre.GetEncoded())));
        Assert.Null(Licence.Lire(Vraie, "pas une clé"));
        Assert.NotNull(Licence.Lire(Vraie, Licence.ClePubliqueUtahpik));
    }

    [Fact]
    public void EssaiDe14JoursPuisGratuit()
    {
        var t0 = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000).UtcDateTime;
        var version = t0;
        Assert.Equal(new EtatPro.Gratuit(), EtatPro.Calculer(null, null, version));
        Assert.Equal(new EtatPro.Essai(14), EtatPro.Calculer(null, t0, version, t0));
        Assert.Equal(new EtatPro.Essai(1), EtatPro.Calculer(null, t0, version, t0.AddDays(13.5)));
        Assert.Equal(new EtatPro.Gratuit(), EtatPro.Calculer(null, t0, version, t0.AddDays(14)));
        Assert.Equal(new EtatPro.Gratuit(), EtatPro.Calculer(null, t0, version, t0.AddDays(15)));
        Assert.False(new EtatPro.Gratuit().Actif);
        Assert.True(new EtatPro.Essai(3).Actif);
    }

    [Fact]
    public void EssaiDemarreALaPremiereGardeSeulement()
    {
        var t0 = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        var m = new MemoirePro();
        Assert.Equal(new EtatPro.Gratuit(), m.Etat(t0, t0));
        Assert.True(m.DemarrerEssaiSiPremiereGarde(t0));
        Assert.False(m.DemarrerEssaiSiPremiereGarde(t0.AddDays(5)));  // une deuxième garde ne relance pas l'essai
        Assert.Equal(new EtatPro.Essai(9), m.Etat(t0, t0.AddDays(5)));
    }

    [Fact]
    public void MisesAJourCouvertesJusquALaFinSaufFondateur()
    {
        var l = Licence.Lire(Vraie)!;
        Assert.True(l.Couvre(Jour("2027-06-01")));
        Assert.True(l.Couvre(Jour("2027-11-03")));
        Assert.False(l.Couvre(Jour("2028-01-01")));
        var fondateur = new Licence("a@b.c", Offre.Fondateur, DateTime.UtcNow, DateTime.MinValue);
        Assert.True(fondateur.Couvre(DateTime.MaxValue));

        // Version trop récente pour la licence : retour à l'essai ou au gratuit.
        Assert.Equal(new EtatPro.Pro(l), EtatPro.Calculer(l, null, Jour("2027-06-01")));
        Assert.Equal(new EtatPro.Gratuit(), EtatPro.Calculer(l, null, Jour("2028-01-01")));
    }

    [Fact]
    public void LienDActivation()
    {
        Assert.Equal(new Ordre.ActiverLicence(Vraie), Ordre.Lire($"hoverguard://licence?cle={Vraie}"));
        Assert.Equal(new Ordre.ActiverLicence(Vraie), Ordre.Lire($"hoverguard://licence/?cle={Uri.EscapeDataString(Vraie)}"));
        Assert.Null(Ordre.Lire("hoverguard://licence?cle=autre"));
        Assert.Null(Ordre.Lire("hoverguard://licence"));
        Assert.Null(Ordre.Lire("hoverguard://licence?cle=HG1-" + new string('a', 2000)));
    }

    [Fact]
    public void ActivationEtEditionDeLancement()
    {
        var dossier = Path.Combine(Path.GetTempPath(), "hoverguard-tests-" + Guid.NewGuid().ToString("N"));
        var chemin = Path.Combine(dossier, "pro.json");
        try
        {
            var m = new MemoirePro();
            Assert.False(m.Activer("HG1-abc.def"));
            Assert.Null(m.Licence);
            Assert.True(m.Activer(" " + Vraie + "\n"));
            Assert.Equal(Vraie, m.Licence);

            Assert.False(m.AccorderProLancementSiEdition(editionLancement: false));
            Assert.True(m.AccorderProLancementSiEdition(editionLancement: true));
            Assert.False(m.AccorderProLancementSiEdition(editionLancement: true));
            m.Enregistrer(chemin);

            // Relu par une version compilée sans l'édition de lancement : le cadeau reste.
            var relue = MemoirePro.Charger(chemin);
            Assert.Equal(new EtatPro.Lancement(), relue.Etat(DateTime.UtcNow));
            Assert.Equal("essai@exemple.fr", relue.LicenceValide?.Email);

            // Licence trafiquée dans le fichier : revérifiée à la lecture, donc ignorée.
            File.WriteAllText(chemin, """{ "licence": "HG1-ZXNzYWlAZXhlbXBsZS5mcnxmb25kYXRldXJ8MjAyNi0xMS0wM3wyMDI3LTExLTAz.AAAA" }""");
            Assert.Null(MemoirePro.Charger(chemin).LicenceValide);
            File.WriteAllText(chemin, "{ abîmé");
            Assert.Null(MemoirePro.Charger(chemin).Licence);
        }
        finally
        {
            if (Directory.Exists(dossier)) Directory.Delete(dossier, recursive: true);
        }
    }
}
