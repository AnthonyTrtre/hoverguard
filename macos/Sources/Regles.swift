import Foundation
import SocleEnergie

enum EtatGarde: Equatable, Sendable {
    case libre
    /// Voile posé, clavier et souris physiques bloqués ; Claude et les autres agents passent.
    /// Touch ID est armé en silence : poser le doigt suffit, comme un Mac en veille.
    case garde
    /// Une touche ou un clic a « réveillé » la garde : la demande Touch ID / mot de passe est à l'écran.
    case authentification
}

/// Ce que le filtre d'entrées sait d'un événement clavier ou souris.
struct Entree: Equatable, Sendable {
    enum Genre: Equatable, Sendable { case touche, clic, autre }

    /// Processus qui a fabriqué l'événement (`kCGEventSourceUnixProcessID`) ; 0 pour le vrai clavier et la vraie souris.
    var pidSource: Int64
    /// `touche` = touche enfoncée, `clic` = bouton de souris enfoncé, `autre` = relâchements, mouvements, molette…
    var genre: Genre
    var estRaccourci = false
    var avecCommande = false

    var estPhysique: Bool { pidSource == 0 }
}

enum Decision: Equatable, Sendable {
    case laisser
    case bloquer
    /// ⌃⌘W hors garde : poser la garde.
    case poser
    /// Touche ou clic sous la garde : afficher la demande de mot de passe.
    case reveiller
    /// Pendant la demande : la touche est avalée et sert au mot de passe, quelle que soit l'app qui a le focus
    /// (sinon le mot de passe pourrait partir dans le Terminal de l'agent — audit C2).
    case saisir
}

/// Ce qu'une touche fait au mot de passe tapé sous le voile.
enum ActionSaisie: Equatable, Sendable {
    case caractere(String)
    case effacer
    case valider
    case annuler

    /// Codes de touche positionnels Apple : 51 = Effacement, 117 = Suppression, 36 / 76 = Entrée, 53 = Échap.
    static func depuis(codeTouche: Int64, caracteres: String?, avecCommande: Bool, avecControle: Bool) -> ActionSaisie? {
        switch codeTouche {
        case 51, 117: return .effacer
        case 36, 76: return .valider
        case 53: return .annuler
        default:
            guard !avecCommande, !avecControle, let c = caracteres, !c.isEmpty,
                  c.unicodeScalars.allSatisfy({ !CharacterSet.controlCharacters.contains($0) }) else { return nil }
            return .caractere(c)
        }
    }
}

enum Regles {
    /// Cœur de la garde. Ce que fabriquent les agents passe toujours ; seul le matériel est filtré.
    static func decider(_ e: Entree, etat: EtatGarde) -> Decision {
        guard e.estPhysique else { return .laisser }
        switch etat {
        case .libre:
            return e.estRaccourci && e.genre == .touche ? .poser : .laisser
        case .garde:
            return e.genre == .autre ? .bloquer : .reveiller
        case .authentification:
            // Rien du matériel n'atteint les apps : les touches nourrissent le mot de passe, le reste est avalé.
            return e.genre == .touche ? .saisir : .bloquer
        }
    }

    /// ⌃⌘W par défaut, d'après le caractère tapé (et non la position de la touche) pour marcher en AZERTY.
    static func estRaccourci(caracteres: String?, controle: Bool, commande: Bool, option: Bool, majuscule: Bool,
                             touche: String = "w") -> Bool {
        controle && commande && !option && !majuscule && caracteres?.lowercased() == touche
    }

    /// Nom d'agent reçu par URL : lettres, chiffres, espace, point, tiret, souligné ; 40 octets au plus (audit M4).
    static func nomAgentPropre(_ brut: String) -> String {
        let permis = CharacterSet.alphanumerics.union(CharacterSet(charactersIn: " ._-"))
        let filtre = String(String.UnicodeScalarView(brut.unicodeScalars.filter { $0.isASCII && permis.contains($0) }))
        let net = String(filtre.prefix(40)).trimmingCharacters(in: .whitespaces)
        return net.isEmpty ? "agent" : net
    }
}

/// Le drone vole en permanence sous la garde et se pose de temps en temps : 4 à 8 s toutes les 4 à 6 minutes.
enum ReglesVol {
    static let attente: ClosedRange<TimeInterval> = 240...360
    static let pause: ClosedRange<TimeInterval> = 4...8
}

enum ReglesGarde {
    /// Mots de passe faux : libre jusqu'à 5 erreurs, puis 3 s, 6 s, 9 s… plafonné à 2 min. Touch ID reste possible.
    static func attenteApres(echecs: Int) -> TimeInterval? {
        guard echecs >= 5 else { return nil }
        return min(Double(echecs - 4) * 3, 120)
    }
}

/// Ce que les agents (Claude Code, Codex…) signalent par `hoverguard://agent?nom=…&etat=…`.
/// Une session = un agent principal ; ses sous-agents sont comptés à part, car ça peut vite monter.
struct SuiviAgents: Equatable, Sendable {
    enum Etat: String, Sendable {
        case travail, attente, fini
        /// Un sous-agent démarre / s'arrête dans cette session.
        case sousAgentDebut = "sous-agent-debut", sousAgentFin = "sous-agent-fin"
    }

    struct Session: Equatable, Sendable {
        var etat: Etat
        var date: Date
        var sousAgents = 0
    }

    /// Au-delà, un agent qui n'a plus rien dit est considéré comme arrêté (il a pu planter sans prévenir).
    static let silenceMax: TimeInterval = 20 * 60

    private(set) var sessions: [String: Session] = [:]

    /// Au-delà, les sessions les plus anciennes sont oubliées (une app malveillante pourrait en inventer des milliers).
    static let maxSessions = 50

    mutating func purger(a maintenant: Date = .now) {
        sessions = sessions.filter { maintenant.timeIntervalSince($0.value.date) < Self.silenceMax }
    }

    mutating func signaler(_ agent: String, _ etat: Etat, date: Date = .now) {
        if sessions[agent] == nil, sessions.count >= Self.maxSessions {
            purger(a: date)
            if sessions.count >= Self.maxSessions, let plusVieille = sessions.min(by: { $0.value.date < $1.value.date }) {
                sessions[plusVieille.key] = nil
            }
        }
        var s = sessions[agent] ?? Session(etat: .travail, date: date)
        s.date = date
        switch etat {
        case .fini: sessions[agent] = nil; return
        case .travail, .attente: s.etat = etat
        case .sousAgentDebut: s.etat = .travail; s.sousAgents += 1
        case .sousAgentFin: s.sousAgents = max(0, s.sousAgents - 1)
        }
        sessions[agent] = s
    }

    private func vivantes(_ maintenant: Date) -> [String: Session] {
        sessions.filter { maintenant.timeIntervalSince($0.value.date) < Self.silenceMax }
    }

    func actifs(a maintenant: Date = .now) -> [String] {
        vivantes(maintenant).filter { $0.value.etat == .travail }.keys.sorted()
    }

    func enAttente(a maintenant: Date = .now) -> [String] {
        vivantes(maintenant).filter { $0.value.etat == .attente }.keys.sorted()
    }

    func sousAgents(a maintenant: Date = .now) -> Int {
        vivantes(maintenant).values.filter { $0.etat == .travail }.reduce(0) { $0 + $1.sousAgents }
    }

    /// Tout le monde : agents principaux qui travaillent + leurs sous-agents.
    func total(a maintenant: Date = .now) -> Int { actifs(a: maintenant).count + sousAgents(a: maintenant) }
}

/// Ordres reçus par URL.
enum Ordre: Equatable, Sendable {
    case agent(nom: String, etat: SuiviAgents.Etat)
    case verrouiller
    /// `hoverguard://licence?cle=HG1-…` : lien envoyé avec une licence (achat ou cadeau). Sans danger : une licence
    /// ne s'active que si sa signature Utahpik est valide.
    case licence(String)

    /// `hoverguard://agent?nom=claude&etat=travail`, `hoverguard://verrouiller`.
    /// Volontairement aucun ordre pour LEVER la garde : n'importe quelle page web ou app peut ouvrir une URL.
    init?(url: URL) {
        guard url.scheme == "hoverguard" else { return nil }
        let morceaux = URLComponents(url: url, resolvingAgainstBaseURL: false)
        let valeur = { (nom: String) in morceaux?.queryItems?.first { $0.name == nom }?.value }
        switch url.host() {
        case "agent":
            guard let etat = valeur("etat").flatMap(SuiviAgents.Etat.init(rawValue:)) else { return nil }
            self = .agent(nom: Regles.nomAgentPropre(valeur("nom") ?? ""), etat: etat)
        case "verrouiller": self = .verrouiller
        case "licence":
            guard let cle = valeur("cle"), cle.hasPrefix("HG1-"), cle.count < 2_000 else { return nil }
            self = .licence(cle)
        default: return nil
        }
    }
}

enum Textes {
    /// Phrases du voile, tirées au hasard à chaque garde (sauf si l'utilisateur a écrit la sienne).
    static let phrases = [
        "Ici, on bosse. Admirez avec les yeux seulement 😉",
        "Mac en pleine mission. Merci de repasser plus tard !",
        "Les IA travaillent, le drone veille. Les mains dans les poches, s'il vous plaît.",
        "Chut… des agents sont concentrés. Pas touche au clavier !",
        "Je travaille pour mon humain. Lui seul peut me réveiller 🐾",
        "Occupé à sauver le monde, une ligne de code à la fois. Revenez plus tard !",
        "Allez voir ailleurs si j'y suis… moi, je bosse 😄",
        "Merci de circuler, il n'y a rien à voir. Enfin, si : un drone.",
    ]

    /// L'équipe d'agents, avec humour et sans rabaisser personne.
    static func equipe(_ total: Int) -> String {
        switch total {
        case 0: return "L'équipe fait une pause café ☕"
        case 1: return "Un collègue numérique au travail, concentré comme jamais"
        default:
            let variantes = [
                "\(total) collègues numériques au travail, et aucun ne réclame de pause café",
                "Open space au complet : \(total) agents en réunion de production",
                "L'équipe tourne à plein : \(total) agents sur le pont",
                "\(total) agents au boulot, le manager est parti prendre l'air",
            ]
            return variantes[total % variantes.count]
        }
    }

    static func compteur(principaux: Int, sousAgents: Int) -> String {
        let total = principaux + sousAgents
        guard total > 0 else { return "Aucun agent au travail" }
        let detail = sousAgents == 0 ? "" : " (\(principaux) \(principaux > 1 ? "principaux" : "principal") + \(sousAgents) sous-agent\(sousAgents > 1 ? "s" : ""))"
        return "\(total) agent\(total > 1 ? "s" : "") au travail" + detail
    }

    /// Message du voile quand le Mac tourne sur batterie : la garde peut durer des heures.
    static func batterie(_ b: EtatBatterie) -> String? {
        guard !b.surSecteur, let n = b.niveau else { return nil }
        return n <= 20 ? "🪫 Plus que \(n) % de batterie ! Branchez-moi vite, sinon les agents s'arrêtent."
                       : "🔋 Sur batterie (\(n) %) : merci de me brancher, j'en ai pour un moment."
    }

    static func statutAgents(actifs: [String], enAttente: [String]) -> String {
        switch (actifs.count, enAttente.count) {
        case (0, 0): "Aucun agent en cours"
        case (0, _): "\(enAttente.joined(separator: ", ")) attend une réponse"
        case (1, _): "\(actifs[0]) travaille"
        default: "\(actifs.count) agents travaillent : \(actifs.joined(separator: ", "))"
        }
    }
}
