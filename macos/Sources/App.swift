import AppKit
import SocleBarreDeMenus
import SocleCore
import Sparkle
import SwiftUI

/// Point d'entrée : `--gardien <pid>` fait de ce processus le gardien de la garde (voir `Gardien`), sinon l'app démarre.
@main
enum Lanceur {
    static func main() {
        let args = CommandLine.arguments
        if args.count >= 3, args[1] == Gardien.argument, let pid = Int32(args[2]) { Gardien.veiller(pid: pid) }
        HoverguardApp.main()
    }
}

struct HoverguardApp: App {
    @NSApplicationDelegateAdaptor private var delegue: Delegue

    var body: some Scene {
        Settings { EmptyView() }  // tout se règle depuis le menu de l'icône
    }
}

@MainActor
final class Delegue: NSObject, NSApplicationDelegate, NSMenuDelegate, SPUUpdaterDelegate {
    let garde = ControleurGarde()
    let eveil = ControleurEveil()
    private lazy var reglages = FenetreReglages(garde: garde)
    /// Mises à jour intégrées (Sparkle) : archives signées par Utahpik, vérifiées avant installation.
    private var miseAJour: SPUStandardUpdaterController?
    /// Relance d'une mise à jour repoussée tant que la garde est posée.
    private var relanceEnAttente: (() -> Void)?
    private let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
    private let menu = NSMenu()

    /// Quitter pendant la garde est refusé, sauf à l'extinction ou à la fermeture de session (audit C1).
    private var extinction = false

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        extinction || garde.etat == .libre ? .terminateNow : .terminateCancel
    }

    func applicationWillTerminate(_ notification: Notification) {
        eveil.desactiver()
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        // Pendant les tests l'app sert d'hôte : pas de filtre d'entrées, pas d'inscription au démarrage.
        guard ProcessInfo.processInfo.environment["XCTestConfigurationFilePath"] == nil else { return }
        Pro.accorderProLancementSiEdition()
        miseAJour = SPUStandardUpdaterController(startingUpdater: true, updaterDelegate: self, userDriverDelegate: nil)
        #if DEBUG
        // Vérification visuelle : `HOVERGUARD_REGLAGES=1` ouvre les réglages au lancement.
        if ProcessInfo.processInfo.environment["HOVERGUARD_REGLAGES"] != nil { reglages.montrer() }
        #endif
        NotificationCenter.default.addObserver(forName: .rechercherMisesAJour, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.miseAJour?.checkForUpdates(nil) }
        }
        NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.willPowerOffNotification, object: nil,
                                                          queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.extinction = true }
        }
        menu.delegate = self
        item.menu = menu
        garde.surChangement = { [weak self] in
            guard let self else { return }
            self.rafraichirIcone()
            if self.garde.etat == .libre, let relance = self.relanceEnAttente {
                self.relanceEnAttente = nil
                relance()
            }
        }
        eveil.surChangement = { [weak self] in self?.rafraichirIcone() }
        garde.demarrer()
        rafraichirIcone()
        proposerOuvertureALaConnexion()
        if !Filtre.autorise { Filtre.demanderAutorisation() }
    }

    func application(_ application: NSApplication, open urls: [URL]) {
        urls.compactMap(Ordre.init(url:)).forEach(garde.recevoir)
    }

    private func rafraichirIcone() {
        // Logo Hoverguard (Codex, variante « Sentinelle ») : drone posé quand l'écran est libre, bouclier sous la garde.
        let nom = garde.etat == .libre ? "BarreMenus-libre" : "BarreMenus-garde"
        let image = NSImage(named: nom)
        image?.isTemplate = true
        item.button?.image = image
        item.button?.appearsDisabled = false
        item.button?.toolTip = garde.etat == .libre ? "Hoverguard : \(Textes.compteur(principaux: garde.principauxActifs, sousAgents: garde.sousAgentsActifs))"
                                                    : "Hoverguard : garde posée"
    }

    func menuNeedsUpdate(_ menu: NSMenu) {
        menu.removeAllItems()
        let g = garde, e = eveil
        titre(g.etat == .libre ? "Écran libre" : "Garde posée", dans: menu)
        titre(Textes.compteur(principaux: g.principauxActifs, sousAgents: g.sousAgentsActifs), dans: menu)
        let parOutil = Dictionary(grouping: g.detectes, by: \.nom).mapValues { $0.filter(\.actif).count }
            .merging(g.journaux.principaux) { max($0, $1) }
        let ouverts = parOutil.sorted { $0.key < $1.key }.map { nom, n in "\(nom) : \(n) au travail" }
        if !ouverts.isEmpty { titre(ouverts.joined(separator: " · "), dans: menu) }
        if !g.agents.enAttente().isEmpty {
            titre(Textes.statutAgents(actifs: [], enAttente: g.agents.enAttente()), dans: menu)
        }
        guard g.etat == .libre else {
            // Sous la garde, le menu ne permet ni de lever la garde ni de quitter : on y accède au clavier (⌃F8) ou par
            // la Commande vocale, qui passent le filtre (audit C1). Seuls Touch ID et le mot de passe lèvent la garde.
            titre("Touch ID ou mot de passe pour lever la garde", dans: menu)
            return
        }
        menu.addItem(ItemAction("Poser la garde   ⌃⌘W") { g.verrouiller() })

        // Ex-Veilleur : garder le Mac éveillé sans poser la garde.
        menu.addItem(.separator())
        menu.addItem(.sectionHeader(title: "Garder le Mac éveillé"))
        if let session = e.session {
            let reste = session.restant(a: .now).map { "encore \(ReglesEveil.texteRestant($0))" } ?? "jusqu'à l'arrêt"
            titre("Éveillé, \(reste)", dans: menu)
            menu.addItem(ItemAction("Arrêter l'éveil") { e.desactiver() })
        }
        for duree in Duree.allCases {
            menu.addItem(ItemAction(duree.libelle, coche: e.session?.duree == duree) { e.activer(duree) })
        }
        menu.addItem(ItemAction("Garder aussi l'écran allumé", coche: e.ecranAllume) { e.ecranAllume.toggle() })
        let batterie = NSMenuItem(title: "Arrêter l'éveil sur batterie", action: nil, keyEquivalent: "")
        batterie.submenu = NSMenu()
        for seuil in [0, 10, 20, 30, 50] {
            batterie.submenu?.addItem(ItemAction(seuil == 0 ? "Jamais" : "À \(seuil) %", coche: e.seuilBatterie == seuil) {
                e.seuilBatterie = seuil
            })
        }
        menu.addItem(batterie)

        menu.addItem(.separator())
        if !Filtre.autorise {
            menu.addItem(ItemAction("⚠︎ Autoriser Accessibilité (clavier et souris)…") { Filtre.demanderAutorisation() })
        }
        if g.dernierRapport != nil {
            menu.addItem(ItemAction("Rapport de la dernière garde…") { g.montrerDernierRapport() })
        }
        menu.addItem(ItemAction("Rechercher des mises à jour…") { [weak self] in self?.miseAJour?.checkForUpdates(nil) })
        menu.addItem(ItemAction("Réglages…", touche: ",") { [weak self] in self?.reglages.montrer() })
        menu.addItem(ItemAction("À propos de Hoverguard") { [weak self] in self?.reglages.montrer(onglet: .aPropos) })
        menu.addItem(.separator())
        menu.addItem(ItemAction("Quitter Hoverguard", touche: "q") { NSApp.terminate(nil) })
    }

    /// Au premier lancement, on demande avant d'inscrire Hoverguard au démarrage (audit B6).
    private func proposerOuvertureALaConnexion() {
        guard Bundle.main.bundlePath.hasPrefix("/Applications/"),
              !UserDefaults.standard.bool(forKey: "ouvertureDemandee") else { return }
        UserDefaults.standard.set(true, forKey: "ouvertureDemandee")
        let alerte = NSAlert()
        alerte.messageText = "Ouvrir Hoverguard à chaque démarrage ?"
        alerte.informativeText = "Le drone sera prêt dans la barre des menus dès l'ouverture de session. Modifiable dans les Réglages."
        alerte.addButton(withTitle: "Oui")
        alerte.addButton(withTitle: "Non")
        NSApp.activate()
        if alerte.runModal() == .alertFirstButtonReturn, !OuvertureALaConnexion.active { OuvertureALaConnexion.basculer() }
    }

    private func titre(_ texte: String, dans menu: NSMenu) {
        let item = NSMenuItem(title: texte, action: nil, keyEquivalent: "")
        item.isEnabled = false
        menu.addItem(item)
    }



    func brancher(_ outil: BranchementAgents) {
        let alerte = NSAlert()
        alerte.messageText = "Brancher \(outil.nom) à Hoverguard ?"
        var texte = """
        Hoverguard ajoute ses hooks dans \(outil.fichier.path.replacingOccurrences(of: NSHomeDirectory(), with: "~")) \
        (début de travail, fin\(outil.evenements.contains { $0.etat == .attente } ? ", attente" : "")\
        \(outil.evenements.contains { $0.etat == .sousAgentDebut } ? ", sous-agents" : "")) pour compter les agents et faire \
        voler le drone. Une copie du fichier est gardée à côté. Seuls l'état et 4 caractères de l'identifiant de session \
        sont transmis, rien ne sort du Mac.
        """
        if outil.nom == "Codex" { texte += "\n\nDans Codex, tape une fois /hooks pour approuver les nouveaux hooks." }
        if outil.nom == "Cursor" { texte += "\n\nCursor ne signale pas quand il attend ta réponse : seulement travail et fin." }
        alerte.informativeText = texte
        alerte.addButton(withTitle: "Brancher")
        alerte.addButton(withTitle: "Annuler")
        NSApp.activate()
        guard alerte.runModal() == .alertFirstButtonReturn else { return }
        do {
            try outil.ecrire(outil.brancher(outil.lire()))
        } catch {
            signalerErreur(outil, error)
        }
    }

    func debrancher(_ outil: BranchementAgents) {
        do {
            try outil.ecrire(outil.debrancher(outil.lire()))
        } catch {
            signalerErreur(outil, error)
        }
    }

    private func signalerErreur(_ outil: BranchementAgents, _ erreur: Error) {
        Journal.app.error("Branchement \(outil.nom, privacy: .public) impossible")
        let alerte = NSAlert()
        alerte.alertStyle = .warning
        alerte.messageText = "\(outil.nom) n'a pas pu être modifié"
        alerte.informativeText = erreur.localizedDescription
        NSApp.activate()
        alerte.runModal()
    }

    // MARK: Mises à jour

    /// Jamais de relance pendant une garde : l'écran resterait sans voile. On attend le déverrouillage.
    nonisolated func updater(_ updater: SPUUpdater, shouldPostponeRelaunchForUpdate item: SUAppcastItem,
                             untilInvokingBlock installHandler: @escaping () -> Void) -> Bool {
        let relance = Relance(installHandler)
        return MainActor.assumeIsolated {
            guard garde.etat != .libre else { return false }
            relanceEnAttente = relance.action
            return true
        }
    }
}

/// Le bloc de Sparkle, appelé une seule fois sur le fil principal au déverrouillage.
private final class Relance: @unchecked Sendable {
    let action: () -> Void
    init(_ action: @escaping () -> Void) { self.action = action }
}
