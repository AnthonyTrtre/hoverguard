import Foundation
import IOKit.pwr_mgt
import Testing
import SocleEnergie
@testable import Hoverguard

@Suite struct SessionEveilTests {
    let t0 = Date(timeIntervalSince1970: 1_000_000)

    @Test func dureeFinieExpire() {
        let s = Session(duree: .quinzeMinutes, debut: t0)
        #expect(s.restant(a: t0.addingTimeInterval(60)) == 840)
        #expect(!s.estExpiree(a: t0.addingTimeInterval(899)))
        #expect(s.estExpiree(a: t0.addingTimeInterval(900)))
        #expect(s.restant(a: t0.addingTimeInterval(5000)) == 0)
    }

    @Test func sessionIndefinieNExpireJamais() {
        let s = Session(duree: .indefinie, debut: t0)
        #expect(s.restant(a: t0.addingTimeInterval(1e9)) == nil)
        #expect(!s.estExpiree(a: t0.addingTimeInterval(1e9)))
    }

    @Test(arguments: [(30.0, "1 min"), (600, "10 min"), (3599, "1 h 00"), (3900, "1 h 05"), (14400, "4 h 00")])
    func texteRestant(secondes: Double, attendu: String) {
        #expect(ReglesEveil.texteRestant(secondes) == attendu)
    }
}

@Suite struct BatterieEveilTests {
    @Test func arretSeulementSurBatterieSousLeSeuil() {
        #expect(ReglesEveil.doitArreter(EtatBatterie(niveau: 15, surSecteur: false), seuil: 20))
        #expect(ReglesEveil.doitArreter(EtatBatterie(niveau: 20, surSecteur: false), seuil: 20))
        #expect(!ReglesEveil.doitArreter(EtatBatterie(niveau: 21, surSecteur: false), seuil: 20))
        #expect(!ReglesEveil.doitArreter(EtatBatterie(niveau: 5, surSecteur: true), seuil: 20))
        #expect(!ReglesEveil.doitArreter(EtatBatterie(niveau: 5, surSecteur: false), seuil: 0))
        #expect(!ReglesEveil.doitArreter(EtatBatterie(niveau: nil, surSecteur: false), seuil: 20))
    }

    @Test func lectureReelleCoherente() {
        let b = Batterie.lire()
        if let n = b.niveau { #expect((0...100).contains(n)) }
    }
}

@MainActor @Suite struct ControleurEveilTests {
    func controleur() -> ControleurEveil {
        let nom = "veilleur-tests-\(UUID().uuidString)"
        return ControleurEveil(reglages: UserDefaults(suiteName: nom)!)
    }

    /// Noms des assertions d'alimentation que macOS attribue à ce processus (ce que montre `pmset -g assertions`).
    func assertionsDuProcessus() -> [String] {
        var parPid: Unmanaged<CFDictionary>?
        guard IOPMCopyAssertionsByProcess(&parPid) == kIOReturnSuccess,
              let d = parPid?.takeRetainedValue() as? [Int: [[String: Any]]] else { return [] }
        return (d[Int(getpid())] ?? []).compactMap { $0[kIOPMAssertionNameKey] as? String }
    }

    @Test func activerPuisArreter() {
        let c = controleur()
        c.activer(.trenteMinutes)
        #expect(c.estActif)
        #expect(assertionsDuProcessus().contains("Hoverguard garde le Mac éveillé"))
        #expect(c.dureeParDefaut == .trenteMinutes)
        c.desactiver()
        #expect(!c.estActif)
        #expect(!assertionsDuProcessus().contains("Hoverguard garde le Mac éveillé"))
    }

    @Test func arretAutomatiqueEnFinDeDuree() {
        let c = controleur()
        c.activer(.quinzeMinutes)
        c.verifier(maintenant: .now.addingTimeInterval(901), batterie: EtatBatterie(niveau: nil, surSecteur: true))
        #expect(!c.estActif)
    }

    @Test func arretAutomatiqueBatterieBasse() {
        let c = controleur()
        c.seuilBatterie = 20
        c.activer(.indefinie)
        c.verifier(batterie: EtatBatterie(niveau: 50, surSecteur: false))
        #expect(c.estActif)
        c.verifier(batterie: EtatBatterie(niveau: 19, surSecteur: false))
        #expect(!c.estActif)
    }

    @Test func changerEcranGardeLaFin() {
        let c = controleur()
        c.activer(.uneHeure)
        let fin = c.session?.fin
        c.ecranAllume = true
        #expect(c.estActif && c.session?.fin == fin)
        c.desactiver()
    }
}
