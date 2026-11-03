import Foundation
import OpenDirectory

/// Vérifie le mot de passe de la session auprès d'OpenDirectory (comptes locaux), sans jamais le stocker.
enum MotDePasse {
    static func verifier(_ motDePasse: String, utilisateur: String = NSUserName()) async -> Bool {
        guard !motDePasse.isEmpty else { return false }
        return await Task.detached(priority: .userInitiated) {
            do {
                let noeud = try ODNode(session: .default(), type: ODNodeType(kODNodeTypeLocalNodes))
                // « dsRecTypeStandard:Users » = kODRecordTypeUsers (constante globale non sûre en Swift 6).
                let fiche = try noeud.record(withRecordType: "dsRecTypeStandard:Users", name: utilisateur, attributes: nil)
                try fiche.verifyPassword(motDePasse)
                return true
            } catch {
                return false
            }
        }.value
    }
}
