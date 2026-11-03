import Darwin
import Foundation

/// Détection automatique des agents IA qui tournent sur le Mac, sans rien brancher : chaque processus d'agent connu
/// (Claude Code — y compris les conversations de l'app Claude —, Codex, Gemini, Cursor, Aider…) est repéré, et il
/// compte « au travail » quand lui ou les commandes qu'il lance (compilations, tests) consomment du processeur.
/// Les sessions cloud tournent sur des serveurs distants : elles ne sont pas visibles d'ici.
enum ReglesDetection {
    /// Nom de l'exécutable (ou du script lancé par node / python) → nom affiché de l'agent.
    static let agents: [String: String] = [
        "claude": "Claude", "codex": "Codex", "gemini": "Gemini", "agy": "Antigravity", "cursor-agent": "Cursor",
        "aider": "Aider", "opencode": "OpenCode", "goose": "Goose", "copilot": "Copilot", "amp": "Amp",
        "droid": "Droid", "qwen": "Qwen", "crush": "Crush", "kiro-cli": "Kiro", "cline": "Cline", "openhands": "OpenHands",
    ]

    static let interpretes: Set<String> = ["node", "bun", "deno", "python", "python3", "uv", "uvx"]

    /// L'agent que représente ce processus, ou `nil`. `arguments` ne sert que pour les interprètes (node, python…).
    static func agent(executable: String, arguments: [String]) -> String? {
        // L'app Claude n'est pas un agent : ce sont ses conversations (un processus `claude` chacune) qui comptent.
        if executable.hasSuffix("/Claude.app/Contents/MacOS/Claude") { return nil }
        let nom = (executable as NSString).lastPathComponent.lowercased()
        if let a = agents[nom] { return a }
        guard interpretes.contains(nom) || nom.hasPrefix("python") else { return nil }
        for argument in arguments.dropFirst().prefix(3) where !argument.hasPrefix("-") {
            let script = (argument as NSString).lastPathComponent.lowercased()
                .replacingOccurrences(of: ".js", with: "").replacingOccurrences(of: ".py", with: "")
            if let a = agents[script] { return a }
            // « …/node_modules/@google/gemini-cli/… », « …/@openai/codex/bin/codex.js »
            for (cle, nomAgent) in agents where argument.lowercased().contains("/\(cle)-cli/") || argument.lowercased().contains("/@openai/\(cle)/") {
                return nomAgent
            }
        }
        return nil
    }

    /// Au travail si l'arbre du processus a consommé au moins ce pourcentage d'un cœur depuis la mesure précédente.
    static let seuilActivite = 3.0

    static func pourcentage(tempsCPU: TimeInterval, intervalle: TimeInterval) -> Double {
        intervalle > 0 ? tempsCPU / intervalle * 100 : 0
    }
}

/// Un agent vu à un instant : son processus racine et l'activité de tout son arbre.
struct AgentDetecte: Equatable, Sendable {
    let pid: Int32
    let nom: String
    let actif: Bool
}

/// Relève les processus du système. Appelée hors du fil principal (≈ 700 processus, quelques millisecondes).
final class Detecteur: @unchecked Sendable {
    private var precedent: [Int32: TimeInterval] = [:]
    private var instantPrecedent = Date.distantPast
    private let verrou = NSLock()

    func relever() -> [AgentDetecte] {
        verrou.lock(); defer { verrou.unlock() }
        let maintenant = Date.now
        let intervalle = maintenant.timeIntervalSince(instantPrecedent)
        let pids = Self.tousLesPids()
        var parent: [Int32: Int32] = [:], cpu: [Int32: TimeInterval] = [:], racines: [Int32: String] = [:]
        for pid in pids {
            guard let info = Self.infos(pid) else { continue }
            parent[pid] = info.parent
            cpu[pid] = info.tempsCPU
        }
        for pid in pids {
            guard let chemin = Self.chemin(pid) else { continue }
            let nom = (chemin as NSString).lastPathComponent.lowercased()
            let args = ReglesDetection.interpretes.contains(nom) || nom.hasPrefix("python") ? Self.arguments(pid) : []
            if let agent = ReglesDetection.agent(executable: chemin, arguments: args) { racines[pid] = agent }
        }
        // Un agent lancé par un autre agent du même nom (helpers internes) n'est compté qu'une fois : sa racine.
        racines = racines.filter { pid, nom in
            var p = parent[pid] ?? 0
            while p > 1 {
                if racines[p] == nom { return false }
                p = parent[p] ?? 0
            }
            return true
        }
        // Temps CPU de chaque arbre : la racine et tous ses descendants (outils, compilations).
        var arbre: [Int32: TimeInterval] = [:]
        for (pid, temps) in cpu {
            var p: Int32 = pid
            while p > 1 {
                if racines[p] != nil { arbre[p, default: 0] += temps; break }
                p = parent[p] ?? 0
            }
        }
        defer { precedent = arbre; instantPrecedent = maintenant }
        return racines.map { pid, nom in
            let ecart = max(0, (arbre[pid] ?? 0) - (precedent[pid] ?? (arbre[pid] ?? 0)))
            let actif = intervalle < 120 && ReglesDetection.pourcentage(tempsCPU: ecart, intervalle: intervalle) >= ReglesDetection.seuilActivite
            return AgentDetecte(pid: pid, nom: nom, actif: actif)
        }.sorted { $0.pid < $1.pid }
    }

    private static func tousLesPids() -> [Int32] {
        let n = proc_listallpids(nil, 0)
        guard n > 0 else { return [] }
        var pids = [Int32](repeating: 0, count: Int(n) + 64)
        let lus = proc_listallpids(&pids, Int32(pids.count * MemoryLayout<Int32>.size))
        return Array(pids.prefix(Int(max(lus, 0)))).filter { $0 > 0 }
    }

    private static func chemin(_ pid: Int32) -> String? {
        var tampon = [UInt8](repeating: 0, count: 4 * Int(MAXPATHLEN))
        let n = proc_pidpath(pid, &tampon, UInt32(tampon.count))
        guard n > 0 else { return nil }
        return String(decoding: tampon.prefix(Int(n)), as: UTF8.self)
    }

    private static let echelle: Double = {
        var base = mach_timebase_info_data_t()
        mach_timebase_info(&base)
        return Double(base.numer) / Double(base.denom) / 1_000_000_000
    }()

    private static func infos(_ pid: Int32) -> (parent: Int32, tempsCPU: TimeInterval)? {
        var bsd = proc_bsdinfo()
        guard proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, &bsd, Int32(MemoryLayout<proc_bsdinfo>.size)) > 0 else { return nil }
        var tache = proc_taskinfo()
        let temps: TimeInterval = proc_pidinfo(pid, PROC_PIDTASKINFO, 0, &tache, Int32(MemoryLayout<proc_taskinfo>.size)) > 0
            ? Double(tache.pti_total_user + tache.pti_total_system) * echelle : 0
        return (Int32(bsd.pbi_ppid), temps)
    }

    /// Arguments d'un processus (KERN_PROCARGS2), pour reconnaître `node …/gemini` ou `python -m aider`.
    private static func arguments(_ pid: Int32) -> [String] {
        var mib = [CTL_KERN, KERN_PROCARGS2, pid]
        var taille = 0
        guard sysctl(&mib, 3, nil, &taille, nil, 0) == 0, taille > 0 else { return [] }
        var tampon = [UInt8](repeating: 0, count: taille)
        guard sysctl(&mib, 3, &tampon, &taille, nil, 0) == 0, taille > MemoryLayout<Int32>.size else { return [] }
        let argc = tampon.withUnsafeBytes { $0.load(as: Int32.self) }
        var i = MemoryLayout<Int32>.size
        while i < taille, tampon[i] != 0 { i += 1 }  // chemin de l'exécutable
        while i < taille, tampon[i] == 0 { i += 1 }  // bourrage
        var args: [String] = []
        while args.count < Int(argc), i < taille {
            let debut = i
            while i < taille, tampon[i] != 0 { i += 1 }
            args.append(String(decoding: tampon[debut..<i], as: UTF8.self))
            i += 1
        }
        return args
    }
}
