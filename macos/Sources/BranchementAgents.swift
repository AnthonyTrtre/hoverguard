import Foundation

/// Branche les agents en ligne de commande à Hoverguard par leurs « hooks » : chaque début de travail, attente, fin et
/// sous-agent lance `open -g "hoverguard://agent?…"`. Ne touche qu'aux entrées qui contiennent `hoverguard://` ; une
/// copie de sauvegarde du fichier est faite avant d'écrire. Formats vérifiés dans les documentations officielles
/// (2026-11-03) : docs.claude.com, learn.chatgpt.com/docs/hooks, geminicli.com/docs/hooks, cursor.com/docs/hooks.
struct BranchementAgents: Sendable {
    enum Forme: Sendable {
        /// `{"hooks": {Evenement: [{"matcher"?, "hooks": [{type, command}]}]}}` (Claude Code, Codex, Gemini CLI).
        case groupes
        /// `{"version": 1, "hooks": {evenement: [{command}]}}` (Cursor).
        case plate
    }

    let nom: String
    /// Nom court montré sur le voile, suivi de 4 caractères de la session (« Claude a1b2 »).
    let etiquette: String
    let fichier: URL
    /// Délai maximal du hook, dans l'unité de l'outil (secondes, millisecondes pour Gemini CLI).
    let delai: Int
    let forme: Forme
    /// Champ de l'entrée standard qui identifie la session (4 caractères affichés sur le voile).
    let cleSession: String
    let evenements: [(nom: String, etat: SuiviAgents.Etat, matcher: String?, sortie: String)]

    static let claude = BranchementAgents(
        nom: "Claude Code", etiquette: "Claude", fichier: URL.homeDirectory.appending(path: ".claude/settings.json"), delai: 5, forme: .groupes,
        cleSession: "session_id", evenements: [
            ("UserPromptSubmit", .travail, nil, ""), ("Notification", .attente, nil, ""), ("Stop", .fini, nil, ""),
            ("PreToolUse", .sousAgentDebut, "Agent|Task", ""), ("SubagentStop", .sousAgentFin, nil, ""),
        ])

    /// Codex demande d'approuver les nouveaux hooks une fois (`/hooks` dans Codex). Sa sortie standard sur
    /// UserPromptSubmit part dans le contexte du modèle : la commande ne doit rien écrire.
    static let codex = BranchementAgents(
        nom: "Codex", etiquette: "Codex", fichier: URL.homeDirectory.appending(path: ".codex/hooks.json"), delai: 5, forme: .groupes,
        cleSession: "session_id", evenements: [
            ("UserPromptSubmit", .travail, nil, ""), ("PermissionRequest", .attente, "*", ""), ("Stop", .fini, nil, ""),
            ("SubagentStart", .sousAgentDebut, nil, ""), ("SubagentStop", .sousAgentFin, nil, ""),
        ])

    /// Gemini CLI attend un objet JSON (et rien d'autre) sur la sortie standard.
    static let gemini = BranchementAgents(
        nom: "Gemini CLI", etiquette: "Gemini", fichier: URL.homeDirectory.appending(path: ".gemini/settings.json"), delai: 5000, forme: .groupes,
        cleSession: "session_id", evenements: [
            ("BeforeAgent", .travail, "*", "echo '{}'"), ("Notification", .attente, "*", "echo '{}'"),
            ("AfterAgent", .fini, "*", "echo '{}'"),
        ])

    /// Cursor n'a pas d'événement « attente d'approbation » : seulement travail, fin et sous-agents.
    static let cursor = BranchementAgents(
        nom: "Cursor", etiquette: "Cursor", fichier: URL.homeDirectory.appending(path: ".cursor/hooks.json"), delai: 5, forme: .plate,
        cleSession: "conversation_id", evenements: [
            ("beforeSubmitPrompt", .travail, nil, #"echo '{"continue":true}'"#), ("stop", .fini, nil, "echo '{}'"),
            ("subagentStart", .sousAgentDebut, nil, "echo '{}'"), ("subagentStop", .sousAgentFin, nil, "echo '{}'"),
        ])

    static let tous = [claude, codex, gemini, cursor]

    /// L'outil semble installé (son dossier de réglages existe).
    var present: Bool { FileManager.default.fileExists(atPath: fichier.deletingLastPathComponent().path) }

    /// Chemins absolus : un `PATH` piégé ne peut pas remplacer sed, cut ou open (audit B7).
    func commande(_ etat: SuiviAgents.Etat, sortie: String = "") -> String {
        let id = #"id=$(/usr/bin/sed -n 's/.*"\#(cleSession)" *: *"\([^"]*\)".*/\1/p' | /usr/bin/cut -c1-4); "#
        let ouvrir = #"/usr/bin/open -g "hoverguard://agent?nom=\#(etiquette)%20${id}&etat=\#(etat.rawValue)" >/dev/null 2>&1"#
        return id + ouvrir + (sortie.isEmpty ? "" : "; " + sortie)
    }

    private static func estDeHoverguard(_ hook: [String: Any]) -> Bool {
        (hook["command"] as? String)?.contains("hoverguard://") == true
    }

    private func entrees(_ hooks: [String: Any], _ evenement: String) -> [[String: Any]] {
        hooks[evenement] as? [[String: Any]] ?? []
    }

    private func contientHoverguard(_ entree: [String: Any]) -> Bool {
        switch forme {
        case .groupes: (entree["hooks"] as? [[String: Any]] ?? []).contains(where: Self.estDeHoverguard)
        case .plate: Self.estDeHoverguard(entree)
        }
    }

    /// Retire seulement les hooks de Hoverguard ; un groupe qui contenait aussi un hook de l'utilisateur est gardé
    /// (audit M6). Un groupe vidé disparaît.
    private func sansHoverguard(_ liste: [[String: Any]]) -> [[String: Any]] {
        switch forme {
        case .plate:
            return liste.filter { !Self.estDeHoverguard($0) }
        case .groupes:
            return liste.compactMap { groupe in
                guard let hooks = groupe["hooks"] as? [[String: Any]], hooks.contains(where: Self.estDeHoverguard) else { return groupe }
                let restants = hooks.filter { !Self.estDeHoverguard($0) }
                guard !restants.isEmpty else { return nil }
                var copie = groupe
                copie["hooks"] = restants
                return copie
            }
        }
    }

    func estBranche(_ reglages: [String: Any]) -> Bool {
        guard let hooks = reglages["hooks"] as? [String: Any] else { return false }
        return evenements.allSatisfy { e in entrees(hooks, e.nom).contains(where: contientHoverguard) }
    }

    /// Renvoie les réglages avec les hooks de Hoverguard (remplacés s'ils existaient déjà), le reste intact.
    func brancher(_ reglages: [String: Any]) -> [String: Any] {
        var resultat = reglages
        var hooks = reglages["hooks"] as? [String: Any] ?? [:]
        for e in evenements {
            var liste = sansHoverguard(entrees(hooks, e.nom))
            let hook: [String: Any] = ["type": "command", "command": commande(e.etat, sortie: e.sortie), "timeout": delai]
            switch forme {
            case .groupes:
                var groupe: [String: Any] = ["hooks": [hook]]
                if let matcher = e.matcher { groupe["matcher"] = matcher }
                liste.append(groupe)
            case .plate:
                liste.append(["command": hook["command"]!, "timeout": delai])
            }
            hooks[e.nom] = liste
        }
        if case .plate = forme, resultat["version"] == nil { resultat["version"] = 1 }
        resultat["hooks"] = hooks
        return resultat
    }

    /// Retire tous les hooks de Hoverguard (le reste du fichier est intact).
    func debrancher(_ reglages: [String: Any]) -> [String: Any] {
        guard var hooks = reglages["hooks"] as? [String: Any] else { return reglages }
        for e in evenements {
            let liste = sansHoverguard(entrees(hooks, e.nom))
            hooks[e.nom] = liste.isEmpty ? nil : liste
        }
        var resultat = reglages
        resultat["hooks"] = hooks
        return resultat
    }

    enum Erreur: LocalizedError {
        case pasUnObjet, illisible(String)
        var errorDescription: String? {
            switch self {
            case .pasUnObjet: "Le fichier n'a pas la forme attendue (un objet JSON) : Hoverguard n'y touche pas."
            case .illisible(let detail): "Le fichier n'est pas du JSON lisible (commentaires ?) : \(detail). Hoverguard n'y touche pas."
            }
        }
    }

    /// Le vrai fichier, même si le chemin est un lien symbolique (dotfiles) : on écrit dans la cible (audit M6).
    private var cible: URL { fichier.resolvingSymlinksInPath() }

    func lire() throws -> [String: Any] {
        guard FileManager.default.fileExists(atPath: cible.path) else { return [:] }
        let objet: Any
        do {
            objet = try JSONSerialization.jsonObject(with: Data(contentsOf: cible))
        } catch {
            throw Erreur.illisible(error.localizedDescription)
        }
        guard let dictionnaire = objet as? [String: Any] else { throw Erreur.pasUnObjet }
        return dictionnaire
    }

    /// Une seule sauvegarde (`….avant-hoverguard`), mêmes droits que l'original (souvent 600 : il peut contenir des
    /// clés) (audit M6).
    func ecrire(_ reglages: [String: Any]) throws {
        let fm = FileManager.default
        let droits = (try? fm.attributesOfItem(atPath: cible.path)[.posixPermissions] as? Int) ?? 0o600
        if fm.fileExists(atPath: cible.path) {
            let copie = cible.deletingLastPathComponent().appending(path: cible.lastPathComponent + ".avant-hoverguard")
            try? fm.removeItem(at: copie)
            try fm.copyItem(at: cible, to: copie)
            try fm.setAttributes([.posixPermissions: droits], ofItemAtPath: copie.path)
        }
        try fm.createDirectory(at: cible.deletingLastPathComponent(), withIntermediateDirectories: true)
        let donnees = try JSONSerialization.data(withJSONObject: reglages, options: [.prettyPrinted, .sortedKeys, .withoutEscapingSlashes])
        try donnees.write(to: cible, options: .atomic)
        try fm.setAttributes([.posixPermissions: droits], ofItemAtPath: cible.path)
    }
}
