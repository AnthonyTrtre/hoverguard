import Foundation

/// Ce qui s'est passé pendant la garde, résumé au retour de l'utilisateur.
struct JournalAbsence: Equatable, Sendable {
    let debut: Date
    private(set) var reveils: [Date] = []
    private(set) var motsDePasseFaux = 0
    private(set) var agentsTermines: [String] = []
    private(set) var picSousAgents = 0
    private(set) var photos: [URL] = []

    init(debut: Date = .now) { self.debut = debut }

    mutating func reveil(_ date: Date = .now) { reveils.append(date) }
    mutating func motDePasseFaux() { motsDePasseFaux += 1 }
    mutating func photo(_ url: URL) { photos.append(url) }
    mutating func agentTermine(_ nom: String) { if !agentsTermines.contains(nom) { agentsTermines.append(nom) } }
    mutating func sousAgents(_ n: Int) { picSousAgents = max(picSousAgents, n) }

    /// Une photo de l'intrus à la 2e erreur, puis toutes les 3 erreurs (2, 5, 8…).
    var fautPhotographier: Bool { motsDePasseFaux >= 2 && (motsDePasseFaux - 2) % 3 == 0 }

    func rapport(fin: Date = .now) -> String {
        let secondes = fin.timeIntervalSince(debut)
        var lignes = ["Absence : \(secondes < 60 ? "moins d'une minute" : ReglesEveil.texteRestant(secondes))."]
        lignes.append(agentsTermines.isEmpty ? "Aucun agent n'a terminé pendant ce temps."
                                             : "Agents terminés : \(agentsTermines.joined(separator: ", ")).")
        if picSousAgents > 0 { lignes.append("Jusqu'à \(picSousAgents) sous-agents en même temps.") }
        if reveils.isEmpty {
            lignes.append("Personne n'a touché au Mac. 👍")
        } else {
            let heures = reveils.map { $0.formatted(date: .omitted, time: .shortened) }.joined(separator: ", ")
            lignes.append("Quelqu'un a touché au Mac \(reveils.count) fois (\(heures)).")
        }
        if motsDePasseFaux > 0 { lignes.append("Mots de passe faux : \(motsDePasseFaux).") }
        if !photos.isEmpty { lignes.append("Photos prises par la caméra : \(photos.count).") }
        return lignes.joined(separator: "\n")
    }
}

/// Événements qui peuvent partir en notification sur l'iPhone, avec un délai minimal entre deux envois du même type.
enum Alerte: String, Sendable, CaseIterable {
    case agentAttend, agentsFinis, intrus, batterieFaible

    static let intervalleMin: TimeInterval = 5 * 60

    func texte(_ detail: String) -> String {
        switch self {
        case .agentAttend: "🛸 Hoverguard : \(detail) attend ta réponse."
        case .agentsFinis: "🛸 Hoverguard : tous les agents ont fini. \(detail)"
        case .intrus: "🚨 Hoverguard : quelqu'un essaie d'entrer sur ton Mac (\(detail))."
        case .batterieFaible: "🪫 Hoverguard : \(detail) Branche le Mac, les agents vont s'arrêter."
        }
    }
}

struct LimiteurAlertes: Sendable {
    private var derniers: [Alerte: Date] = [:]

    /// `true` si l'alerte peut partir maintenant (et note l'envoi).
    mutating func autoriser(_ alerte: Alerte, a maintenant: Date = .now) -> Bool {
        if let d = derniers[alerte], maintenant.timeIntervalSince(d) < Alerte.intervalleMin { return false }
        derniers[alerte] = maintenant
        return true
    }
}
