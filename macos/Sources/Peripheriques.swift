import AppKit
@preconcurrency import AVFoundation
import SocleCore

/// Une photo par la caméra du Mac, gardée en local (`Application Support/com.utahpik.hoverguard/intrus`).
enum Camera {
    static var dossier: URL { URL.applicationSupportDirectory.appending(path: "com.utahpik.hoverguard/intrus") }

    static var autorisee: Bool { AVCaptureDevice.authorizationStatus(for: .video) == .authorized }

    /// À demander quand l'utilisateur est devant le Mac (au moment de poser la garde), jamais devant un intrus.
    static func demanderAutorisation() async -> Bool {
        if autorisee { return true }
        guard AVCaptureDevice.authorizationStatus(for: .video) == .notDetermined else { return false }
        return await AVCaptureDevice.requestAccess(for: .video)
    }

    /// La caméra intégrée seulement (jamais l'iPhone par Caméra de continuité).
    private static var cameraIntegree: AVCaptureDevice? {
        AVCaptureDevice.DiscoverySession(deviceTypes: [.builtInWideAngleCamera], mediaType: .video, position: .unspecified)
            .devices.first
    }

    /// Les photos sont effacées au bout de 30 jours (audit M8).
    static func purger(apres jours: Int = 30) {
        let limite = Date.now.addingTimeInterval(-Double(jours) * 86_400)
        let fichiers = (try? FileManager.default.contentsOfDirectory(at: dossier, includingPropertiesForKeys: [.creationDateKey])) ?? []
        for f in fichiers where ((try? f.resourceValues(forKeys: [.creationDateKey]))?.creationDate ?? .now) < limite {
            try? FileManager.default.removeItem(at: f)
        }
    }

    static func photographier() async -> URL? {
        guard autorisee, let appareil = cameraIntegree else { return nil }
        return await Task.detached(priority: .userInitiated) { () -> URL? in
            let session = AVCaptureSession()
            session.sessionPreset = .photo
            let sortie = AVCapturePhotoOutput()
            guard let entree = try? AVCaptureDeviceInput(device: appareil), session.canAddInput(entree),
                  session.canAddOutput(sortie) else { return nil }
            session.addInput(entree)
            session.addOutput(sortie)
            session.startRunning()
            defer { session.stopRunning() }
            try? await Task.sleep(for: .seconds(1.2))  // le temps que l'exposition se règle
            let donnees: Data? = await withCheckedContinuation { suite in
                let delegue = Recepteur { suite.resume(returning: $0) }
                sortie.capturePhoto(with: AVCapturePhotoSettings(), delegate: delegue)
                objc_setAssociatedObject(sortie, "recepteur", delegue, .OBJC_ASSOCIATION_RETAIN)
            }
            guard let donnees else { return nil }
            try? FileManager.default.createDirectory(at: dossier, withIntermediateDirectories: true,
                                                     attributes: [.posixPermissions: 0o700])
            let url = dossier.appending(path: "intrus-\(Int(Date.now.timeIntervalSince1970)).jpg")
            do { try donnees.write(to: url) } catch { return nil }
            try? FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
            return url
        }.value
    }

    private final class Recepteur: NSObject, AVCapturePhotoCaptureDelegate, @unchecked Sendable {
        private let fin: (Data?) -> Void
        init(fin: @escaping (Data?) -> Void) { self.fin = fin }
        func photoOutput(_ output: AVCapturePhotoOutput, didFinishProcessingPhoto photo: AVCapturePhoto, error: Error?) {
            fin(error == nil ? photo.fileDataRepresentation() : nil)
        }
    }
}

/// Notifications sur l'iPhone par iMessage, envoyées à l'utilisateur lui-même depuis Messages sur le Mac.
/// Rien ne passe par un service tiers. Lancé dans un processus `osascript` à part, avec le texte et le destinataire
/// passés en arguments (jamais collés dans le script), et hors du fil principal : la première demande
/// d'autorisation de macOS ne doit pas bloquer le filtre d'entrées (audit H1).
enum Notifications {
    private static let script = """
    on run argv
        tell application "Messages"
            set compte to 1st account whose service type = iMessage
            send (item 1 of argv) to participant (item 2 of argv) of compte
        end tell
    end run
    """

    static func envoyer(_ texte: String, a destinataire: String) {
        guard !destinataire.isEmpty else { return }
        Task.detached(priority: .utility) {
            let p = Process()
            p.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
            p.arguments = ["-e", script, texte, destinataire]
            p.standardOutput = FileHandle.nullDevice
            p.standardError = FileHandle.nullDevice
            do {
                try p.run()
                p.waitUntilExit()
                if p.terminationStatus != 0 { Journal.app.error("Notification iMessage refusée ou impossible") }
            } catch {
                Journal.app.error("Notification iMessage : \(error.localizedDescription, privacy: .private)")
            }
        }
    }
}

/// Verrouille la vraie session macOS (l'écran de connexion du système). Les agents en ligne de commande continuent ;
/// ceux qui pilotent l'écran s'arrêtent. Utilisé quand la garde ne peut plus être garantie (audit M1, H2, H5).
/// `SACLockScreenImmediate` est une fonction privée du système (utilisée aussi par Hammerspoon), chargée avec
/// prudence ; si elle manque, on éteint au moins l'écran.
enum VerrouSession {
    static func verrouiller() {
        typealias Fonction = @convention(c) () -> Int32
        if let lib = dlopen("/System/Library/PrivateFrameworks/login.framework/Versions/Current/login", RTLD_LAZY),
           let symbole = dlsym(lib, "SACLockScreenImmediate") {
            _ = unsafeBitCast(symbole, to: Fonction.self)()
            return
        }
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/pmset")
        p.arguments = ["displaysleepnow"]
        try? p.run()
    }
}

/// Temps depuis la dernière action sur le vrai clavier ou la vraie souris.
enum Inactivite {
    static var secondes: TimeInterval {
        // ~0 = kCGAnyInputEventType ; l'état « HID » ne compte que le matériel.
        CGEventSource.secondsSinceLastEventType(.hidSystemState, eventType: CGEventType(rawValue: ~0)!)
    }
}

/// Gardien de la garde : le même exécutable lancé avec `--gardien <pid>`, sans interface, à chaque garde. Si Hoverguard
/// disparaît pendant la garde (Forcer à quitter, plantage), il verrouille aussitôt la vraie session et relance
/// Hoverguard : l'écran ne reste jamais ouvert. Il s'arrête seul quand la garde est levée normalement (témoin retiré).
enum Gardien {
    static let argument = "--gardien"

    /// Fichier présent tant qu'une garde est posée (lisible par le gardien, un autre processus).
    static var temoin: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appending(path: "com.utahpik.hoverguard/garde-en-cours")
    }

    static func poserTemoin(_ pose: Bool) {
        if pose {
            try? FileManager.default.createDirectory(at: temoin.deletingLastPathComponent(), withIntermediateDirectories: true)
            FileManager.default.createFile(atPath: temoin.path, contents: Data())
        } else {
            try? FileManager.default.removeItem(at: temoin)
        }
    }

    static func lancer() {
        guard let exe = Bundle.main.executableURL else { return }
        let p = Process()
        p.executableURL = exe
        p.arguments = [argument, String(getpid())]
        do { try p.run() } catch { Journal.app.error("Gardien : \(error.localizedDescription)") }
    }

    static func veiller(pid: Int32) -> Never {
        while FileManager.default.fileExists(atPath: temoin.path) {
            sleep(1)
            guard kill(pid, 0) != 0, errno == ESRCH else { continue }
            guard FileManager.default.fileExists(atPath: temoin.path) else { break }  // garde levée juste avant
            VerrouSession.verrouiller()
            Journal.app.error("Hoverguard s'est arrêté pendant la garde : session verrouillée par le gardien")
            let relance = Process()
            relance.executableURL = URL(fileURLWithPath: "/usr/bin/open")
            relance.arguments = ["-g", Bundle.main.bundleURL.path]
            try? relance.run()
            relance.waitUntilExit()
            break
        }
        exit(0)
    }
}
