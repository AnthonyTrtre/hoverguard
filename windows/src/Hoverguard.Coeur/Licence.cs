using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Hoverguard.Coeur;

public enum Offre { Fondateur, Pro, Equipe }

/// <summary>Licence Pro signée hors ligne (Ed25519), même format et même clé que la version macOS
/// (<c>macos/Sources/Licence.swift</c>) : l'app ne contient que la clé publique d'Utahpik et vérifie sans réseau.
/// Format : <c>HG1-&lt;données en base64url&gt;.&lt;signature en base64url&gt;</c> ;
/// données = <c>e-mail|offre|achat|fin des mises à jour</c> (dates ISO 8601 jour). Aucune activation en ligne,
/// aucune télémétrie.</summary>
/// <param name="FinMisesAJour">Les versions compilées après cette date ne s'ouvrent plus en Pro avec cette licence
/// (l'app déjà installée, si).</param>
public sealed record Licence(string Email, Offre Offre, DateTime Achat, DateTime FinMisesAJour)
{
    /// <summary>Clé publique d'Utahpik (32 octets bruts, base64). La clé privée ne quitte jamais Utahpik.</summary>
    public const string ClePubliqueUtahpik = "/KrNPU4ssMVCT7SCIFmjtM0oPeOuNFAxJK7AmBipmik=";

    public const string Prefixe = "HG1-";

    /// <summary>Lit et vérifie une licence ; <c>null</c> si elle est mal formée ou si la signature ne correspond pas.</summary>
    public static Licence? Lire(string? texte, string clePublique = ClePubliqueUtahpik)
    {
        var net = (texte ?? "").Trim();
        if (!net.StartsWith(Prefixe, StringComparison.Ordinal)) return null;
        var morceaux = net[Prefixe.Length..].Split('.');
        if (morceaux.Length != 2) return null;
        if (Base64Url.Decoder(morceaux[0]) is not byte[] donnees || Base64Url.Decoder(morceaux[1]) is not byte[] signature) return null;
        if (!SignatureValide(donnees, signature, clePublique)) return null;

        string contenu;
        try { contenu = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(donnees); }
        catch (DecoderFallbackException) { return null; }
        var champs = contenu.Split('|');
        if (champs.Length != 4) return null;
        if (LireOffre(champs[1]) is not Offre offre) return null;
        if (Jour(champs[2]) is not DateTime achat || Jour(champs[3]) is not DateTime fin) return null;
        return new Licence(champs[0], offre, achat, fin);
    }

    /// <summary>La licence couvre-t-elle une version de l'app compilée à cette date ? Le Fondateur couvre tout.</summary>
    public bool Couvre(DateTime dateDeVersion) => Offre == Offre.Fondateur || dateDeVersion <= FinMisesAJour;

    /// <summary>Date « aaaa-mm-jj » à minuit UTC, comme le <c>DateFormatter</c> du Mac.</summary>
    public static DateTime? Jour(string texte)
        => DateTime.TryParseExact(texte, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    /// <summary>Même orthographe que les valeurs brutes de l'énumération Swift.</summary>
    private static Offre? LireOffre(string texte) => texte switch
    {
        "fondateur" => Offre.Fondateur,
        "pro" => Offre.Pro,
        "equipe" => Offre.Equipe,
        _ => null,
    };

    private static bool SignatureValide(byte[] donnees, byte[] signature, string clePublique)
    {
        if (signature.Length != 64) return false;
        byte[] brute;
        try { brute = Convert.FromBase64String(clePublique); }
        catch (FormatException) { return false; }
        if (brute.Length != 32) return false;
        try
        {
            var verificateur = new Ed25519Signer();
            verificateur.Init(false, new Ed25519PublicKeyParameters(brute, 0));
            verificateur.BlockUpdate(donnees, 0, donnees.Length);
            return verificateur.VerifySignature(signature);
        }
        catch (ArgumentException) { return false; }  // clé publique invalide (point hors de la courbe)
    }
}

/// <summary>Ce que l'utilisateur a le droit d'utiliser, à un instant donné.</summary>
public abstract record EtatPro
{
    public sealed record Gratuit : EtatPro;
    /// <summary>Essai de 14 jours, déclenché à la première vraie garde (et non à l'installation).</summary>
    public sealed record Essai(int JoursRestants) : EtatPro;
    public sealed record Pro(Licence Licence) : EtatPro;
    /// <summary>Pro offert à vie aux 1 000 premiers (édition de lancement, « Fondateur »).</summary>
    public sealed record Lancement : EtatPro;

    public const int DureeEssai = 14;

    public bool Actif => this is not Gratuit;

    public static EtatPro Calculer(Licence? licence, DateTime? debutEssai, DateTime dateDeVersion, DateTime? maintenant = null)
    {
        if (licence is not null && licence.Couvre(dateDeVersion)) return new Pro(licence);
        if (debutEssai is not DateTime debut) return new Gratuit();
        // Jours entiers écoulés, comme Calendar.dateComponents([.day]) sur macOS.
        var ecoules = (int)Math.Floor(((maintenant ?? DateTime.UtcNow) - debut).TotalDays);
        var restants = DureeEssai - ecoules;
        return restants > 0 ? new Essai(Math.Min(restants, DureeEssai)) : new Gratuit();
    }

    /// <summary>Phrase du statut, affichée dans l'onglet « Pro ».</summary>
    public string Libelle => this switch
    {
        Essai e => $"Essai Pro : encore {e.JoursRestants} jour{(e.JoursRestants > 1 ? "s" : "")}",
        Pro p => $"Hoverguard Pro, licence de {p.Licence.Email}",
        Lancement => "Fondateur : Pro offert à vie, vous faites partie des 1 000 premiers 🎉",
        _ => "Hoverguard est gratuit et le restera : toute la protection est dans la version gratuite. Le Pro ajoute de quoi suivre vos agents et personnaliser la garde.",
    };
}

public static class Base64Url
{
    public static byte[]? Decoder(string texte)
    {
        if (texte.Length == 0) return [];
        if (texte.Contains('+') || texte.Contains('/') || texte.Contains('=')) return null;
        var b = texte.Replace('-', '+').Replace('_', '/');
        b += new string('=', (4 - b.Length % 4) % 4);
        var tampon = new byte[b.Length];
        return Convert.TryFromBase64String(b, tampon, out var n) ? tampon[..n] : null;
    }

    public static string Encoder(byte[] octets)
        => Convert.ToBase64String(octets).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}

/// <summary>Licence, essai et cadeau de lancement, rangés dans <c>%APPDATA%\Utahpik\Hoverguard\pro.json</c>
/// (à part des réglages : un réglage abîmé ne fait pas perdre la licence, et inversement). Aucun secret : la licence
/// n'est qu'une preuve d'achat signée.</summary>
public sealed class MemoirePro
{
    /// <summary>Texte de la licence tel que collé (vérifié à chaque lecture, jamais cru sur parole).</summary>
    [JsonPropertyName("licence")] public string? Licence { get; set; }
    [JsonPropertyName("debutEssai")] public DateTime? DebutEssai { get; set; }
    [JsonPropertyName("proLancement")] public bool ProLancement { get; set; }
    [JsonPropertyName("proLancementDate")] public DateTime? ProLancementDate { get; set; }

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Licence enregistrée, revérifiée.</summary>
    public Licence? LicenceValide => Coeur.Licence.Lire(Licence);

    public EtatPro Etat(DateTime dateDeVersion, DateTime? maintenant = null)
        => ProLancement ? new EtatPro.Lancement() : EtatPro.Calculer(LicenceValide, DebutEssai, dateDeVersion, maintenant);

    /// <summary>Démarre l'essai à la première vraie garde ; vrai si quelque chose a changé.</summary>
    public bool DemarrerEssaiSiPremiereGarde(DateTime? maintenant = null)
    {
        if (DebutEssai is not null) return false;
        DebutEssai = maintenant ?? DateTime.UtcNow;
        return true;
    }

    /// <summary>Édition de lancement : Pro offert à vie au premier lancement. Ceux qui l'ont reçu le gardent dans les
    /// versions suivantes, compilées sans ce cadeau. Vrai si quelque chose a changé.</summary>
    public bool AccorderProLancementSiEdition(bool editionLancement, DateTime? maintenant = null)
    {
        if (!editionLancement || ProLancement) return false;
        ProLancement = true;
        ProLancementDate = maintenant ?? DateTime.UtcNow;
        return true;
    }

    /// <summary>Vrai si la licence collée est valide (elle est alors gardée) ; sinon rien ne change.</summary>
    public bool Activer(string? texte)
    {
        if (Coeur.Licence.Lire(texte) is null) return false;
        Licence = texte!.Trim();
        return true;
    }

    /// <summary>Lit le fichier ; absent ou abîmé = état vierge (jamais d'exception).</summary>
    public static MemoirePro Charger(string chemin)
    {
        try
        {
            if (!File.Exists(chemin)) return new MemoirePro();
            return JsonSerializer.Deserialize<MemoirePro>(File.ReadAllText(chemin), Options) ?? new MemoirePro();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new MemoirePro();
        }
    }

    public void Enregistrer(string chemin)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(chemin)!);
        var temporaire = chemin + ".tmp";
        File.WriteAllText(temporaire, JsonSerializer.Serialize(this, Options), new UTF8Encoding(false));
        File.Move(temporaire, chemin, overwrite: true);
    }
}
