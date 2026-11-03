import Foundation
import SocleCore
import SocleEnergie

/// Garder le Mac éveillé pour une durée (ex-Veilleur) : session en cours et réglages.
@MainActor
final class ControleurEveil {
    private(set) var session: Session?
    private var assertion: AssertionEveil?
    private var minuteur: Timer?
    private let reglages: UserDefaults
    /// Appelé à chaque changement d'état, pour mettre l'icône à jour.
    var surChangement: () -> Void = {}

    init(reglages: UserDefaults = .standard) {
        self.reglages = reglages
    }

    var estActif: Bool { session != nil }

    var ecranAllume: Bool {
        get { reglages.bool(forKey: "ecranAllume") }
        set {
            reglages.set(newValue, forKey: "ecranAllume")
            if let session { activer(session.duree, depuis: session) }  // remplace l'assertion par le bon type
        }
    }

    /// Seuil d'arrêt sur batterie en %, 0 = jamais.
    var seuilBatterie: Int {
        get { reglages.object(forKey: "seuilBatterie") as? Int ?? 20 }
        set { reglages.set(newValue, forKey: "seuilBatterie") }
    }

    /// Dernière durée choisie (cochée dans le menu au prochain lancement).
    var dureeParDefaut: Duree {
        get { Duree(rawValue: reglages.integer(forKey: "dureeParDefaut")) ?? .indefinie }
        set { reglages.set(newValue.rawValue, forKey: "dureeParDefaut") }
    }

    /// `depuis` garde la fin d'une session existante (changement de réglage en cours de route).
    func activer(_ duree: Duree, depuis existante: Session? = nil) {
        assertion = nil
        guard let nouvelle = AssertionEveil(ecranAllume: ecranAllume, raison: "Hoverguard garde le Mac éveillé") else {
            Journal.app.error("Assertion d'alimentation refusée par le système")
            return desactiver()
        }
        assertion = nouvelle
        session = existante ?? Session(duree: duree)
        if existante == nil { dureeParDefaut = duree }
        memoriser(session)
        minuteur?.invalidate()
        minuteur = Timer.scheduledTimer(withTimeInterval: 15, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.verifier() }
        }
        minuteur?.tolerance = 3
        Journal.app.info("Éveil activé : \(duree.libelle, privacy: .public)")
        surChangement()
    }

    func desactiver() {
        suspendre()
        if session != nil { Journal.app.info("Éveil arrêté") }
        session = nil
        memoriser(nil)
        surChangement()
    }

    /// Sortie de l'app sans que l'utilisateur ait arrêté l'éveil (mise à jour, extinction) : l'assertion est rendue,
    /// mais la session reste mémorisée et reprend au prochain lancement.
    func suspendre() {
        minuteur?.invalidate()
        minuteur = nil
        assertion = nil
    }

    /// Au lancement : reprend la session mémorisée si elle n'est pas finie (« Jusqu'à l'arrêt » ne se décoche plus
    /// tout seul après une mise à jour).
    func restaurer(maintenant: Date = .now) {
        guard reglages.bool(forKey: "eveilActif"),
              let duree = Duree(rawValue: reglages.integer(forKey: "eveilDuree")) else { return }
        let memorisee = Session(duree: duree, fin: reglages.object(forKey: "eveilFin") as? Date)
        if memorisee.estExpiree(a: maintenant) { return memoriser(nil) }
        activer(duree, depuis: memorisee)
    }

    private func memoriser(_ s: Session?) {
        reglages.set(s != nil, forKey: "eveilActif")
        reglages.set(s?.duree.rawValue ?? 0, forKey: "eveilDuree")
        if let fin = s?.fin { reglages.set(fin, forKey: "eveilFin") } else { reglages.removeObject(forKey: "eveilFin") }
    }

    /// Fin de durée ou batterie trop basse : on rend la main à la veille normale.
    func verifier(maintenant: Date = .now, batterie: EtatBatterie = Batterie.lire()) {
        guard let session else { return }
        if session.estExpiree(a: maintenant) || ReglesEveil.doitArreter(batterie, seuil: seuilBatterie) {
            desactiver()
        } else {
            surChangement()
        }
    }
}
