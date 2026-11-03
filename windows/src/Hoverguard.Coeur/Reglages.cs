using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hoverguard.Coeur;

/// <summary>Réglages de l'utilisateur, mêmes noms que les clés UserDefaults de la version macOS. Stockés en JSON dans
/// <c>%APPDATA%\Utahpik\Hoverguard\reglages.json</c>. Aucun mot de passe ni secret n'y figure.</summary>
public sealed class Reglages
{
    /// <summary>Phrase choisie par l'utilisateur ; vide = une phrase drôle au hasard à chaque garde.</summary>
    [JsonPropertyName("phrase")] public string Phrase { get; set; } = "";

    /// <summary>Photo par la caméra intégrée après 2 mots de passe faux. Désactivée par défaut, comme sur macOS :
    /// photographier quelqu'un peut être encadré par la loi (droit à l'image, audit M8). Photos effacées après 30 jours.</summary>
    [JsonPropertyName("photoIntrus")] public bool PhotoIntrus { get; set; }

    /// <summary>Poser la garde toute seule après N minutes sans toucher au PC, si des agents travaillent (0 = jamais).</summary>
    [JsonPropertyName("gardeAutoMinutes")] public int GardeAutoMinutes { get; set; } = 15;

    /// <summary>Ouvrir le rapport de la garde tout seul au déverrouillage. Désactivé par défaut, comme sur macOS : le
    /// rapport attend dans le menu (« Rapport de la dernière garde… »). Même clé que la version macOS.</summary>
    /// <summary>Garde automatique même sans agent au travail (même clé que la version macOS). Activée par défaut.</summary>
    [JsonPropertyName("gardeAutoSansAgent")] public bool GardeAutoSansAgent { get; set; } = true;

    [JsonPropertyName("rapportAuRetour")] public bool RapportAuRetour { get; set; }

    [JsonPropertyName("ecranAllume")] public bool EcranAllume { get; set; }

    /// <summary>Seuil d'arrêt de l'éveil sur batterie en %, 0 = jamais.</summary>
    [JsonPropertyName("seuilBatterie")] public int SeuilBatterie { get; set; } = 20;

    [JsonPropertyName("dureeParDefaut")] public Duree DureeParDefaut { get; set; } = Duree.Indefinie;

    /// <summary>Notifications sur le téléphone : emplacement réservé, toujours désactivé sous Windows (pas d'iMessage).
    /// TODO : brancher un canal local respectant « aucune connexion réseau » (par ex. l'app « Lien avec Windows »).</summary>
    [JsonPropertyName("notifications")] public bool Notifications { get; set; }

    /// <summary>Mises à jour automatiques depuis les Releases GitHub (même clé que la version macOS / Sparkle).</summary>
    [JsonPropertyName("SUEnableAutomaticChecks")] public bool MisesAJourAuto { get; set; } = true;

    /// <summary>Le premier lancement a déjà inscrit l'ouverture au démarrage de session.</summary>
    [JsonPropertyName("premierLancementFait")] public bool PremierLancementFait { get; set; }

    public static readonly IReadOnlyList<int> ChoixGardeAuto = [0, 5, 10, 15, 30];
    public static readonly IReadOnlyList<int> ChoixSeuilBatterie = [0, 10, 20, 30, 50];

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Lit les réglages ; un fichier absent ou abîmé donne les valeurs par défaut (jamais d'exception).</summary>
    public static Reglages Charger(string chemin)
    {
        try
        {
            if (!File.Exists(chemin)) return new Reglages();
            var r = JsonSerializer.Deserialize<Reglages>(File.ReadAllText(chemin), Options) ?? new Reglages();
            r.Normaliser();
            return r;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new Reglages();
        }
    }

    public void Enregistrer(string chemin)
    {
        Normaliser();
        Directory.CreateDirectory(Path.GetDirectoryName(chemin)!);
        var temporaire = chemin + ".tmp";
        File.WriteAllText(temporaire, JsonSerializer.Serialize(this, Options), new UTF8Encoding(false));
        File.Move(temporaire, chemin, overwrite: true);
    }

    private void Normaliser()
    {
        Phrase = (Phrase ?? "").Trim();
        if (Phrase.Length > 200) Phrase = Phrase[..200];
        GardeAutoMinutes = Math.Clamp(GardeAutoMinutes, 0, 24 * 60);
        SeuilBatterie = Math.Clamp(SeuilBatterie, 0, 100);
        if (!Enum.IsDefined(DureeParDefaut)) DureeParDefaut = Duree.Indefinie;
        Notifications = false;  // pas de canal sous Windows pour l'instant
    }
}

/// <summary>Garde automatique : basée sur la dernière entrée PHYSIQUE vue par le filtre, pas sur
/// <c>GetLastInputInfo</c>, qui compte aussi les clics et frappes injectés par les agents (avec lui, un agent actif
/// empêcherait toute garde automatique).</summary>
public static class ReglesGardeAuto
{
    /// <param name="memeSansAgent">Poser aussi quand aucun agent ne travaille (comme un économiseur d'écran).</param>
    public static bool DoitPoser(EtatGarde etat, int minutes, int agentsActifs, TimeSpan inactivitePhysique, bool memeSansAgent = false)
        => etat == EtatGarde.Libre && minutes > 0 && (agentsActifs > 0 || memeSansAgent) && inactivitePhysique > TimeSpan.FromMinutes(minutes);
}
