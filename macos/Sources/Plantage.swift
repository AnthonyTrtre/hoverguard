import AppKit
import Foundation

/// Rapport de plantage, sans serveur ni envoi automatique : au lancement suivant, Hoverguard propose d'ouvrir une issue
/// GitHub pré-remplie et anonymisée. Seul le clic de l'utilisateur ouvre le navigateur.
enum RapportPlantage {
    static let tailleMaxURL = 6000
    static let tronque = "(rapport tronqué, joindre le fichier)"
    static let adresseIssues = "https://github.com/AnthonyTrtre/hoverguard/issues/new"

    /// Le rapport de Hoverguard le plus récent, s'il est plus récent que le dernier déjà proposé.
    static func nouveau(_ fichiers: [(nom: String, date: Date)], dernierVu: Date) -> (nom: String, date: Date)? {
        fichiers.filter { $0.nom.hasPrefix("Hoverguard") && $0.nom.hasSuffix(".ips") && $0.date > dernierVu }
            .max { $0.date < $1.date }
    }

    /// L'essentiel d'un rapport `.ips` (une ligne d'en-tête JSON, puis le corps JSON) : version, exception, fin du
    /// processus, et les 15 premières lignes du fil fautif.
    static func extrait(ips: String) -> String {
        let morceaux = ips.split(separator: "\n", maxSplits: 1)
        guard morceaux.count == 2,
              let corps = (try? JSONSerialization.jsonObject(with: Data(morceaux[1].utf8))) as? [String: Any]
        else { return String(ips.prefix(1500)) }
        func texte(_ d: [String: Any], _ cles: [String]) -> String { cles.compactMap { d[$0] as? String }.joined(separator: " ") }
        var lignes: [String] = []
        if let entete = (try? JSONSerialization.jsonObject(with: Data(morceaux[0].utf8))) as? [String: Any],
           let v = entete["app_version"] as? String {
            lignes.append("Version au plantage : \(v)")
        }
        if let e = corps["exception"] as? [String: Any] { lignes.append("Exception : " + texte(e, ["type", "signal", "subtype", "codes"])) }
        if let t = corps["termination"] as? [String: Any] {
            lignes.append("Fin : " + texte(t, ["namespace", "indicator"]))
            lignes += (t["reasons"] as? [String] ?? []).prefix(3).map { "  " + $0.prefix(300) }
        }
        if let asi = corps["asi"] as? [String: [String]] {
            lignes += asi.values.flatMap { $0 }.prefix(3).map { "Message : " + $0.prefix(300) }
        }
        let images = (corps["usedImages"] as? [[String: Any]] ?? []).map { $0["name"] as? String ?? "?" }
        let indice = corps["faultingThread"] as? Int ?? 0
        if let fils = corps["threads"] as? [[String: Any]], fils.indices.contains(indice) {
            let file = (fils[indice]["queue"] as? String).map { " (\($0))" } ?? ""
            lignes.append("Fil fautif \(indice)\(file) :")
            for (n, f) in (fils[indice]["frames"] as? [[String: Any]] ?? []).prefix(15).enumerated() {
                let image = (f["imageIndex"] as? Int).flatMap { images.indices.contains($0) ? images[$0] : nil } ?? "?"
                let symbole = (f["symbol"] as? String).map { "\($0) + \(f["symbolLocation"] as? Int ?? 0)" }
                    ?? "+ \(f["imageOffset"] as? Int ?? 0)"
                lignes.append("\(n)  \(image)  \(symbole)")
            }
        }
        return lignes.joined(separator: "\n")
    }

    /// Retire ce qui identifie la personne : dossier personnel → `~`, noms de machine → `<machine>`, nom d'utilisateur
    /// → `<utilisateur>`. Les noms de moins de 3 caractères sont laissés (ils abîmeraient tout le texte).
    static func anonymiser(_ texte: String, dossierPersonnel: String, utilisateurs: [String], machines: [String]) -> String {
        var t = texte
        if dossierPersonnel.count > 1 { t = t.replacingOccurrences(of: dossierPersonnel, with: "~") }
        for m in machines.sorted(by: { $0.count > $1.count }) where m.count >= 3 {
            t = t.replacingOccurrences(of: m, with: "<machine>", options: .caseInsensitive)
        }
        for u in utilisateurs.sorted(by: { $0.count > $1.count }) where u.count >= 3 {
            t = t.replacingOccurrences(of: u, with: "<utilisateur>", options: .caseInsensitive)
        }
        return t
    }

    /// Adresse de la nouvelle issue, bornée à `tailleMaxURL` caractères : au-delà, l'extrait est tronqué.
    static func url(version: String, systeme: String, architecture: String, extrait: String) -> URL {
        func adresse(_ e: String) -> String {
            let corps = "**Version** : \(version)\n**Système** : \(systeme)\n**Architecture** : \(architecture)\n\n```\n\(e)\n```\n"
            return "\(adresseIssues)?title=\(encoder("Plantage de Hoverguard \(version)"))&body=\(encoder(corps))&labels=plantage"
        }
        var e = extrait
        if adresse(e).count > tailleMaxURL {
            repeat { e = String(e.dropLast(100)) } while !e.isEmpty && adresse(e + "\n" + tronque).count > tailleMaxURL
            e += "\n" + tronque
        }
        return URL(string: adresse(e))!
    }

    private static let permis = CharacterSet(charactersIn: "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~")
    private static func encoder(_ s: String) -> String { s.addingPercentEncoding(withAllowedCharacters: permis) ?? "" }
}

/// Côté Mac : cherche les rapports de macOS (`~/Library/Logs/DiagnosticReports/Hoverguard*.ips`) et pose la question.
@MainActor
enum SignalementPlantage {
    private static let cle = "dernierPlantageVu"
    private static var dossier: URL {
        FileManager.default.homeDirectoryForCurrentUser.appending(path: "Library/Logs/DiagnosticReports")
    }

    /// Un rapport apparu depuis le dernier proposé. Au premier lancement, les rapports déjà là sont ignorés.
    static func chercher() -> URL? {
        guard let dernier = UserDefaults.standard.object(forKey: cle) as? Date else {
            UserDefaults.standard.set(Date(), forKey: cle)
            return nil
        }
        let urls = (try? FileManager.default.contentsOfDirectory(at: dossier, includingPropertiesForKeys: [.contentModificationDateKey])) ?? []
        let fichiers = urls.compactMap { u in date(u).map { (nom: u.lastPathComponent, date: $0) } }
        return RapportPlantage.nouveau(fichiers, dernierVu: dernier).map { dossier.appending(path: $0.nom) }
    }

    static func proposer(_ fichier: URL) {
        UserDefaults.standard.set(date(fichier) ?? Date(), forKey: cle)
        let alerte = NSAlert()
        alerte.messageText = "Hoverguard s'est fermé de façon inattendue. Envoyer le rapport ?"
        alerte.informativeText = "« Signaler sur GitHub » ouvre une issue pré-remplie dans votre navigateur, sans votre nom ni celui du Mac. Vous la relisez avant de l'envoyer : rien ne part sans votre clic."
        alerte.addButton(withTitle: "Signaler sur GitHub")
        alerte.addButton(withTitle: "Afficher le rapport")
        alerte.addButton(withTitle: "Ignorer")
        NSApp.activate()
        switch alerte.runModal() {
        case .alertFirstButtonReturn: NSWorkspace.shared.open(url(fichier))
        case .alertSecondButtonReturn: NSWorkspace.shared.activateFileViewerSelecting([fichier])
        default: break
        }
    }

    private static func url(_ fichier: URL) -> URL {
        let brut = (try? String(contentsOf: fichier, encoding: .utf8)) ?? ""
        let machines = [Host.current().localizedName, ProcessInfo.processInfo.hostName,
                        ProcessInfo.processInfo.hostName.components(separatedBy: ".").first].compactMap { $0 }
        let extrait = RapportPlantage.anonymiser(RapportPlantage.extrait(ips: brut), dossierPersonnel: NSHomeDirectory(),
                                                 utilisateurs: [NSUserName(), NSFullUserName()], machines: machines)
        #if arch(arm64)
        let architecture = "arm64 (Apple Silicon)"
        #else
        let architecture = "x86_64 (Intel)"
        #endif
        let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "?"
        return RapportPlantage.url(version: version, systeme: "macOS \(ProcessInfo.processInfo.operatingSystemVersionString)",
                                   architecture: architecture, extrait: extrait)
    }

    private static func date(_ u: URL) -> Date? { try? u.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate }
}
