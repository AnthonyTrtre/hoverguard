import Foundation
import IOKit.ps
import IOKit.pwr_mgt

/// Assertion d'alimentation : tant qu'elle existe, macOS ne met pas le Mac en veille d'inactivité.
/// Libérée au `deinit` ; macOS la libère aussi si l'app quitte ou plante. Vérifier avec `pmset -g assertions`.
public final class AssertionEveil {
    private var id: IOPMAssertionID = 0

    /// `raison` apparaît dans `pmset -g assertions` : la rendre lisible (« Veilleur garde le Mac éveillé »).
    public init?(ecranAllume: Bool, raison: String) {
        let type = ecranAllume ? kIOPMAssertionTypePreventUserIdleDisplaySleep : kIOPMAssertionTypePreventUserIdleSystemSleep
        let ok = IOPMAssertionCreateWithName(type as CFString, IOPMAssertionLevel(kIOPMAssertionLevelOn),
                                             raison as CFString, &id)
        guard ok == kIOReturnSuccess else { return nil }
    }

    deinit { IOPMAssertionRelease(id) }
}

public enum Batterie {
    public static func lire() -> EtatBatterie {
        let info = IOPSCopyPowerSourcesInfo().takeRetainedValue()
        let surSecteur = IOPSGetProvidingPowerSourceType(info)?.takeUnretainedValue() as String? == kIOPMACPowerKey
        let sources = IOPSCopyPowerSourcesList(info).takeRetainedValue() as [CFTypeRef]
        for source in sources {
            guard let d = IOPSGetPowerSourceDescription(info, source)?.takeUnretainedValue() as? [String: Any],
                  d[kIOPSTypeKey] as? String == kIOPSInternalBatteryType,
                  let charge = d[kIOPSCurrentCapacityKey] as? Int,
                  let max = d[kIOPSMaxCapacityKey] as? Int, max > 0 else { continue }
            return EtatBatterie(niveau: charge * 100 / max, surSecteur: surSecteur)
        }
        return EtatBatterie(niveau: nil, surSecteur: true)
    }
}

public struct EtatBatterie: Equatable, Sendable {
    /// Pourcentage de charge, `nil` sur un Mac sans batterie.
    public let niveau: Int?
    public let surSecteur: Bool

    public init(niveau: Int?, surSecteur: Bool) {
        self.niveau = niveau
        self.surSecteur = surSecteur
    }
}
