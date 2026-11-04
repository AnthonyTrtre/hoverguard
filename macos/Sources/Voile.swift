import AppKit
import SwiftUI

/// Ce que montre le voile ; partagé par les fenêtres de tous les écrans.
@MainActor @Observable
final class EtatVoile {
    var phrase = ""
    var compteur = ""
    var equipe = ""
    var batterie: String?
    var agentsActifs = 0
    var enVol = true
    /// Quelqu'un a touché le clavier : le drone s'approche et le mot de passe s'affiche.
    var approche = false
    var motDePasse = ""
    var erreur: String?
    var verification = false
    var derniereActivite = Date.now
    var intensiteLasers = 0.35
}


/// Une fenêtre par écran, au-dessus de tout. Réglages mesurés sur macOS 26 (2026-11-03) :
/// - opacité 0,998 et non 1 (0,99 laissait deviner le texte dessous ; 0,999 marche encore) : à 100 %, macOS cesse de
///   dessiner les fenêtres dessous, qui se croient cachées (Chrome ralentit) et les captures d'écran deviennent noires ;
/// - exclue des captures (`sharingType = .none`) : Claude voit Chrome, pas le voile ;
/// - transparente aux clics : les clics de Claude atteignent Chrome ; ceux d'une main humaine sont bloqués par le Filtre.
@MainActor
final class Voile {
    private var fenetres: [NSWindow] = []
    private var minuteurVol: Timer?
    let etat = EtatVoile()

    var estPose: Bool { !fenetres.isEmpty }

    /// `HOVERGUARD_APERCU=1` : voile visible dans les captures et qui ne bloque rien, pour vérifier son rendu.
    /// Seulement dans les builds Debug : en Release, personne ne peut l'activer de l'extérieur (audit B4).
    #if DEBUG
    static let apercu = ProcessInfo.processInfo.environment["HOVERGUARD_APERCU"] != nil
    #else
    static let apercu = false
    #endif

    func poser() {
        retirer()
        for ecran in NSScreen.screens {
            let f = NSWindow(contentRect: ecran.frame, styleMask: [.borderless], backing: .buffered, defer: false, screen: ecran)
            f.level = NSWindow.Level(rawValue: Int(CGShieldingWindowLevel()))
            f.isOpaque = false
            f.backgroundColor = NSColor.black.withAlphaComponent(0.998)
            f.sharingType = Voile.apercu ? .readOnly : .none
            f.ignoresMouseEvents = true
            f.hasShadow = false
            f.isReleasedWhenClosed = false
            f.collectionBehavior = [.canJoinAllSpaces, .stationary, .fullScreenAuxiliary, .ignoresCycle]
            f.contentView = NSHostingView(rootView: VueVoile(etat: etat, principal: ecran == NSScreen.main))
            f.setFrame(ecran.frame, display: true)
            f.orderFrontRegardless()
            fenetres.append(f)
        }
        etat.enVol = true
        planifierVol(pause: false)
    }

    func retirer() {
        minuteurVol?.invalidate()
        minuteurVol = nil
        fenetres.forEach { $0.orderOut(nil) }
        fenetres = []
    }

    /// Alterne vol et courtes pauses au sol (`ReglesVol`) tant que le voile est posé.
    private func planifierVol(pause: Bool) {
        let duree = Double.random(in: pause ? ReglesVol.pause : ReglesVol.attente)
        minuteurVol = Timer.scheduledTimer(withTimeInterval: duree, repeats: false) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self, self.estPose else { return }
                self.etat.enVol = pause
                self.planifierVol(pause: !pause)
            }
        }
    }

    /// Affiche la demande sur l'écran principal. Hoverguard ne prend PAS le focus : c'est le filtre qui avale les
    /// touches et remplit le mot de passe, l'app de l'agent garde la main (audit C2).
    func approcher() {
        etat.motDePasse = ""
        etat.erreur = nil
        etat.derniereActivite = .now
        etat.approche = true
    }

    /// Retour à la surveillance (Échap, délai dépassé) : le drone recule.
    func reculer() {
        etat.approche = false
        etat.motDePasse = ""
        etat.erreur = nil
    }
}

private struct VueVoile: View {
    let etat: EtatVoile
    /// Le mot de passe ne s'affiche que sur l'écran principal ; le drone et les textes sont partout.
    let principal: Bool

    var body: some View {
        ZStack {
            VueDrone(enVol: etat.enVol, approche: etat.approche, principal: principal,
                     intensiteLasers: etat.intensiteLasers)
                .ignoresSafeArea()
            VStack(spacing: 14) {
                Spacer()
                if etat.approche {
                    if principal { saisie }
                } else {
                    textes
                }
                Spacer().frame(height: 36)
                Text(etat.approche ? "Échap pour laisser le drone surveiller"
                                   : "Posez le doigt sur Touch ID, ou appuyez sur une touche")
                    .font(.callout)
                    .foregroundStyle(.white.opacity(0.45))
                    .padding(.bottom, 40)
            }
            .padding(.horizontal, 40)
        }
        .animation(.easeInOut(duration: 0.5), value: etat.approche)
    }

    private var textes: some View {
        VStack(spacing: 12) {
            Text(etat.phrase)
                .font(.system(size: 32, weight: .semibold, design: .rounded))
                .multilineTextAlignment(.center)
                .foregroundStyle(.white)
            if !etat.compteur.isEmpty {
                Text(etat.compteur)
                    .font(.title2.weight(.medium))
                    .foregroundStyle(etat.agentsActifs > 0 ? .cyan : .gray)
            }
            if !etat.equipe.isEmpty {
                Text(etat.equipe)
                    .font(.title3)
                    .foregroundStyle(.white.opacity(0.7))
            }
            if let batterie = etat.batterie {
                Text(batterie)
                    .font(.title3.weight(.semibold))
                    .foregroundStyle(.orange)
                    .padding(.horizontal, 18).padding(.vertical, 8)
                    .background(.orange.opacity(0.15), in: .capsule)
            }
            TimelineView(.periodic(from: .now, by: 30)) { contexte in
                Text(contexte.date.formatted(date: .omitted, time: .shortened))
                    .font(.system(size: 54, weight: .thin, design: .rounded).monospacedDigit())
                    .foregroundStyle(.white.opacity(0.8))
            }
        }
        .transition(.opacity)
    }

    private var saisie: some View {
        VStack(spacing: 14) {
            Text("Identification")
                .font(.system(size: 26, weight: .semibold, design: .rounded))
                .foregroundStyle(.white)
            Text("Touch ID, ou tapez le mot de passe de la session puis Entrée")
                .foregroundStyle(.white.opacity(0.7))
            // Simple affichage : les touches sont lues par le filtre, jamais par un champ texte.
            Text(etat.motDePasse.isEmpty ? " " : String(repeating: "●", count: min(etat.motDePasse.count, 24)))
                .font(.title3.monospaced())
                .foregroundStyle(.white)
                .padding(.horizontal, 16).padding(.vertical, 10)
                .frame(width: 320)
                .background(.white.opacity(0.1), in: .capsule)
                .overlay(Capsule().stroke(etat.erreur == nil ? .cyan.opacity(0.6) : .red, lineWidth: 1))
                .opacity(etat.verification ? 0.5 : 1)
            if let erreur = etat.erreur {
                Text(erreur).foregroundStyle(.red)
            }
        }
        .transition(.scale(scale: 0.9).combined(with: .opacity))
    }
}
