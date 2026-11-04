import CryptoKit
import Foundation
import SocleEnergie
import Testing
@testable import Hoverguard

@Suite struct FiltreTests {
    func humain(_ g: Entree.Genre, raccourci: Bool = false, commande: Bool = false) -> Entree {
        Entree(pidSource: 0, genre: g, estRaccourci: raccourci, avecCommande: commande)
    }
    func claude(_ g: Entree.Genre) -> Entree { Entree(pidSource: 4242, genre: g) }

    @Test func ceQueFabriquentLesAgentsPasseToujours() {
        for etat in [EtatGarde.libre, .garde, .authentification] {
            for g in [Entree.Genre.touche, .clic, .autre] { #expect(Regles.decider(claude(g), etat: etat) == .laisser) }
        }
    }

    @Test func sousLaGardeUneToucheOuUnClicReveilleLeResteEstBloque() {
        #expect(Regles.decider(humain(.touche), etat: .garde) == .reveiller)
        #expect(Regles.decider(humain(.clic), etat: .garde) == .reveiller)
        #expect(Regles.decider(humain(.autre), etat: .garde) == .bloquer)
        #expect(Regles.decider(humain(.touche, raccourci: true), etat: .garde) == .reveiller)
    }

    @Test func pendantLeMotDePasseRienDuMaterielNAtteintLesApps() {
        // Audit C2 : les touches nourrissent le mot de passe, même si une autre app a le focus.
        #expect(Regles.decider(humain(.touche), etat: .authentification) == .saisir)
        #expect(Regles.decider(humain(.touche, commande: true), etat: .authentification) == .saisir)
        #expect(Regles.decider(humain(.autre), etat: .authentification) == .bloquer)
        #expect(Regles.decider(humain(.clic), etat: .authentification) == .bloquer)
    }

    @Test func touchesDuMotDePasse() {
        #expect(ActionSaisie.depuis(codeTouche: 0, caracteres: "q", avecCommande: false, avecControle: false) == .caractere("q"))
        #expect(ActionSaisie.depuis(codeTouche: 51, caracteres: nil, avecCommande: false, avecControle: false) == .effacer)
        #expect(ActionSaisie.depuis(codeTouche: 36, caracteres: "\r", avecCommande: false, avecControle: false) == .valider)
        #expect(ActionSaisie.depuis(codeTouche: 53, caracteres: nil, avecCommande: false, avecControle: false) == .annuler)
        #expect(ActionSaisie.depuis(codeTouche: 12, caracteres: "q", avecCommande: true, avecControle: false) == nil)
        #expect(ActionSaisie.depuis(codeTouche: 8, caracteres: "\u{3}", avecCommande: false, avecControle: true) == nil)
    }

    @Test func nomDAgentFiltre() {
        #expect(Regles.nomAgentPropre("Claude a1b2") == "Claude a1b2")
        #expect(Regles.nomAgentPropre("Apple : vérifiez http://x.tld\n") == "Apple  vrifiez httpx.tld")
        #expect(Regles.nomAgentPropre("\u{202E}\u{0301}") == "agent")
        #expect(Regles.nomAgentPropre(String(repeating: "a", count: 300)).count == 40)
    }

    @Test func horsGardeSeulLeRaccourciPhysiquePoseLaGarde() {
        #expect(Regles.decider(humain(.touche, raccourci: true), etat: .libre) == .poser)
        #expect(Regles.decider(humain(.touche), etat: .libre) == .laisser)
        #expect(Regles.decider(humain(.clic), etat: .libre) == .laisser)
        #expect(Regles.decider(Entree(pidSource: 4242, genre: .touche, estRaccourci: true), etat: .libre) == .laisser)
    }

    @Test func raccourciLuParCaractereDoncAZERTY() {
        #expect(Regles.estRaccourci(caracteres: "w", controle: true, commande: true, option: false, majuscule: false))
        #expect(Regles.estRaccourci(caracteres: "W", controle: true, commande: true, option: false, majuscule: false))
        #expect(!Regles.estRaccourci(caracteres: "w", controle: false, commande: true, option: false, majuscule: false))
        #expect(!Regles.estRaccourci(caracteres: "w", controle: true, commande: true, option: true, majuscule: false))
        #expect(!Regles.estRaccourci(caracteres: "z", controle: true, commande: true, option: false, majuscule: false))
    }

    @Test func essaisDeMotDePasseGenereux() {
        #expect((1...4).allSatisfy { ReglesGarde.attenteApres(echecs: $0) == nil })
        #expect(ReglesGarde.attenteApres(echecs: 5) == 3)
        #expect(ReglesGarde.attenteApres(echecs: 7) == 9)
        #expect(ReglesGarde.attenteApres(echecs: 50) == 120)
    }

    @Test func laLeveeParTouchIDRepousseLaGardeAuto() {
        #expect(ReglesGarde.inactivite(materielle: 3600, depuisLevee: 1) == 1)
        #expect(ReglesGarde.inactivite(materielle: 30, depuisLevee: .infinity) == 30)
    }

    @Test func motDePasseFauxRefuse() async {
        #expect(await MotDePasse.verifier("certainement-pas-le-bon-\(UUID().uuidString)") == false)
        #expect(await MotDePasse.verifier("") == false)
    }

    @Test func phrasesDrolesEtPolies() {
        #expect(Textes.phrases.count >= 5)
        #expect(Textes.phrases.allSatisfy { !$0.isEmpty && $0.count < 90 })
    }
}

@Suite struct AgentsTests {
    @Test func travailAttenteFin() {
        var s = SuiviAgents()
        let t0 = Date.now
        s.signaler("Claude a1b2", .travail, date: t0)
        s.signaler("Codex", .travail, date: t0)
        #expect(s.actifs(a: t0) == ["Claude a1b2", "Codex"])
        s.signaler("Codex", .attente, date: t0)
        #expect(s.actifs(a: t0) == ["Claude a1b2"] && s.enAttente(a: t0) == ["Codex"])
        s.signaler("Claude a1b2", .fini, date: t0)
        #expect(s.actifs(a: t0).isEmpty)
    }

    @Test func sessionsPlafonnees() {
        var s = SuiviAgents()
        let t0 = Date.now
        for i in 0..<200 { s.signaler("faux \(i)", .travail, date: t0.addingTimeInterval(Double(i))) }
        #expect(s.actifs(a: t0.addingTimeInterval(300)).count == SuiviAgents.maxSessions)
    }

    @Test func unAgentMuetDepuis20MinutesSEteint() {
        var s = SuiviAgents()
        let t0 = Date.now
        s.signaler("Claude", .travail, date: t0)
        #expect(s.actifs(a: t0.addingTimeInterval(19 * 60)) == ["Claude"])
        #expect(s.actifs(a: t0.addingTimeInterval(21 * 60)).isEmpty)
    }

    @Test func sousAgentsComptesAPart() {
        var s = SuiviAgents()
        let t0 = Date.now
        s.signaler("Claude a1", .travail, date: t0)
        for _ in 0..<5 { s.signaler("Claude a1", .sousAgentDebut, date: t0) }
        s.signaler("Claude b2", .sousAgentDebut, date: t0)  // un sous-agent qui démarre réveille sa session
        s.signaler("Claude a1", .sousAgentFin, date: t0)
        #expect(s.actifs(a: t0) == ["Claude a1", "Claude b2"])
        #expect(s.sousAgents(a: t0) == 5)
        #expect(s.total(a: t0) == 7)
        s.signaler("Claude a1", .fini, date: t0)
        #expect(s.total(a: t0) == 2)
        for _ in 0..<3 { s.signaler("Claude b2", .sousAgentFin, date: t0) }
        #expect(s.sousAgents(a: t0) == 0)  // jamais négatif
    }

    @Test func compteurEtEquipe() {
        #expect(Textes.compteur(principaux: 0, sousAgents: 0) == "Aucun agent au travail")
        #expect(Textes.compteur(principaux: 1, sousAgents: 0) == "1 agent au travail")
        #expect(Textes.compteur(principaux: 2, sousAgents: 9) == "11 agents au travail (2 principaux + 9 sous-agents)")
        #expect(Textes.equipe(0).contains("pause café"))
        #expect(Textes.equipe(11).contains("11"))
    }

    @Test func batterie() {
        #expect(Textes.batterie(EtatBatterie(niveau: 80, surSecteur: true)) == nil)
        #expect(Textes.batterie(EtatBatterie(niveau: nil, surSecteur: false)) == nil)
        #expect(Textes.batterie(EtatBatterie(niveau: 64, surSecteur: false))?.contains("64 %") == true)
        #expect(Textes.batterie(EtatBatterie(niveau: 12, surSecteur: false))?.contains("vite") == true)
    }

    @Test func statut() {
        #expect(Textes.statutAgents(actifs: [], enAttente: []) == "Aucun agent en cours")
        #expect(Textes.statutAgents(actifs: ["Claude"], enAttente: []) == "Claude travaille")
        #expect(Textes.statutAgents(actifs: ["A", "B"], enAttente: []) == "2 agents travaillent : A, B")
        #expect(Textes.statutAgents(actifs: [], enAttente: ["Codex"]) == "Codex attend une réponse")
    }
}

@Suite struct OrdresTests {
    @Test func url() {
        #expect(Ordre(url: URL(string: "hoverguard://agent?nom=Claude%20a1b2&etat=travail")!) == .agent(nom: "Claude a1b2", etat: .travail))
        #expect(Ordre(url: URL(string: "hoverguard://agent?etat=fini")!) == .agent(nom: "agent", etat: .fini))
        #expect(Ordre(url: URL(string: "hoverguard://verrouiller")!) == .verrouiller)
        #expect(Ordre(url: URL(string: "hoverguard://agent?nom=C&etat=sous-agent-debut")!) == .agent(nom: "C", etat: .sousAgentDebut))
        #expect(Ordre(url: URL(string: "hoverguard://agent?nom=x&etat=nimporte")!) == nil)
        #expect(Ordre(url: URL(string: "autre://verrouiller")!) == nil)
        #expect(Ordre(url: URL(string: "hoverguard://deverrouiller")!) == nil)  // personne ne lève la garde par URL
    }
}

@Suite struct BranchementTests {
    @Test func ajouteSansToucherAuResteEtSansDoublon() {
        let claude = BranchementAgents.claude
        let avant: [String: Any] = ["model": "opus", "hooks": ["Stop": [["hooks": [["type": "command", "command": "say fini"]]]]]]
        let apres = claude.brancher(claude.brancher(avant))
        #expect(apres["model"] as? String == "opus")
        #expect(claude.estBranche(apres))
        #expect(!claude.estBranche(avant))
        let hooks = apres["hooks"] as? [String: Any]
        #expect((hooks?["Stop"] as? [[String: Any]])?.count == 2)  // le hook existant + celui de Hoverguard, une seule fois
        #expect((hooks?["PreToolUse"] as? [[String: Any]])?.first?["matcher"] as? String == "Agent|Task")
    }

    @Test func groupeMixteGardeLeHookDeLUtilisateur() {
        let claude = BranchementAgents.claude
        let mixte: [String: Any] = ["hooks": ["Stop": [["hooks": [["type": "command", "command": "say fini"],
                                                               ["type": "command", "command": "open hoverguard://x"]]]]]]
        let apres = claude.debrancher(claude.brancher(mixte))
        let stop = (apres["hooks"] as? [String: Any])?["Stop"] as? [[String: Any]]
        #expect(stop?.count == 1)
        #expect(((stop?.first?["hooks"] as? [[String: Any]])?.first?["command"] as? String) == "say fini")
        #expect(!claude.estBranche(apres))
    }

    @Test func ecritureSureDansUnLienSymboliqueAvecDroits() throws {
        let dossier = FileManager.default.temporaryDirectory.appending(path: "hg-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dossier, withIntermediateDirectories: true)
        let vrai = dossier.appending(path: "vrai.json")
        try Data(#"{"env":{"CLE":"secret"}}"#.utf8).write(to: vrai)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: vrai.path)
        let lien = dossier.appending(path: "settings.json")
        try FileManager.default.createSymbolicLink(at: lien, withDestinationURL: vrai)
        let outil = BranchementAgents(nom: "Essai", etiquette: "Essai", fichier: lien, delai: 5, forme: .groupes,
                                      cleSession: "session_id", evenements: [("Stop", .fini, nil, "")])
        try outil.ecrire(outil.brancher(outil.lire()))
        try outil.ecrire(outil.brancher(outil.lire()))
        #expect(try FileManager.default.destinationOfSymbolicLink(atPath: lien.path) == vrai.path)  // lien intact
        #expect(try FileManager.default.attributesOfItem(atPath: vrai.path)[.posixPermissions] as? Int == 0o600)
        #expect(try outil.lire()["env"] != nil)
        let sauvegardes = try FileManager.default.contentsOfDirectory(atPath: dossier.path).filter { $0.contains("avant-hoverguard") }
        #expect(sauvegardes == ["vrai.json.avant-hoverguard"])
    }

    @Test func fichierIllisibleOuPasUnObjetRefuse() throws {
        let f = FileManager.default.temporaryDirectory.appending(path: "hg-\(UUID().uuidString).json")
        let outil = BranchementAgents(nom: "Essai", etiquette: "Essai", fichier: f, delai: 5, forme: .groupes,
                                      cleSession: "session_id", evenements: [])
        try Data("[1, 2]".utf8).write(to: f)
        #expect(throws: BranchementAgents.Erreur.self) { try outil.lire() }
        try Data("{ // commentaire\n }".utf8).write(to: f)
        #expect(throws: BranchementAgents.Erreur.self) { try outil.lire() }
    }

    @Test func formatsCodexGeminiCursor() {
        let codex = BranchementAgents.codex.brancher([:])
        #expect(((codex["hooks"] as? [String: Any])?["PermissionRequest"] as? [[String: Any]])?.first?["matcher"] as? String == "*")
        let gemini = BranchementAgents.gemini.brancher([:])
        let hookGemini = (((gemini["hooks"] as? [String: Any])?["AfterAgent"] as? [[String: Any]])?.first?["hooks"] as? [[String: Any]])?.first
        #expect(hookGemini?["timeout"] as? Int == 5000)  // millisecondes chez Gemini
        #expect((hookGemini?["command"] as? String)?.hasSuffix("echo '{}'") == true)
        let cursor = BranchementAgents.cursor.brancher(["version": 1])
        let stop = ((cursor["hooks"] as? [String: Any])?["stop"] as? [[String: Any]])?.first
        #expect(cursor["version"] as? Int == 1)
        #expect(stop?["hooks"] == nil && (stop?["command"] as? String)?.contains("hoverguard://") == true)
        #expect(BranchementAgents.cursor.estBranche(cursor))
    }

    func urlProduite(_ outil: BranchementAgents, _ etat: SuiviAgents.Etat, entree: String) throws -> String {
        let sortie = Pipe(), stdin = Pipe(), p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/sh")
        // On remplace `open -g` par `echo` pour voir l'URL produite sans rien ouvrir.
        p.arguments = ["-c", outil.commande(etat).replacingOccurrences(of: "/usr/bin/open -g", with: "echo").replacingOccurrences(of: " >/dev/null 2>&1", with: "")]
        p.standardInput = stdin
        p.standardOutput = sortie
        try p.run()
        stdin.fileHandleForWriting.write(Data(entree.utf8))
        try stdin.fileHandleForWriting.close()
        p.waitUntilExit()
        return String(decoding: sortie.fileHandleForReading.readDataToEndOfFile(), as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
    }

    @Test func laCommandeEnvoieLIdentifiantDeSession() throws {
        #expect(try urlProduite(.claude, .travail, entree: #"{"session_id": "a1b2c3d4-xx", "hook_event_name": "UserPromptSubmit"}"#)
                == "hoverguard://agent?nom=Claude%20a1b2&etat=travail")
        #expect(try urlProduite(.cursor, .fini, entree: #"{"conversation_id":"zz99yy","status":"completed"}"#)
                == "hoverguard://agent?nom=Cursor%20zz99&etat=fini")
    }
}

@Suite struct AbsenceTests {
    @Test func rapportDeRetour() {
        let t0 = Date(timeIntervalSince1970: 1_800_000_000)
        var j = JournalAbsence(debut: t0)
        #expect(j.rapport(fin: t0.addingTimeInterval(30)).contains("Personne n'a touché au Mac"))
        j.reveil(t0.addingTimeInterval(600))
        j.motDePasseFaux()
        j.agentTermine("Claude a1")
        j.agentTermine("Claude a1")
        j.sousAgents(4)
        j.sousAgents(2)
        let r = j.rapport(fin: t0.addingTimeInterval(2 * 3600 + 5 * 60))
        #expect(r.contains("Absence : 2 h 05."))
        #expect(r.contains("Agents terminés : Claude a1."))
        #expect(r.contains("Jusqu'à 4 sous-agents"))
        #expect(r.contains("1 fois"))
        #expect(r.contains("Mots de passe faux : 1."))
    }

    @Test func photoALaDeuxiemeErreurPuisToutesLesTrois() {
        var j = JournalAbsence()
        var prises: [Int] = []
        for n in 1...9 { j.motDePasseFaux(); if j.fautPhotographier { prises.append(n) } }
        #expect(prises == [2, 5, 8])
    }

    @Test func alertesEspacees() {
        var l = LimiteurAlertes()
        let t0 = Date.now
        let reponses = [l.autoriser(.intrus, a: t0), l.autoriser(.intrus, a: t0.addingTimeInterval(60)),
                        l.autoriser(.agentAttend, a: t0.addingTimeInterval(60)), l.autoriser(.intrus, a: t0.addingTimeInterval(301))]
        #expect(reponses == [true, false, true, true])
    }
}

@Suite struct LicenceTests {
    /// Licence réelle signée par la clé privée d'Utahpik (script privé), pour un e-mail d'essai.
    let vraie = "HG1-ZXNzYWlAZXhlbXBsZS5mcnxwcm98MjAyNi0xMS0wM3wyMDI3LTExLTAz.SNV8NntuHm9fb5myi5UZbD2N1SuTBBwve4jg-6eJ79Y5GocmcZ63iIe1pQtoLiVmdBennTB87Wh8KuyZZbfmBA"

    @Test func licenceSigneeAccepteeHorsLigne() throws {
        let l = try #require(Licence.lire(vraie))
        #expect(l.email == "essai@exemple.fr" && l.offre == .pro)
        #expect(l.finMisesAJour == Licence.jour("2027-11-03"))
    }

    @Test func licenceModifieeOuInventeeRefusee() {
        // Changer une lettre des données casse la signature : impossible de fabriquer une licence sans la clé privée.
        var morceaux = Array(vraie)
        morceaux[8] = morceaux[8] == "A" ? "B" : "A"
        #expect(Licence.lire(String(morceaux)) == nil)
        #expect(Licence.lire("HG1-abc.def") == nil)
        #expect(Licence.lire("n'importe quoi") == nil)
        let autreCle = Curve25519.Signing.PrivateKey().publicKey.rawRepresentation.base64EncodedString()
        #expect(Licence.lire(vraie, clePublique: autreCle) == nil)
    }

    @Test func essaiDe14JoursPuisGratuit() {
        let t0 = Date(timeIntervalSince1970: 1_800_000_000)
        let version = t0
        #expect(EtatPro.calculer(licence: nil, debutEssai: nil, dateDeVersion: version) == .gratuit)
        #expect(EtatPro.calculer(licence: nil, debutEssai: t0, dateDeVersion: version, maintenant: t0) == .essai(joursRestants: 14))
        #expect(EtatPro.calculer(licence: nil, debutEssai: t0, dateDeVersion: version, maintenant: t0.addingTimeInterval(15 * 86_400)) == .gratuit)
    }

    @Test func lienDActivation() throws {
        let url = try #require(URL(string: "hoverguard://licence?cle=\(vraie)"))
        #expect(Ordre(url: url) == .licence(vraie))
        #expect(Ordre(url: URL(string: "hoverguard://licence?cle=autre")!) == nil)
    }

    @Test func misesAJourCouvertesUnAnSaufFondateur() throws {
        let l = try #require(Licence.lire(vraie))
        #expect(l.couvre(dateDeVersion: Licence.jour("2027-06-01")!))
        #expect(!l.couvre(dateDeVersion: Licence.jour("2028-01-01")!))
        let fondateur = Licence(email: "a@b.c", offre: .fondateur, achat: .now, finMisesAJour: .distantPast)
        #expect(fondateur.couvre(dateDeVersion: .distantFuture))
    }
}

@Suite struct DetectionTests {
    @Test func reconnaitLesAgentsParLeurExecutable() {
        let claudeApp = "/Users/x/Library/Application Support/Claude/claude-code/2.1.309/abc/claude.app/Contents/MacOS/claude"
        #expect(ReglesDetection.agent(executable: claudeApp, arguments: []) == "Claude")
        #expect(ReglesDetection.agent(executable: "/opt/homebrew/bin/codex", arguments: []) == "Codex")
        #expect(ReglesDetection.agent(executable: "/usr/local/bin/cursor-agent", arguments: []) == "Cursor")
        #expect(ReglesDetection.agent(executable: "/Applications/Claude.app/Contents/MacOS/Claude", arguments: []) == nil)
        #expect(ReglesDetection.agent(executable: "/Applications/Safari.app/Contents/MacOS/Safari", arguments: []) == nil)
    }

    @Test func reconnaitLesAgentsLancesParNodeOuPython() {
        #expect(ReglesDetection.agent(executable: "/opt/homebrew/bin/node", arguments: ["node", "/opt/homebrew/bin/gemini"]) == "Gemini")
        #expect(ReglesDetection.agent(executable: "/opt/homebrew/bin/node",
                                      arguments: ["node", "/opt/homebrew/lib/node_modules/@openai/codex/bin/codex.js"]) == "Codex")
        #expect(ReglesDetection.agent(executable: "/usr/bin/python3", arguments: ["python3", "/Users/x/.local/bin/aider"]) == "Aider")
        #expect(ReglesDetection.agent(executable: "/opt/homebrew/bin/node", arguments: ["node", "server.js"]) == nil)
    }

    @Test func seuilDActivite() {
        #expect(ReglesDetection.pourcentage(tempsCPU: 0.5, intervalle: 10) == 5)
        #expect(ReglesDetection.pourcentage(tempsCPU: 0.5, intervalle: 10) >= ReglesDetection.seuilActivite)
        #expect(ReglesDetection.pourcentage(tempsCPU: 0.1, intervalle: 10) < ReglesDetection.seuilActivite)
    }
}

@Suite struct JournauxAgentsTests {
    /// Crée un journal dont la date de modification est « il y a `age` secondes ».
    private func journal(_ racine: URL, _ chemin: String, age: TimeInterval, maintenant: Date) throws {
        let url = racine.appending(path: chemin)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        FileManager.default.createFile(atPath: url.path, contents: Data("{}\n".utf8))
        try FileManager.default.setAttributes([.modificationDate: maintenant.addingTimeInterval(-age)], ofItemAtPath: url.path)
    }

    @Test func conversationsEtSousAgentsAuTravail() throws {
        let maison = FileManager.default.temporaryDirectory.appending(path: "hg-journaux-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: maison) }
        let maintenant = Date(timeIntervalSince1970: 1_793_700_000)
        let p = ".claude/projects/-Users-moi-projet"
        try journal(maison, "\(p)/a.jsonl", age: 10, maintenant: maintenant)               // écrit il y a 10 s : au travail
        try journal(maison, "\(p)/b.jsonl", age: 3_600, maintenant: maintenant)            // au repos
        try journal(maison, "\(p)/b/subagents/agent-1.jsonl", age: 30, maintenant: maintenant)  // b attend son sous-agent
        try journal(maison, "\(p)/b/subagents/agent-2.jsonl", age: 40, maintenant: maintenant)
        try journal(maison, "\(p)/c.jsonl", age: 600, maintenant: maintenant)              // au repos
        let jour = Calendar.current.dateComponents([.year, .month, .day], from: maintenant)
        let codex = String(format: ".codex/sessions/%04d/%02d/%02d", jour.year!, jour.month!, jour.day!)
        try journal(maison, "\(codex)/rollout-1.jsonl", age: 5, maintenant: maintenant)
        try journal(maison, "\(codex)/rollout-2.jsonl", age: 9_000, maintenant: maintenant)

        let r = JournauxAgents.relever(maison: maison, maintenant: maintenant)
        #expect(r.principaux == ["Claude": 2, "Codex": 1])
        #expect(r.sousAgents == 2)
        #expect(r.totalPrincipaux == 3)
    }

    @Test func rienQuandAucunAgentNEstInstalle() {
        let vide = FileManager.default.temporaryDirectory.appending(path: "hg-vide-\(UUID().uuidString)")
        #expect(JournauxAgents.relever(maison: vide) == ReleveJournaux())
    }
}

@MainActor @Suite struct EveilMemoriseTests {
    private func reglages() -> UserDefaults {
        let nom = "hg-eveil-\(UUID().uuidString)"
        return UserDefaults(suiteName: nom)!
    }

    @Test func jusquALArretReprendApresUneRelance() {
        let r = reglages()
        let avant = ControleurEveil(reglages: r)
        avant.activer(.indefinie)
        avant.suspendre()  // l'app quitte pour une mise à jour
        let apres = ControleurEveil(reglages: r)
        apres.restaurer()
        #expect(apres.session?.duree == .indefinie)
        apres.desactiver()
    }

    @Test func arretVolontaireEtSessionFinieNeReprennentPas() {
        let r = reglages()
        let c = ControleurEveil(reglages: r)
        c.activer(.uneHeure)
        c.desactiver()
        let apres = ControleurEveil(reglages: r)
        apres.restaurer()
        #expect(apres.session == nil)

        c.activer(.quinzeMinutes)
        c.suspendre()
        let tard = ControleurEveil(reglages: r)
        tard.restaurer(maintenant: .now.addingTimeInterval(3_600))
        #expect(tard.session == nil)
    }
}

@Suite struct CapotTests {
    @Test func veilleInterditeSaufBatterieBasse() {
        let secteur = EtatBatterie(niveau: 50, surSecteur: true), basse = EtatBatterie(niveau: 15, surSecteur: false)
        #expect(ReglesCapot.doitEmpecherVeille(option: true, batterie: secteur, seuil: 20))
        #expect(!ReglesCapot.doitEmpecherVeille(option: true, batterie: basse, seuil: 20))
        #expect(!ReglesCapot.doitEmpecherVeille(option: false, batterie: secteur, seuil: 20))
    }

    @Test func gardePoseeSeulementALaFermeture() {
        #expect(ReglesCapot.doitPoserGarde(option: true, etaitFerme: false, estFerme: true, etat: .libre))
        #expect(!ReglesCapot.doitPoserGarde(option: true, etaitFerme: true, estFerme: true, etat: .libre))   // levée capot fermé : pas de nouvelle garde
        #expect(!ReglesCapot.doitPoserGarde(option: false, etaitFerme: false, estFerme: true, etat: .libre))
        #expect(!ReglesCapot.doitPoserGarde(option: true, etaitFerme: false, estFerme: true, etat: .garde))
    }

    @Test func regleSudoLimiteeADeuxCommandes() {
        #expect(ReglesCapot.compteValide("anthonytartare"))
        #expect(!ReglesCapot.compteValide("moi ALL=(ALL) ALL"))
        #expect(!ReglesCapot.compteValide("a'b"))
        #expect(ReglesCapot.regleSudo(compte: "moi") == "moi ALL=(root) NOPASSWD: /usr/bin/pmset -a disablesleep 1, /usr/bin/pmset -a disablesleep 0")
    }
}
