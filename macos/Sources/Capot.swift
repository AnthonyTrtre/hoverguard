import AppKit
import IOKit
import SocleCore
import SocleEnergie

/// Mode « capot fermé » : fermer l'écran du MacBook ne change rien (le Mac reste éveillé, les agents continuent) et
/// la garde se pose aussitôt.
enum ReglesCapot {
    /// La veille reste interdite tant que l'option est active, sauf batterie trop basse (même seuil que l'éveil) : un
    /// Mac fermé qui ne dort pas chauffe et se vide.
    static func doitEmpecherVeille(option: Bool, batterie: EtatBatterie, seuil: Int) -> Bool {
        option && !ReglesEveil.doitArreter(batterie, seuil: seuil)
    }

    /// Garde posée au moment où le capot se ferme (pas en continu : lever la garde capot fermé reste possible).
    static func doitPoserGarde(option: Bool, etaitFerme: Bool, estFerme: Bool, etat: EtatGarde) -> Bool {
        option && !etaitFerme && estFerme && etat == .libre
    }

    /// Nom de compte accepté dans la règle sudo : lettres, chiffres, point, tiret, souligné.
    static func compteValide(_ nom: String) -> Bool {
        !nom.isEmpty && nom.count <= 64 && nom.unicodeScalars.allSatisfy {
            $0.isASCII && (CharacterSet.alphanumerics.contains($0) || "._-".unicodeScalars.contains($0))
        }
    }

    /// Contenu de la règle : deux commandes exactes, rien d'autre.
    static func regleSudo(compte: String) -> String {
        "\(compte) ALL=(root) NOPASSWD: /usr/bin/pmset -a disablesleep 1, /usr/bin/pmset -a disablesleep 0"
    }
}

/// État du capot, lu dans le registre d'alimentation (`AppleClamshellState`).
enum Capot {
    static var estFerme: Bool {
        let racine = IOServiceGetMatchingService(kIOMainPortDefault, IOServiceMatching("IOPMrootDomain"))
        guard racine != 0 else { return false }
        defer { IOObjectRelease(racine) }
        let valeur = IORegistryEntryCreateCFProperty(racine, "AppleClamshellState" as CFString, kCFAllocatorDefault, 0)
        return (valeur?.takeRetainedValue() as? Bool) ?? false
    }
}

/// Interdire la veille demande l'administrateur (`pmset -a disablesleep`). Une seule fois, avec le mot de passe tapé
/// dans la fenêtre de macOS, Hoverguard installe une règle `sudo` qui n'autorise que ces deux commandes ; ensuite il
/// bascule seul. La règle est vérifiée par `visudo` avant d'être posée.
enum VeilleCapot {
    static let regle = "/etc/sudoers.d/hoverguard"

    static var autorisee: Bool { FileManager.default.fileExists(atPath: regle) }

    /// Demande le mot de passe administrateur (fenêtre de macOS) et installe la règle. Faux si refusé.
    @MainActor static func autoriser() -> Bool {
        let compte = NSUserName()
        guard ReglesCapot.compteValide(compte) else { return false }
        let script = """
        f=$(/usr/bin/mktemp) && /usr/bin/printf '%s\\n' '\(ReglesCapot.regleSudo(compte: compte))' > "$f" && \
        /usr/sbin/visudo -cf "$f" >/dev/null && /usr/bin/install -m 0440 -o root -g wheel "$f" \(regle); r=$?; /bin/rm -f "$f"; exit $r
        """
        return administrateur(script)
    }

    /// Retire la règle (mot de passe administrateur) et rend la veille au système.
    @MainActor static func retirer() -> Bool {
        empecher(false)
        return administrateur("/bin/rm -f \(regle)")
    }

    /// Interdit ou permet la veille, sans mot de passe grâce à la règle. Faux si la règle manque.
    @discardableResult
    static func empecher(_ oui: Bool) -> Bool {
        guard autorisee else { return false }
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/sudo")
        p.arguments = ["-n", "/usr/bin/pmset", "-a", "disablesleep", oui ? "1" : "0"]
        p.standardOutput = FileHandle.nullDevice
        p.standardError = FileHandle.nullDevice
        do { try p.run() } catch { return false }
        p.waitUntilExit()
        return p.terminationStatus == 0
    }

    @MainActor private static func administrateur(_ commande: String) -> Bool {
        let echappee = commande.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"")
        let source = "do shell script \"\(echappee)\" with prompt \"Hoverguard veut garder le Mac éveillé capot fermé.\" with administrator privileges"
        var erreur: NSDictionary?
        _ = NSAppleScript(source: source)?.executeAndReturnError(&erreur)
        return erreur == nil
    }
}

/// Surveille le capot (toutes les secondes) et applique le mode : veille interdite tant que l'option est active et la
/// batterie suffisante, garde posée à la fermeture.
@MainActor
final class ControleurCapot {
    private let reglages: UserDefaults
    private var minuteur: Timer?
    private var etaitFerme = Capot.estFerme
    private var veilleEmpechee: Bool?
    var poserGarde: () -> Void = {}
    /// Capot rouvert (option active ou non) : Touch ID, coupé capot fermé, doit être réarmé.
    var surOuverture: () -> Void = {}
    var etatGarde: () -> EtatGarde = { .libre }
    var seuilBatterie: () -> Int = { 20 }

    init(reglages: UserDefaults = .standard) { self.reglages = reglages }

    var option: Bool {
        get { reglages.bool(forKey: "capotFerme") }
        set { reglages.set(newValue, forKey: "capotFerme"); verifier() }
    }

    func demarrer() {
        minuteur?.invalidate()
        minuteur = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.verifier() }
        }
        minuteur?.tolerance = 0.3
        verifier()
    }

    /// À la sortie de l'app : la veille est toujours rendue au système.
    func arreter() {
        minuteur?.invalidate()
        minuteur = nil
        if veilleEmpechee == true || option { VeilleCapot.empecher(false) }
        veilleEmpechee = false
    }

    private func verifier() {
        let empecher = ReglesCapot.doitEmpecherVeille(option: option && VeilleCapot.autorisee, batterie: Batterie.lire(),
                                                     seuil: seuilBatterie())
        if empecher != veilleEmpechee, VeilleCapot.autorisee {
            if VeilleCapot.empecher(empecher) {
                veilleEmpechee = empecher
                Journal.app.info("Veille capot fermé \(empecher ? "interdite" : "permise", privacy: .public)")
            }
        }
        let ferme = Capot.estFerme
        if ReglesCapot.doitPoserGarde(option: option, etaitFerme: etaitFerme, estFerme: ferme, etat: etatGarde()) {
            Journal.app.info("Capot fermé : garde posée")
            poserGarde()
        }
        if etaitFerme, !ferme { surOuverture() }
        etaitFerme = ferme
    }
}
