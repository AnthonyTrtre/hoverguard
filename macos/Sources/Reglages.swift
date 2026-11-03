import AppKit
import SocleBarreDeMenus
import SwiftUI

/// Fenêtre de réglages (⌘,) : tout ce que le menu de la barre propose, rangé par onglets, et l'écran « À propos ».
/// Les valeurs vivent dans UserDefaults, sous les mêmes clés que les contrôleurs.
@MainActor
final class FenetreReglages {
    private var fenetre: NSWindow?
    private let garde: ControleurGarde

    init(garde: ControleurGarde) { self.garde = garde }

    func montrer(onglet: Onglet = .general) {
        if fenetre == nil {
            let f = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 560, height: 460),
                             styleMask: [.titled, .closable, .miniaturizable], backing: .buffered, defer: false)
            f.title = "Réglages de Hoverguard"
            f.isReleasedWhenClosed = false
            f.center()
            fenetre = f
        }
        fenetre?.contentView = NSHostingView(rootView: VueReglages(garde: garde, onglet: onglet))
        NSApp.activate()
        fenetre?.makeKeyAndOrderFront(nil)
    }

    enum Onglet: Hashable { case general, agents, alertes, eveil, pro, aPropos }
}

private struct VueReglages: View {
    let garde: ControleurGarde
    @State var onglet: FenetreReglages.Onglet

    var body: some View {
        TabView(selection: $onglet) {
            General(garde: garde).tabItem { Label("Garde", systemImage: "lock.shield") }.tag(FenetreReglages.Onglet.general)
            Agents(garde: garde).tabItem { Label("Agents", systemImage: "person.3") }.tag(FenetreReglages.Onglet.agents)
            Alertes().tabItem { Label("Alertes", systemImage: "bell.badge") }.tag(FenetreReglages.Onglet.alertes)
            Eveil().tabItem { Label("Éveil", systemImage: "bolt") }.tag(FenetreReglages.Onglet.eveil)
            OngletPro().tabItem { Label("Pro", systemImage: "sparkles") }.tag(FenetreReglages.Onglet.pro)
            APropos().tabItem { Label("À propos", systemImage: "info.circle") }.tag(FenetreReglages.Onglet.aPropos)
        }
        .padding(20)
        .frame(width: 560, height: 460)
    }
}

private struct General: View {
    @AppStorage("phrase") private var phrase = ""
    @AppStorage("gardeAutoMinutes") private var gardeAuto = 15
    @AppStorage("rapportAuRetour") private var rapportAuRetour = false
    @AppStorage("intensiteLasers") private var lasers = 0.35
    var garde: ControleurGarde?
    @State private var connexion = OuvertureALaConnexion.active

    var body: some View {
        Form {
            Section("Poser la garde") {
                LabeledContent("Raccourci", value: "⌃⌘W")
                Picker("Toute seule après", selection: $gardeAuto) {
                    Text("Jamais").tag(0)
                    ForEach([5, 10, 15, 30], id: \.self) { Text("\($0) min sans activité").tag($0) }
                }
                Text("Seulement si des agents travaillent.").font(.caption).foregroundStyle(.secondary)
            }
            Section("Au retour") {
                Toggle("Ouvrir le rapport de la garde au déverrouillage", isOn: $rapportAuRetour)
                Text("Sinon, il reste disponible dans le menu : « Rapport de la dernière garde… ».")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Section("Lasers du drone") {
                Picker("Luminosité", selection: $lasers) {
                    Text("Éteints").tag(0.0)
                    Text("Doux").tag(0.35)
                    Text("Normaux").tag(0.7)
                    Text("Vifs").tag(1.0)
                }
                .pickerStyle(.segmented)
                .onChange(of: lasers) { garde?.intensiteLasers = $1 }
            }
            Section("Voile") {
                TextField("Phrase affichée", text: $phrase, prompt: Text("Vide = une phrase drôle au hasard"))
            }
            Section("Lever la garde") {
                Text("Posez le doigt sur Touch ID, ou appuyez sur une touche puis tapez le mot de passe de la session. Échap renvoie le drone surveiller.")
                    .foregroundStyle(.secondary)
            }
            MisesAJour()
            Toggle("Ouvrir Hoverguard à la connexion", isOn: $connexion)
                .onChange(of: connexion) { if $1 != OuvertureALaConnexion.active { OuvertureALaConnexion.basculer() } }
        }
        .formStyle(.grouped)
    }
}

private struct Agents: View {
    let garde: ControleurGarde
    @State private var rafraichir = 0

    var body: some View {
        Form {
            Section("En ce moment") {
                Text(Textes.compteur(principaux: garde.principauxActifs, sousAgents: garde.agents.sousAgents()))
                ForEach(Dictionary(grouping: garde.detectes, by: \.nom).sorted { $0.key < $1.key }, id: \.key) { nom, liste in
                    LabeledContent(nom, value: "\(liste.filter(\.actif).count) au travail sur \(liste.count) ouvert\(liste.count > 1 ? "s" : "")")
                }
                Text("Détection automatique de tous les agents qui tournent sur ce Mac (Claude Code et l'app Claude, Codex, Gemini, Cursor, Aider, OpenCode, Goose…). Les hooks ajoutent le compte des sous-agents. Les sessions dans le cloud ne sont pas visibles d'ici.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Section("Outils suivis") {
                ForEach(BranchementAgents.tous, id: \.nom) { outil in
                    let branche = (try? outil.estBranche(outil.lire())) ?? false
                    HStack {
                        Text(outil.nom)
                        Spacer()
                        if !outil.present {
                            Text("non installé").foregroundStyle(.secondary)
                        } else if branche {
                            Label("branché", systemImage: "checkmark.circle.fill").foregroundStyle(.green)
                            Button("Débrancher") {
                                (NSApp.delegate as? Delegue)?.debrancher(outil)
                                rafraichir += 1
                            }
                        } else {
                            Button("Brancher…") {
                                (NSApp.delegate as? Delegue)?.brancher(outil)
                                rafraichir += 1
                            }
                        }
                    }
                }
                .id(rafraichir)
                Text("Chaque outil prévient Hoverguard par une URL locale `hoverguard://` ; rien ne sort du Mac.")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
    }
}

private struct Alertes: View {
    @AppStorage("photoIntrus") private var photo = false
    @AppStorage("notifications") private var notifications = false
    @AppStorage("destinataire") private var destinataire = ""

    var body: some View {
        Form {
            Section("Intrus") {
                Toggle("Photo par la caméra après 2 mots de passe faux", isOn: $photo)
                Text("Désactivée par défaut. Photographier une personne peut être encadré par la loi (droit à l'image, surveillance au travail) : vérifiez avant d'activer. Photos gardées 30 jours sur ce Mac.")
                    .font(.caption).foregroundStyle(.secondary)
                Button("Ouvrir le dossier des photos") { NSWorkspace.shared.open(Camera.dossier) }
            }
            Section("Notifications sur l'iPhone (iMessage)") {
                Toggle("Activer", isOn: $notifications).disabled(destinataire.isEmpty)
                TextField("Numéro ou adresse iMessage", text: $destinataire, prompt: Text("+33 6 39 98 12 34"))
                Button("Envoyer un message d'essai") {
                    Notifications.envoyer("🛸 Hoverguard : essai réussi, les notifications arrivent bien.", a: destinataire)
                }
                .disabled(destinataire.isEmpty)
                Text("Envoyées depuis Messages sur ce Mac, vers vous-même : quand un agent attend, quand tout est fini, quand quelqu'un se trompe de mot de passe, batterie faible. Au plus une par type toutes les 5 minutes.")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
    }
}

private struct Eveil: View {
    @AppStorage("ecranAllume") private var ecranAllume = false
    @AppStorage("seuilBatterie") private var seuil = 20

    var body: some View {
        Form {
            Section("Garder le Mac éveillé (sans poser la garde)") {
                Text("Choisissez la durée dans le menu de l'icône.").foregroundStyle(.secondary)
                Toggle("Garder aussi l'écran allumé", isOn: $ecranAllume)
                Picker("Arrêter sur batterie", selection: $seuil) {
                    Text("Jamais").tag(0)
                    ForEach([10, 20, 30, 50], id: \.self) { Text("À \($0) %").tag($0) }
                }
            }
            Section("Pendant la garde") {
                Text("Le Mac reste toujours éveillé, et le voile demande d'être branché quand il passe sur batterie.")
                    .foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
    }
}

private struct OngletPro: View {
    @State private var etat = Pro.etat
    @State private var saisie = ""
    @State private var erreur = false

    private let fonctions = [
        ("chart.bar.xaxis", "Tableau de bord des agents", "temps de travail, sessions, sous-agents, chronologie"),
        ("eurosign.circle", "Coût des agents", "jetons et euros par agent et par jour, alerte de budget"),
        ("list.bullet.rectangle", "Journal de l'absence", "ce que les agents ont fait pendant que vous n'étiez pas là"),
        ("bell.and.waves.left.and.right", "Notifications partout", "ntfy, Pushover, Slack, Discord, Telegram, webhook"),
        ("video", "Vidéo de l'intrus", "quelques secondes au lieu d'une photo"),
        ("paintpalette", "Drones, thèmes et sons", "autres modèles, couleurs des lasers, votre logo sur le voile"),
    ]

    var body: some View {
        Form {
            Section {
                switch etat {
                case .gratuit:
                    Text("Hoverguard est gratuit et le restera : toute la protection est dans la version gratuite. Le Pro ajoute de quoi suivre vos agents et personnaliser la garde.")
                case .essai(let jours):
                    Label("Essai Pro : encore \(jours) jour\(jours > 1 ? "s" : "")", systemImage: "sparkles").foregroundStyle(.cyan)
                case .pro(let l):
                    Label("Hoverguard Pro, licence de \(l.email)", systemImage: "checkmark.seal.fill").foregroundStyle(.green)
                case .lancement:
                    Label("Fondateur : Pro offert à vie, vous faites partie des 1 000 premiers 🎉", systemImage: "gift.fill").foregroundStyle(.green)
                }
            }
            Section("Ce que le Pro ajoutera") {
                ForEach(fonctions, id: \.1) { icone, titre, detail in
                    Label { VStack(alignment: .leading) { Text(titre); Text(detail).font(.caption).foregroundStyle(.secondary) } }
                        icon: { Image(systemName: icone) }
                }
                Text("En préparation : les fonctions arrivent avec les prochaines versions.").font(.caption).foregroundStyle(.secondary)
            }
            Section("Licence") {
                HStack {
                    TextField("Collez votre licence (HG1-…)", text: $saisie)
                    Button("Activer") {
                        erreur = !Pro.activer(saisie)
                        if !erreur { saisie = ""; etat = Pro.etat }
                    }
                    .disabled(saisie.isEmpty)
                }
                if erreur { Text("Licence non reconnue. Vérifiez qu'elle est complète.").foregroundStyle(.red) }
                if let achat = Pro.urlAchat { Link("Acheter Hoverguard Pro", destination: achat) }
                if let soutien = Pro.urlSoutien { Link("Soutenir Utahpik", destination: soutien) }
                Text("La licence est vérifiée sur ce Mac, sans connexion internet.").font(.caption).foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
    }
}

private struct MisesAJour: View {
    // Clés lues directement par Sparkle.
    @AppStorage("SUEnableAutomaticChecks") private var verifier = true
    @AppStorage("SUAutomaticallyUpdate") private var installer = true

    var body: some View {
        Section("Mises à jour") {
            Toggle("Vérifier chaque jour s'il existe une nouvelle version", isOn: $verifier)
            Toggle("Installer les mises à jour automatiquement", isOn: $installer).disabled(!verifier)
            Text("Seule connexion de Hoverguard : la liste des versions publiée sur GitHub. Chaque mise à jour est signée par Utahpik et vérifiée avant d'être installée ; jamais pendant une garde.")
                .font(.caption).foregroundStyle(.secondary)
        }
    }
}

private struct APropos: View {
    private var version: String {
        let info = Bundle.main.infoDictionary
        return "\(info?["CFBundleShortVersionString"] as? String ?? "?") (\(info?["CFBundleVersion"] as? String ?? "?"))"
    }

    var body: some View {
        VStack(spacing: 14) {
            Image(nsImage: NSApp.applicationIconImage).resizable().frame(width: 96, height: 96)
            Text("Hoverguard").font(.largeTitle.bold())
            Text("Version \(version)").foregroundStyle(.secondary)
            Text("Un drone garde votre écran pendant que vos agents IA travaillent.")
                .multilineTextAlignment(.center)
            Divider().frame(width: 220)
            Text("Développé par Utahpik").font(.headline)
            Text("© 2026 Utahpik. Hoverguard protège la vue et le clavier, il ne remplace pas le verrouillage de macOS (⌃⌘Q).")
                .font(.caption).foregroundStyle(.secondary).multilineTextAlignment(.center)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}
