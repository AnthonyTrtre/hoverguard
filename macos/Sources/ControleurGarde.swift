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
    /// Conversations au travail d'après leurs journaux (Claude, Codex, Gemini), sous-agents compris.
    private(set) var journaux = ReleveJournaux()

    /// Agents principaux au travail : le plus grand des comptes (processus, journaux des conversations, hooks).
    var principauxActifs: Int { max(agents.actifs().count, detectes.filter(\.actif).count, journaux.totalPrincipaux) }
    var sousAgentsActifs: Int { max(agents.sousAgents(), journaux.sousAgents) }
    var totalActifs: Int { principauxActifs + sousAgentsActifs }
    let voile = Voile()
    let filtre = Filtre()
    private let reglages: UserDefaults
    private var eveil: AssertionEveil?
    private var minuteur: Timer?
    /// Touch ID armé en silence tant que la garde est posée (aussi pendant la saisie du mot de passe).
    private var contexteDiscret: LAContext?
    private var reessaiTouchIDPrevu = false
    private var rearmementPrevu: DispatchWorkItem?
    /// Changement d'alimentation (branché, débranché, niveau) : le message de batterie du voile suit tout de suite.
    private var sourceAlimentation: CFRunLoopSource?
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

    /// Poser la garde tout seule même sans agent au travail (comme un économiseur d'écran). Activé par défaut.
    var gardeAutoSansAgent: Bool { reglages.object(forKey: "gardeAutoSansAgent") as? Bool ?? true }

    /// « Pas de garde automatique jusqu'à la prochaine garde » (vidéo, lecture…) : levé à la prochaine garde ou au
    /// verrouillage de la session macOS. Pas retenu au relancement de l'app.
    var gardeAutoSuspendue = false

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
    /// Touch ID lève la garde sans toucher au clavier ni à la souris : sans ce repère, l'inactivité matérielle dépasse
    /// déjà le seuil et la garde automatique revenait dans les 10 s.
    private var derniereLevee = Date.distantPast
    /// Garde posée au dernier arrêt de l'app : si elle est encore vraie au lancement, l'app s'est arrêtée en pleine garde.
    private var gardeEnCours: Bool {
        get { reglages.bool(forKey: "gardeEnCours") }
        set {
            reglages.set(newValue, forKey: "gardeEnCours")
            Gardien.poserTemoin(newValue)
        }
    }

    private func ecouterAlimentation() {
        guard sourceAlimentation == nil else { return }
        let contexte = Unmanaged.passUnretained(self).toOpaque()  // contrôleur vivant tant que l'app tourne
        guard let source = IOPSNotificationCreateRunLoopSource({ contexte in
            guard let contexte else { return }
            let moi = Unmanaged<ControleurGarde>.fromOpaque(contexte).takeUnretainedValue()
            MainActor.assumeIsolated { moi.rafraichirVoile() }
        }, contexte)?.takeRetainedValue() else { return }
        CFRunLoopAddSource(CFRunLoopGetMain(), source, .defaultMode)
        sourceAlimentation = source
    }

    func demarrer() {
        ecouterAlimentation()
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
        gardeAutoSuspendue = false
        phraseDuJour = Textes.phrases.randomElement() ?? phraseDuJour
        eveil = AssertionEveil(ecranAllume: true, raison: "Hoverguard garde l'écran, les agents travaillent")
        voile.poser()
        rafraichirVoile()
        echecsDiscrets = 0
        journal = JournalAbsence()
        armerTouchIDDiscret()
        if photoIntrus { Task { _ = await Camera.demanderAutorisation() } }  // l'utilisateur est là : bon moment pour demander
        gardeEnCours = !Voile.apercu
        if gardeEnCours { Gardien.lancer() }
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
        derniereLevee = .now
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
        var indisponible: NSError?
        guard contexte.canEvaluatePolicy(.deviceOwnerAuthenticationWithBiometrics, error: &indisponible) else {
            // Capteur indisponible (capot fermé, clavier Touch ID débranché) : on réessaie jusqu'à son retour.
            guard !reessaiTouchIDPrevu else { return }
            Journal.app.notice("Touch ID indisponible (\(indisponible?.code ?? 0, privacy: .public)) : nouvel essai dans 2 s")
            reessaiTouchIDPrevu = true
            DispatchQueue.main.asyncAfter(deadline: .now() + 2) {
                self.reessaiTouchIDPrevu = false
                self.armerTouchIDDiscret()
            }
            return
        }
        contexteDiscret = contexte
        Journal.app.notice("Touch ID armé")
        Task {
            var ok = false, code = 0
            do {
                ok = try await contexte.evaluatePolicy(.deviceOwnerAuthenticationWithBiometrics,
                                                       localizedReason: "lever la garde de l'écran")
            } catch { code = (error as NSError).code }
            guard contexteDiscret === contexte else { return }  // remplacé ou annulé entre-temps
            contexteDiscret = nil
            // Codes LAError (jamais de donnée personnelle) : -4 annulé par le système, -8 verrouillé, -6 indisponible…
            Journal.app.notice("Touch ID : \(ok ? "réussi" : "échec", privacy: .public) (\(code, privacy: .public))")
            if ok { return deverrouiller() }
            echecsDiscrets += 1
            let delai = min(Double(echecsDiscrets), 3)  // Touch ID toujours prêt à reprendre la main
            DispatchQueue.main.asyncAfter(deadline: .now() + delai) { self.armerTouchIDDiscret() }  // contrôleur vivant tant que l'app tourne
        }
    }

    /// Capot rouvert, écran réveillé : la demande Touch ID en cours a pu être annulée ou rester bloquée pendant que le
    /// capteur était coupé. On la remplace par une neuve.
    /// Capot rouvert, macOS envoie trois signaux à la suite (réveil, session déverrouillée, écrans) : réarmer à chacun
    /// annulait la demande juste lancée et la dernière restait bloquée, sourde au doigt. Un seul réarmement, 1,5 s après
    /// le dernier signal.
    func rearmerTouchID() {
        guard etat != .libre else { return }
        rearmementPrevu?.cancel()
        let travail = DispatchWorkItem { [weak self] in
            guard let self, self.etat != .libre else { return }
            Journal.app.notice("Touch ID réarmé (réveil, capot ou session déverrouillée)")
            self.contexteDiscret?.invalidate()
            self.contexteDiscret = nil
            self.armerTouchIDDiscret()
        }
        rearmementPrevu = travail
        DispatchQueue.main.asyncAfter(deadline: .now() + 1.5, execute: travail)
    }

    /// Une touche ou un clic sous la garde : le drone s'approche et demande le mot de passe (Touch ID reste armé).
    private func reveiller() {
        guard etat == .garde else { return }
        etat = .authentification
        journal?.reveil()
        voile.approcher()
        armerTouchIDDiscret()
        // Clavier Touch ID sans fil endormi (Magic Keyboard au bureau) : il ne se reconnecte qu'à la première touche et
        // son capteur n'est prêt que quelques secondes après. La demande lancée avant ne l'entend pas : le 13/11, aucun
        // doigt reçu pendant 13 min. On la renouvelle une fois le clavier revenu, puis une fois son capteur prêt.
        for delai in [2.0, 10.0] {
            DispatchQueue.main.asyncAfter(deadline: .now() + delai) { [weak self] in
                guard let self, self.etat == .authentification else { return }
                self.rearmerTouchID()
            }
        }
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
            let journaux = JournauxAgents.relever()
            await MainActor.run {
                guard let self else { return }
                self.detectes = releve
                self.journaux = journaux
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
        journal?.sousAgents(sousAgentsActifs)
        if etat != .libre { rafraichirVoile() }  // batterie à jour même si le relevé des agents tarde
        let b = Batterie.lire()
        if etat != .libre, !b.surSecteur, let n = b.niveau, n <= 20 { alerter(.batterieFaible, "Plus que \(n) % de batterie.") }
        if etat == .libre, !gardeAutoSuspendue, gardeAutoMinutes > 0, totalActifs > 0 || gardeAutoSansAgent, ReglesGarde.inactivite(materielle: Inactivite.secondes, depuisLevee: Date.now.timeIntervalSince(derniereLevee)) > Double(gardeAutoMinutes) * 60 {
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
        let actifs = principauxActifs, sous = sousAgentsActifs
        voile.etat.phrase = phrase
        voile.etat.agentsActifs = actifs + sous
        voile.etat.compteur = ""  // compteur d'agents retiré de l'affichage (peu fiable)
        voile.etat.equipe = ""
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
