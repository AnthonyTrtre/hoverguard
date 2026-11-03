using System.Globalization;

namespace Hoverguard.Coeur;

public readonly record struct EtatBatterie(int? Niveau, bool SurSecteur);

/// <summary>Textes du voile, repris de la version macOS (<c>macos/Sources/Regles.swift</c>). Seul changement :
/// « Mac » devient « PC ».</summary>
public static class Textes
{
    /// <summary>Phrases du voile, tirées au hasard à chaque garde (sauf si l'utilisateur a écrit la sienne).</summary>
    public static readonly IReadOnlyList<string> Phrases =
    [
        "Ici, on bosse. Admirez avec les yeux seulement 😉",
        "PC en pleine mission. Merci de repasser plus tard !",
        "Les IA travaillent, le drone veille. Les mains dans les poches, s'il vous plaît.",
        "Chut… des agents sont concentrés. Pas touche au clavier !",
        "Je travaille pour mon humain. Lui seul peut me réveiller 🐾",
        "Occupé à sauver le monde, une ligne de code à la fois. Revenez plus tard !",
        "Allez voir ailleurs si j'y suis… moi, je bosse 😄",
        "Merci de circuler, il n'y a rien à voir. Enfin, si : un drone.",
    ];

    /// <summary>L'équipe d'agents, avec humour et sans rabaisser personne.</summary>
    public static string Equipe(int total)
    {
        switch (total)
        {
            case <= 0: return "L'équipe fait une pause café ☕";
            case 1: return "Un collègue numérique au travail, concentré comme jamais";
            default:
                string[] variantes =
                [
                    $"{total} collègues numériques au travail, et aucun ne réclame de pause café",
                    $"Open space au complet : {total} agents en réunion de production",
                    $"L'équipe tourne à plein : {total} agents sur le pont",
                    $"{total} agents au boulot, le manager est parti prendre l'air",
                ];
                return variantes[total % variantes.Length];
        }
    }

    public static string Compteur(int principaux, int sousAgents)
    {
        int total = principaux + sousAgents;
        if (total <= 0) return "Aucun agent au travail";
        var detail = sousAgents == 0 ? ""
            : $" ({principaux} {(principaux > 1 ? "principaux" : "principal")} + {sousAgents} sous-agent{(sousAgents > 1 ? "s" : "")})";
        return $"{total} agent{(total > 1 ? "s" : "")} au travail{detail}";
    }

    /// <summary>Message du voile quand le PC tourne sur batterie : la garde peut durer des heures.</summary>
    public static string? Batterie(EtatBatterie b)
    {
        if (b.SurSecteur || b.Niveau is not int n) return null;
        return n <= 20 ? $"🪫 Plus que {n} % de batterie ! Branchez-moi vite, sinon les agents s'arrêtent."
                       : $"🔋 Sur batterie ({n} %) : merci de me brancher, j'en ai pour un moment.";
    }

    public static string StatutAgents(IReadOnlyList<string> actifs, IReadOnlyList<string> enAttente)
        => (actifs.Count, enAttente.Count) switch
        {
            (0, 0) => "Aucun agent en cours",
            (0, _) => $"{string.Join(", ", enAttente)} attend une réponse",
            (1, _) => $"{actifs[0]} travaille",
            _ => $"{actifs.Count} agents travaillent : {string.Join(", ", actifs)}",
        };

    public static string Heure(DateTime locale) => locale.ToString("HH:mm", CultureInfo.GetCultureInfo("fr-FR"));
}
