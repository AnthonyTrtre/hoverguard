import AppKit
import SocleCore

/// Filtre clavier et souris par `CGEventTap` au niveau de la session. Il voit aussi les événements envoyés par les
/// autres apps (Claude, outils d'automatisation) : ils portent le pid de leur expéditeur, le vrai matériel porte 0.
/// Exige l'autorisation Accessibilité.
@MainActor
final class Filtre {
    private var tap: CFMachPort?
    private var source: CFRunLoopSource?
    /// Lu à chaque événement : l'état courant de la garde.
    var etat: () -> EtatGarde = { .libre }
    var surPoser: () -> Void = {}
    var surReveil: () -> Void = {}
    /// Touche tapée pendant la demande de mot de passe.
    var surSaisie: (ActionSaisie) -> Void = { _ in }
    /// Le système a coupé le filtre et il n'a pas pu repartir (autorisation retirée…).
    var surPanne: () -> Void = {}

    static var autorise: Bool { AXIsProcessTrusted() }

    static func demanderAutorisation() {
        let options = ["AXTrustedCheckOptionPrompt": true] as CFDictionary  // valeur de kAXTrustedCheckOptionPrompt
        _ = AXIsProcessTrustedWithOptions(options)
    }

    var actif: Bool { tap.map(CGEvent.tapIsEnabled) ?? false }

    @discardableResult
    func demarrer() -> Bool {
        guard tap == nil else { return true }
        let types: [CGEventType] = [.keyDown, .keyUp, .flagsChanged, .leftMouseDown, .leftMouseUp, .rightMouseDown,
                                    .rightMouseUp, .otherMouseDown, .otherMouseUp, .leftMouseDragged,
                                    .rightMouseDragged, .otherMouseDragged, .scrollWheel, .mouseMoved,
                                    .tabletPointer, .tabletProximity]
        // Types sans nom en Swift (audit H4) : 14 = touches système (luminosité, son, média, Dictée),
        // 18-20 et 29-32 = gestes du trackpad (Mission Control, espaces…), 34 = pression.
        let autres: [UInt32] = [14, 18, 19, 20, 29, 30, 31, 32, 34]
        let masque = types.reduce(CGEventMask(0)) { $0 | (1 << $1.rawValue) }
            | autres.reduce(CGEventMask(0)) { $0 | (1 << $1) }
        guard let tap = CGEvent.tapCreate(tap: .cgSessionEventTap, place: .headInsertEventTap, options: .defaultTap,
                                          eventsOfInterest: masque, callback: rappel,
                                          userInfo: Unmanaged.passUnretained(self).toOpaque()) else {
            Journal.app.error("Filtre d'entrées impossible : autorisation Accessibilité manquante ?")
            return false
        }
        self.tap = tap
        source = CFMachPortCreateRunLoopSource(nil, tap, 0)
        CFRunLoopAddSource(CFRunLoopGetMain(), source, .commonModes)
        CGEvent.tapEnable(tap: tap, enable: true)
        return true
    }

    func arreter() {
        if let tap { CGEvent.tapEnable(tap: tap, enable: false); CFMachPortInvalidate(tap) }
        if let source { CFRunLoopRemoveSource(CFRunLoopGetMain(), source, .commonModes) }
        tap = nil
        source = nil
    }

    /// `true` : l'événement continue son chemin ; `false` : il est avalé.
    fileprivate func laisserPasser(_ type: CGEventType, _ entree: Entree, repetition: Bool, saisie: ActionSaisie?) -> Bool {
        if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
            if let tap { CGEvent.tapEnable(tap: tap, enable: true) }
            if !actif { surPanne() }
            return true
        }
        switch Regles.decider(entree, etat: etat()) {
        case .laisser: return true
        case .bloquer: return false
        // Le travail lourd (voile, SceneKit, fenêtres) est reporté : le rappel du tap doit rendre la main vite, sinon
        // macOS le coupe et le voile devient perméable (audit B5).
        case .poser:
            if !repetition { DispatchQueue.main.async { self.surPoser() } }
            return false
        case .reveiller:
            DispatchQueue.main.async { self.surReveil() }
            return false
        case .saisir:
            if let saisie { surSaisie(saisie) }
            return false
        }
    }
}

/// Le tap est posé sur la boucle principale : le rappel arrive sur le fil principal. On lit l'événement ici, puis on
/// ne passe au contrôleur que des valeurs simples.
private let rappel: CGEventTapCallBack = { _, type, evenement, donnees in
    guard let donnees else { return Unmanaged.passUnretained(evenement) }
    let f = evenement.flags
    let genre: Entree.Genre = switch type {
    case .keyDown: .touche
    case .leftMouseDown, .rightMouseDown, .otherMouseDown: .clic
    default: .autre
    }
    let entree = Entree(pidSource: evenement.getIntegerValueField(.eventSourceUnixProcessID), genre: genre,
                        estRaccourci: genre == .touche && Regles.estRaccourci(
                            caracteres: NSEvent(cgEvent: evenement)?.charactersIgnoringModifiers,
                            controle: f.contains(.maskControl), commande: f.contains(.maskCommand),
                            option: f.contains(.maskAlternate), majuscule: f.contains(.maskShift)),
                        avecCommande: f.contains(.maskCommand))
    let repetition = evenement.getIntegerValueField(.keyboardEventAutorepeat) != 0
    let saisie = genre == .touche ? ActionSaisie.depuis(
        codeTouche: evenement.getIntegerValueField(.keyboardEventKeycode),
        caracteres: NSEvent(cgEvent: evenement)?.characters,
        avecCommande: f.contains(.maskCommand), avecControle: f.contains(.maskControl)) : nil
    let filtre = Unmanaged<Filtre>.fromOpaque(donnees).takeUnretainedValue()
    let garder = MainActor.assumeIsolated { filtre.laisserPasser(type, entree, repetition: repetition, saisie: saisie) }
    return garder ? Unmanaged.passUnretained(evenement) : nil
}
