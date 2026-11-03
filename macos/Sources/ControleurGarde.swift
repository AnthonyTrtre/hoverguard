import AppKit
import Carbon.HIToolbox
import LocalAuthentication
import SocleCore
import SocleEnergie

/// Garde de l'écran : voile, filtre des entrées physiques, déverrouillage façon Mac en veille, Mac éveillé, agents.
@MainActor
final class ControleurGarde {
    private(set) var etat: EtatGarde = .libre
    private(set) var agents = SuiviAgents()
    /// Agents repérés automatiquement parmi les processus du Mac (sans hooks).
    private(set) var detectes: [AgentDetecte] = []
    private let detecteur = Detecteur()

    /// Agents principaux au travail : le plus grand des deux comptes (détection des processus, signaux des hooks).
    var principauxActifs: Int { max(agents.actifs().count, detectes.filter(\.actif).count) }
    var totalActifs: Int { principauxActifs + agents.sousAgents() }
    let voile = Voile()
    let filtre = Filtre()
    private let reglages: UserDefaults
    private var eveil: AssertionEveil?
    private var minuteur: Timer?
    /// Touch ID armé en silence tant que la garde est posée (aussi pendant la saisie du mot de passe).
    private var contexteDiscret: LAContext?
    private var echecsDiscrets = 0
    private var echecsMotDePasse = 0
    private var phraseDuJour = Textes.phrases[0]
    private var journal: JournalAbsence?
    /// Dernier rapport de garde, consultable depuis le menu (« Rapport de la dernière garde… »).
    private(set) var dernierRapport: JournalAbsence?
    private var finDernierRapport = Date.now
    private var limiteur = LimiteurAlertes()
    var surChangement: () -> Void = {}

    init(reglages: UserDefaults = .standard) {
        self.reglages = reglages
        filtre.etat = { [weak self] in self?.etat ?? .libre }
        filtre.surPoser = { [weak self] in self?.verrouiller() }
        filtre.surReveil = { [weak self] in self?.reveiller() }
        filtre.surPanne = { [weak self] in self?.panneDuFiltre() }
        filtre.surSaisie = { [weak self] in self?.saisir($0) }
    }

    // MARK: Réglages

    /// Phrase choisie par l'utilisateur ; vide = une phrase drôle au hasard à chaque garde.
    var phrasePerso: String {
        get { reglages.string(forKey: "phrase") ?? "" }
        set { reglages.set(newValue, forKey: "phrase"); rafraichirVoile() }
    }

    var phrase: String { phrasePerso.isEmpty ? phraseDuJour : phrasePerso }

    /// Ouvrir le rapport tout seul au déverrouillage (désactivé par défaut).
    var rapportAuRetour: Bool {
        get { reglages.bool(forKey: "rapportAuRetour") }
        set { reglages.set(newValue, forKey: "rapportAuRetour") }
    }

    func montrerDernierRapport() {
        if let dernierRapport { montrerRapport(dernierRapport, fin: finDernierRapport) }
    }

    /// Photo par la caméra après 2 mots de passe faux.
    var photoIntrus: Bool {
        get { reglages.bool(forKey: "photoIntrus") }  // désactivée par défaut (droit à l'image, audit M8)
        set { reglages.set(newValue, forKey: "photoIntrus") }
    }

    /// Notifications iMessage sur l'iPhone (désactivées par défaut).
    var notifications: Bool {
        get { reglages.bool(forKey: "notifications") }
        set { reglages.set(newValue, forKey: "notifications") }
    }

    /// Numéro ou adresse iMessage de l'utilisateur.
    var destinataire: String {
        get { reglages.string(forKey: "destinataire") ?? "" }
        set { reglages.set(newValue.trimmingCharacters(in: .whitespaces), forKey: "destinataire") }
    }

    /// Poser la garde tout seul après N minutes sans toucher au Mac, si des agents travaillent (0 = jamais).
    var gardeAutoMinutes: Int {
        get { reglages.object(forKey: "gardeAutoMinutes") as? Int ?? 15 }
        set { reglages.set(newValue, forKey: "gardeAutoMinutes") }
    }

    /// Notifications seulement pendant la garde : hors garde, l'utilisateur est devant son Mac (audit M4).
    func alerter(_ alerte: Alerte, _ detail: String) {
        guard notifications, etat != .libre, limiteur.autoriser(alerte) else { return }
        Notifications.envoyer(alerte.texte(detail), a: destinataire)
    }

    // MARK: Garde

    private var dernierVerrouillageParURL = Date.distantPast
    /// Garde posée au dernier arrêt de l'app : si elle est encore vraie au lancement, l'app s'est arrêtée en pleine garde.
    private var gardeEnCours: Bool {
        get { reglages.bool(forKey: "gardeEnCours") }
        set { reglages.set(newValue, forKey: "gardeEnCours") }
    }

    func demarrer() {
        if gardeEnCours {
            // Plantage ou arrêt forcé pendant la garde : on ne laisse pas le Mac ouvert (audit M1).
            gardeEnCours = false
            Journal.app.error("Hoverguard s'était arrêté pendant la garde : session verrouillée")
            VerrouSession.verrouiller()
        }
        Camera.purger()
        if Filtre.autorise { filtre.demarrer() }
        // Écran branché, résolution changée, réveil : le voile est reposé sur chaque écran (audit H3).
        for nom in [NSApplication.didChangeScreenParametersNotification] {
            NotificationCenter.default.addObserver(forName: nom, object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.reposerLeVoile() }
            }
        }
        for nom in [NSWorkspace.didWakeNotification, NSWorkspace.screensDidWakeNotification] {
            NSWorkspace.shared.notificationCenter.addObserver(forName: nom, object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.reposerLeVoile() }
            }
        }
        // Agents muets depuis 20 min, batterie, saisie abandonnée : vérifiés toutes les 10 s.
        minuteur = Timer.scheduledTimer(withTimeInterval: 10, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.surveiller() }
        }
    }

    func verrouiller() {
        guard etat == .libre else { return }
        guard Voile.apercu || (Filtre.autorise && filtre.demarrer()) else {
            Filtre.demanderAutorisation()
            return avertir("Autorisation Accessibilité nécessaire",
                           "Sans elle, Hoverguard ne peut pas bloquer le clavier et la souris. Autorise-le dans Réglages Système › Confidentialité et sécurité › Accessibilité, puis réessaie.")
        }
        etat = .garde
        phraseDuJour = Textes.phrases.randomElement() ?? phraseDuJour
        eveil = AssertionEveil(ecranAllume: true, raison: "Hoverguard garde l'écran, les agents travaillent")
        voile.poser()
        rafraichirVoile()
        echecsDiscrets = 0
        journal = JournalAbsence()
        armerTouchIDDiscret()
        if photoIntrus { Task { _ = await Camera.demanderAutorisation() } }  // l'utilisateur est là : bon moment pour demander
        gardeEnCours = !Voile.apercu
        if !Voile.apercu { Pro.demarrerEssaiSiPremiereGarde() }
        Journal.app.info("Garde posée")
        if Voile.apercu {  // l'aperçu ne bloque rien : il se lève tout seul
            DispatchQueue.main.asyncAfter(deadline: .now() + 12) { [weak self] in self?.deverrouiller() }
        }
        surChangement()
    }

    func deverrouiller() {
        guard etat != .libre else { return }
        etat = .libre
        contexteDiscret?.invalidate()
        contexteDiscret = nil
        voile.etat.approche = false
        voile.etat.motDePasse = ""
        voile.retirer()
        eveil = nil
        gardeEnCours = false
        Journal.app.info("Garde levée")
        surChangement()
        if let journal {
            dernierRapport = journal
            finDernierRapport = .now
            // Pas de fenêtre au déverrouillage par défaut (demande d'Anthony) : le rapport attend dans le menu.
            if rapportAuRetour { montrerRapport(journal, fin: finDernierRapport) }
        }
        journal = nil
    }

    /// Comme un Mac en veille : poser le doigt sur Touch ID suffit, sans rien toucher d'autre.
    /// Si le système annule la demande (perte de focus…), on la réarme un peu plus tard.
    private func armerTouchIDDiscret() {
        guard etat != .libre, contexteDiscret == nil else { return }
        let contexte = LAContext()
        contexte.localizedFallbackTitle = ""  // pas de bouton « mot de passe » ici : une touche suffit pour ça
        guard contexte.canEvaluatePolicy(.deviceOwnerAuthenticationWithBiometrics, error: nil) else { return }
        contexteDiscret = contexte
        Task {
            let ok = (try? await contexte.evaluatePolicy(.deviceOwnerAuthenticationWithBiometrics,
                                                         localizedReason: "lever la garde de l'écran")) ?? false
            guard contexteDiscret === contexte else { return }  // remplacé ou annulé entre-temps
            contexteDiscret = nil
            if ok { return deverrouiller() }
            echecsDiscrets += 1
            let delai = min(Double(echecsDiscrets), 3)  // Touch ID toujours prêt à reprendre la main
            DispatchQueue.main.asyncAfter(deadline: .now() + delai) { self.armerTouchIDDiscret() }  // contrôleur vivant tant que l'app tourne
        }
    }

    /// Une touche ou un clic sous la garde : le drone s'approche et demande le mot de passe (Touch ID reste armé).
    private func reveiller() {
        guard etat == .garde else { return }
        etat = .authentification
        journal?.reveil()
        voile.approcher()
        armerTouchIDDiscret()
        surChangement()
    }

    /// Échap ou une minute sans rien taper : le drone retourne surveiller.
    func annulerReveil() {
        guard etat == .authentification else { return }
        etat = .garde
        voile.reculer()
        surChangement()
    }

    /// Touches lues par le filtre pendant la demande (audit C2).
    private func saisir(_ action: ActionSaisie) {
        guard etat == .authentification else { return }
        let e = voile.etat
        e.derniereActivite = .now
        switch action {
        case .caractere(let c): if !e.verification, e.motDePasse.count < 256 { e.motDePasse += c }
        case .effacer: if !e.verification, !e.motDePasse.isEmpty { e.motDePasse.removeLast() }
        case .valider: validerMotDePasse()
        case .annuler: annulerReveil()
        }
    }

    private func validerMotDePasse() {
        let e = voile.etat
        guard etat == .authentification, !e.verification else { return }
        e.verification = true
        e.erreur = nil
        let saisi = e.motDePasse
        Task {
            let ok = await MotDePasse.verifier(saisi)
            e.verification = false
            if ok, etat == .authentification {
                echecsMotDePasse = 0
                return deverrouiller()
            }
            guard !ok else { return }
            // L'échec compte même si quelqu'un a appuyé sur Échap entre-temps (audit M2).
            echecsMotDePasse += 1
            journal?.motDePasseFaux()
            if journal?.fautPhotographier == true { photographierIntrus() }
            guard etat == .authentification else { return }
            e.motDePasse = ""
            e.derniereActivite = .now
            // Essais libres jusqu'à 4 erreurs, puis une attente croissante (3 s, 6 s… 2 min au plus).
            // Touch ID, lui, reste armé et lève la garde quel que soit le nombre d'erreurs.
            if let attente = ReglesGarde.attenteApres(echecs: echecsMotDePasse) {
                e.verification = true
                e.erreur = "Mot de passe incorrect. Patientez \(Int(attente)) s, ou utilisez Touch ID."
                try? await Task.sleep(for: .seconds(attente))
                e.verification = false
            }
            e.erreur = "Mot de passe incorrect"
        }
    }

    private func photographierIntrus() {
        alerter(.intrus, "\(journal?.motsDePasseFaux ?? 0) mots de passe faux")
        guard photoIntrus else { return }
        Task {
            if let url = await Camera.photographier() { journal?.photo(url) }
        }
    }

    private func reposerLeVoile() {
        guard etat != .libre else { return }
        Journal.app.info("Écrans changés : voile reposé")
        voile.poser()
        rafraichirVoile()
    }

    private func surveiller() {
        agents.purger()
        let detecteur = detecteur
        Task.detached(priority: .utility) { [weak self] in
            let releve = detecteur.relever()
            await MainActor.run {
                guard let self else { return }
                self.detectes = releve
                self.rafraichirVoile()
                self.surChangement()
            }
        }
        if etat != .libre {
            // Une app qui active la saisie sécurisée rend le filtre aveugle au clavier ; VoiceOver lit l'écran à voix
            // haute. Dans les deux cas la garde n'est plus garantie : on verrouille la vraie session (audit H2, H5).
            if IsSecureEventInputEnabled() { return garantirParLeSysteme("saisie sécurisée activée par une app") }
            if NSWorkspace.shared.isVoiceOverEnabled { return garantirParLeSysteme("VoiceOver activé") }
        }
        if etat == .authentification, Date.now.timeIntervalSince(voile.etat.derniereActivite) > 60 { annulerReveil() }
        journal?.sousAgents(agents.sousAgents())
        let b = Batterie.lire()
        if etat != .libre, !b.surSecteur, let n = b.niveau, n <= 20 { alerter(.batterieFaible, "Plus que \(n) % de batterie.") }
        if etat == .libre, gardeAutoMinutes > 0, totalActifs > 0, Inactivite.secondes > Double(gardeAutoMinutes) * 60 {
            Journal.app.info("Garde posée automatiquement après \(self.gardeAutoMinutes) min d'inactivité")
            verrouiller()
        }
        rafraichirVoile()
    }

    /// Au retour : ce qui s'est passé pendant l'absence, avec les photos éventuelles.
    private func montrerRapport(_ j: JournalAbsence, fin: Date) {
        let alerte = NSAlert()
        alerte.messageText = "Pendant la dernière garde…"
        alerte.informativeText = j.rapport(fin: fin)
        let images = j.photos.compactMap { NSImage(contentsOf: $0) }
        if !images.isEmpty {
            let pile = NSStackView(views: images.prefix(3).map { image in
                let vue = NSImageView(image: image)
                vue.imageScaling = .scaleProportionallyUpOrDown
                vue.widthAnchor.constraint(equalToConstant: 160).isActive = true
                vue.heightAnchor.constraint(equalToConstant: 120).isActive = true
                return vue
            })
            pile.frame = NSRect(x: 0, y: 0, width: 500, height: 120)
            alerte.accessoryView = pile
            alerte.addButton(withTitle: "OK")
            alerte.addButton(withTitle: "Voir les photos")
        }
        NSApp.activate()
        if alerte.runModal() == .alertSecondButtonReturn { NSWorkspace.shared.open(Camera.dossier) }
    }

    /// Sans filtre, plus rien ne bloque ni ne réveille : on ne lève pas la garde, on verrouille la vraie session
    /// macOS (les agents en ligne de commande continuent, le propriétaire entre avec son mot de passe) (audit M1).
    private func panneDuFiltre() {
        guard etat != .libre else { return }
        garantirParLeSysteme("filtre d'entrées coupé par le système")
    }

    private func garantirParLeSysteme(_ raison: String) {
        Journal.app.error("Garde non garantie (\(raison, privacy: .public)) : session macOS verrouillée")
        VerrouSession.verrouiller()
        journal = nil  // pas de rapport : la session verrouillée prend le relais
        deverrouiller()
    }

    // MARK: Agents

    func recevoir(_ ordre: Ordre) {
        switch ordre {
        case .agent(let nom, let e):
            let avant = totalActifs
            agents.signaler(nom, e)
            if e == .fini, etat != .libre { journal?.agentTermine(nom) }
            if e == .attente { alerter(.agentAttend, "Un agent") }  // jamais le nom reçu par URL dans un iMessage
            if e == .fini, avant > 0, totalActifs == 0 { alerter(.agentsFinis, journal.map { "\($0.agentsTermines.count) terminé(s) pendant ton absence." } ?? "") }
            rafraichirVoile()
            surChangement()
        case .licence(let cle):
            let alerte = NSAlert()
            if Pro.activer(cle), let l = Pro.licence {
                alerte.messageText = "Hoverguard Pro est activé 🎉"
                alerte.informativeText = "Licence de \(l.email). Merci !"
            } else {
                alerte.messageText = "Licence non reconnue"
                alerte.informativeText = "Le lien semble incomplet. Vous pouvez aussi coller la licence dans Réglages › Pro."
            }
            NSApp.activate()
            alerte.runModal()
        case .verrouiller:
            // Une app ou une page peut ouvrir l'URL en boucle pour gêner : une fois par minute au plus (audit M5).
            guard Date.now.timeIntervalSince(dernierVerrouillageParURL) > 60 else { return }
            dernierVerrouillageParURL = .now
            verrouiller()
        }
    }

    /// Lasers : 0 éteints, 0,35 doux (défaut, demande d'Anthony : les lasers vifs fatiguent les yeux), 0,7, 1 vifs.
    var intensiteLasers: Double {
        get { reglages.object(forKey: "intensiteLasers") as? Double ?? 0.35 }
        set { reglages.set(newValue, forKey: "intensiteLasers"); voile.etat.intensiteLasers = newValue }
    }

    private func rafraichirVoile() {
        voile.etat.intensiteLasers = intensiteLasers
        let actifs = principauxActifs, sous = agents.sousAgents()
        voile.etat.phrase = phrase
        voile.etat.agentsActifs = actifs + sous
        voile.etat.compteur = Textes.compteur(principaux: actifs, sousAgents: sous)
        voile.etat.equipe = Textes.equipe(actifs + sous)
        voile.etat.batterie = etat == .libre ? nil : Textes.batterie(Batterie.lire())
    }

    private func avertir(_ titre: String, _ texte: String) {
        let a = NSAlert()
        a.messageText = titre
        a.informativeText = texte
        NSApp.activate()
        a.runModal()
    }
}
