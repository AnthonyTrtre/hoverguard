import CryptoKit
import Foundation

/// Licence Pro signée hors ligne (Ed25519) : l'app ne contient que la clé publique d'Utahpik et vérifie sans réseau.
/// Format : `HG1-<données en base64url>.<signature en base64url>` ; données = `e-mail|offre|achat|fin des mises à jour`
/// (dates ISO 8601 jour). Aucune activation en ligne, aucune télémétrie.
struct Licence: Equatable, Sendable {
    enum Offre: String, Sendable { case fondateur, pro, equipe }

    let email: String
    let offre: Offre
    let achat: Date
    /// Les versions publiées après cette date ne s'ouvrent plus en Pro avec cette licence (l'app déjà installée, si).
    let finMisesAJour: Date

    /// Clé publique d'Utahpik (base64). La clé privée ne quitte jamais le Mac d'Utahpik.
    static let clePubliqueUtahpik = "/KrNPU4ssMVCT7SCIFmjtM0oPeOuNFAxJK7AmBipmik="

    static func lire(_ texte: String, clePublique: String = clePubliqueUtahpik) -> Licence? {
        let net = texte.trimmingCharacters(in: .whitespacesAndNewlines)
        guard net.hasPrefix("HG1-") else { return nil }
        let morceaux = net.dropFirst(4).split(separator: ".", omittingEmptySubsequences: false)
        guard morceaux.count == 2,
              let donnees = Data(base64URL: String(morceaux[0])), let signature = Data(base64URL: String(morceaux[1])),
              let brute = Data(base64Encoded: clePublique),
              let cle = try? Curve25519.Signing.PublicKey(rawRepresentation: brute),
              cle.isValidSignature(signature, for: donnees),
              let champs = String(data: donnees, encoding: .utf8)?.split(separator: "|", omittingEmptySubsequences: false),
              champs.count == 4, let offre = Offre(rawValue: String(champs[1])),
              let achat = jour(String(champs[2])), let fin = jour(String(champs[3])) else { return nil }
        return Licence(email: String(champs[0]), offre: offre, achat: achat, finMisesAJour: fin)
    }

    /// La licence couvre-t-elle une version de l'app compilée à cette date ?
    func couvre(dateDeVersion: Date) -> Bool { offre == .fondateur || dateDeVersion <= finMisesAJour }

    static func jour(_ texte: String) -> Date? {
        let f = DateFormatter()
        f.calendar = Calendar(identifier: .iso8601)
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = TimeZone(identifier: "UTC")
        f.dateFormat = "yyyy-MM-dd"
        return f.date(from: texte)
    }
}

/// Ce que l'utilisateur a droit d'utiliser, à un instant donné.
enum EtatPro: Equatable, Sendable {
    case gratuit
    /// Essai de 14 jours, déclenché à la première vraie garde (et non à l'installation).
    case essai(joursRestants: Int)
    case pro(Licence)
    /// Pro offert à vie aux 1 000 premiers (édition de lancement).
    case lancement

    static let dureeEssai = 14

    var actif: Bool {
        switch self {
        case .gratuit: false
        case .essai, .pro, .lancement: true
        }
    }

    static func calculer(licence: Licence?, debutEssai: Date?, dateDeVersion: Date, maintenant: Date = .now) -> EtatPro {
        if let licence, licence.couvre(dateDeVersion: dateDeVersion) { return .pro(licence) }
        guard let debutEssai else { return .gratuit }
        let ecoules = Calendar.current.dateComponents([.day], from: debutEssai, to: maintenant).day ?? 0
        let restants = dureeEssai - ecoules
        return restants > 0 ? .essai(joursRestants: restants) : .gratuit
    }
}

extension Data {
    init?(base64URL texte: String) {
        var b = texte.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        b += String(repeating: "=", count: (4 - b.count % 4) % 4)
        self.init(base64Encoded: b)
    }

    var base64URL: String {
        base64EncodedString().replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }
}

/// Licence et essai rangés dans les réglages. L'essai démarre à la première vraie garde.
@MainActor
enum Pro {
    /// Pages de vente et de soutien : vides tant que la boutique n'est pas ouverte (les boutons restent cachés).
    static let urlAchat: URL? = nil
    static let urlSoutien: URL? = nil
    /// Édition de lancement : la première version publiée (compilée avec `EDITION_LANCEMENT`) offre le Pro à vie, tout
    /// seul, au premier lancement. Quand les 1 000 premiers téléchargements sont atteints, la version suivante est
    /// publiée sans ce cadeau ; ceux qui l'ont reçu le gardent. Aucune connexion, rien à valider à la main.
    #if EDITION_LANCEMENT
    static let editionLancement = true
    #else
    static let editionLancement = false
    #endif

    static var proLancement: Bool { UserDefaults.standard.bool(forKey: "proLancement") }

    static func accorderProLancementSiEdition() {
        guard editionLancement, !proLancement else { return }
        UserDefaults.standard.set(true, forKey: "proLancement")
        UserDefaults.standard.set(Date.now, forKey: "proLancementDate")
    }

    static var licence: Licence? { UserDefaults.standard.string(forKey: "licence").flatMap { Licence.lire($0) } }

    static var etat: EtatPro {
        if proLancement { return .lancement }
        let version = Bundle.main.executableURL.flatMap { try? $0.resourceValues(forKeys: [.contentModificationDateKey]) }?
            .contentModificationDate ?? .now
        return EtatPro.calculer(licence: licence, debutEssai: UserDefaults.standard.object(forKey: "debutEssai") as? Date,
                                dateDeVersion: version)
    }

    static func demarrerEssaiSiPremiereGarde() {
        if UserDefaults.standard.object(forKey: "debutEssai") == nil { UserDefaults.standard.set(Date.now, forKey: "debutEssai") }
    }

    /// `true` si la licence collée est valide (elle est alors enregistrée).
    static func activer(_ texte: String) -> Bool {
        guard Licence.lire(texte) != nil else { return false }
        UserDefaults.standard.set(texte.trimmingCharacters(in: .whitespacesAndNewlines), forKey: "licence")
        return true
    }
}
