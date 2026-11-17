import Darwin
import Foundation
import Security
import SocleCore

/// Prise en main à distance (TeamViewer) : ses touches et clics sont injectés avec son pid, comme ceux d'un agent.
/// Sans ce tri, ils passaient sous le voile et le mot de passe ne pouvait pas être tapé à distance. On ne les traite
/// comme le vrai matériel que si le processus est signé par l'éditeur de TeamViewer (vérifié par macOS) : un agent ou
/// un script local ne peut pas s'en faire passer, et une personne à distance se heurte à la même garde qu'au clavier.
@MainActor
enum Telecommande {
    /// Équipe Apple de TeamViewer GmbH.
    nonisolated static let exigence = "anchor apple generic and certificate leaf[subject.OU] = \"H7UGFBUGV6\""

    private static let requete: SecRequirement? = {
        var r: SecRequirement?
        return SecRequirementCreateWithString(exigence as CFString, [], &r) == errSecSuccess ? r : nil
    }()

    /// Clé : pid et instant de démarrage (un pid réutilisé par un autre processus ne garde pas le verdict).
    private struct Cle: Hashable { let pid: Int32; let debut: UInt64 }
    private static var verdicts: [Cle: Bool] = [:]

    /// Appelé par le filtre, sur le fil principal. La signature n'est vérifiée qu'une fois par processus, et seulement
    /// pour un exécutable dont le chemin évoque TeamViewer : les agents ne paient qu'une lecture de chemin.
    static func estTeleassistance(pid: Int64) -> Bool {
        guard pid > 0, pid <= Int64(Int32.max) else { return false }
        let p = Int32(pid)
        var info = proc_bsdinfo()
        let taille = Int32(MemoryLayout<proc_bsdinfo>.size)
        guard proc_pidinfo(p, PROC_PIDTBSDINFO, 0, &info, taille) == taille else { return false }
        let cle = Cle(pid: p, debut: info.pbi_start_tvsec &* 1_000_000 &+ info.pbi_start_tvusec)
        if let v = verdicts[cle] { return v }
        let v = cheminTeamViewer(p) && signe(p)
        if verdicts.count > 200 { verdicts.removeAll() }
        verdicts[cle] = v
        if v { Journal.app.notice("Prise en main à distance reconnue (TeamViewer) : soumise à la garde") }
        return v
    }

    private static func cheminTeamViewer(_ pid: Int32) -> Bool {
        var tampon = [CChar](repeating: 0, count: Int(MAXPATHLEN) * 4)
        guard proc_pidpath(pid, &tampon, UInt32(tampon.count)) > 0 else { return false }
        return String(cString: tampon).localizedCaseInsensitiveContains("teamviewer")
    }

    private static func signe(_ pid: Int32) -> Bool {
        guard let requete else { return false }
        var code: SecCode?
        guard SecCodeCopyGuestWithAttributes(nil, [kSecGuestAttributePid: pid] as CFDictionary, [], &code) == errSecSuccess,
              let code else { return false }
        return SecCodeCheckValidity(code, [], requete) == errSecSuccess
    }
}
