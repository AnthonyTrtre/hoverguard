import Foundation
import ServiceManagement
import SocleCore

/// Ouverture de l'app à la connexion via `SMAppService`. Fiable seulement pour une app installée dans /Applications.
@MainActor
public enum OuvertureALaConnexion {
    public static var active: Bool { SMAppService.mainApp.status == .enabled }

    public static func basculer() {
        do {
            if active {
                try SMAppService.mainApp.unregister()
            } else {
                try SMAppService.mainApp.register()
            }
        } catch {
            Journal.app.error("Ouverture à la connexion : \(error.localizedDescription, privacy: .public)")
        }
        if SMAppService.mainApp.status == .requiresApproval {
            SMAppService.openSystemSettingsLoginItems()
        }
    }
}

extension OuvertureALaConnexion {
    /// Active l'ouverture à la connexion une seule fois, au tout premier lancement ; ensuite on respecte le choix
    /// fait dans le menu. Seulement pour la copie installée dans /Applications (pas pendant les tests ni en debug).
    public static func activerAuPremierLancement(reglages: UserDefaults = .standard) {
        guard Bundle.main.bundlePath.hasPrefix("/Applications/"), !reglages.bool(forKey: "ouvertureALaConnexionInitialisee") else { return }
        reglages.set(true, forKey: "ouvertureALaConnexionInitialisee")
        // On réinscrit pour que l'entrée pointe bien sur cette copie (une build de test a pu s'inscrire avant).
        try? SMAppService.mainApp.unregister()
        basculer()
    }
}
