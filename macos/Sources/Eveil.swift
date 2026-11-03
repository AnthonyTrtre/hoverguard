import Foundation
import SocleEnergie

/// Durées proposées dans le menu. `indefinie` = jusqu'à ce qu'on arrête.
enum Duree: Int, CaseIterable, Sendable {
    case quinzeMinutes = 900, trenteMinutes = 1800, uneHeure = 3600, deuxHeures = 7200, quatreHeures = 14400
    case indefinie = 0

    var secondes: TimeInterval? { self == .indefinie ? nil : TimeInterval(rawValue) }

    var libelle: String {
        switch self {
        case .quinzeMinutes: "15 minutes"
        case .trenteMinutes: "30 minutes"
        case .uneHeure: "1 heure"
        case .deuxHeures: "2 heures"
        case .quatreHeures: "4 heures"
        case .indefinie: "Jusqu'à l'arrêt"
        }
    }
}

/// Une période pendant laquelle le Mac reste éveillé.
struct Session: Equatable, Sendable {
    let duree: Duree
    let fin: Date?

    init(duree: Duree, debut: Date = .now) {
        self.duree = duree
        fin = duree.secondes.map { debut.addingTimeInterval($0) }
    }

    /// Temps restant, `nil` pour une session sans fin.
    func restant(a maintenant: Date) -> TimeInterval? {
        fin.map { max(0, $0.timeIntervalSince(maintenant)) }
    }

    func estExpiree(a maintenant: Date) -> Bool { restant(a: maintenant) == 0 }
}

/// Règles de l'éveil (ex-Veilleur, fusionnée dans Hoverguard le 2026-11-03).
enum ReglesEveil {
    /// Arrêt automatique : seulement sur batterie, à `seuil` % ou moins. `seuil` = 0 désactive la règle.
    static func doitArreter(_ batterie: EtatBatterie, seuil: Int) -> Bool {
        guard seuil > 0, !batterie.surSecteur, let niveau = batterie.niveau else { return false }
        return niveau <= seuil
    }

    /// « 1 h 05 », « 12 min ». Arrondi à la minute supérieure pour ne jamais annoncer 0 min tant que ça tourne.
    static func texteRestant(_ secondes: TimeInterval) -> String {
        let minutes = Int((secondes / 60).rounded(.up))
        guard minutes >= 60 else { return "\(minutes) min" }
        return String(format: "%d h %02d", minutes / 60, minutes % 60)
    }
}
