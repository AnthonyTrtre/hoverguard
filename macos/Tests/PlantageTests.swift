import Foundation
import Testing
@testable import Hoverguard

@Suite struct PlantageTests {
    let t0 = Date(timeIntervalSince1970: 1_800_000_000)

    @Test func seulUnRapportHoverguardPlusRecentQueLeDernierVuEstNouveau() {
        let fichiers = [
            (nom: "Hoverguard-ancien.ips", date: t0.addingTimeInterval(-60)),
            (nom: "Hoverguard-1.ips", date: t0.addingTimeInterval(10)),
            (nom: "Hoverguard-2.ips", date: t0.addingTimeInterval(20)),
            (nom: "Google Chrome-3.ips", date: t0.addingTimeInterval(30)),
            (nom: "Hoverguard-4.diag", date: t0.addingTimeInterval(40)),
        ]
        #expect(RapportPlantage.nouveau(fichiers, dernierVu: t0)?.nom == "Hoverguard-2.ips")
        #expect(RapportPlantage.nouveau(fichiers, dernierVu: t0.addingTimeInterval(20)) == nil)
        #expect(RapportPlantage.nouveau([], dernierVu: t0) == nil)
    }

    static let ips = """
    {"app_name":"Hoverguard","app_version":"1.1.1","bug_type":"309"}
    {"exception":{"type":"EXC_BREAKPOINT","signal":"SIGTRAP"},"termination":{"namespace":"SIGNAL","indicator":"Trace/BPT trap: 5"},\
    "asi":{"libswiftCore.dylib":["Fatal error: Unexpectedly found nil"]},"faultingThread":1,\
    "usedImages":[{"name":"Hoverguard"},{"name":"libswiftCore.dylib"}],\
    "threads":[{"frames":[]},{"queue":"com.apple.main-thread","frames":[\(
        (0..<20).map { #"{"imageIndex":\#($0 % 2),"symbol":"f\#($0)","symbolLocation":4}"# }.joined(separator: ",")),\
    {"imageIndex":0,"imageOffset":99}]}]}
    """

    @Test func extraitGardeExceptionFinMessageEtQuinzeLignesDuFilFautif() {
        let e = RapportPlantage.extrait(ips: Self.ips)
        #expect(e.contains("Version au plantage : 1.1.1"))
        #expect(e.contains("Exception : EXC_BREAKPOINT SIGTRAP"))
        #expect(e.contains("Fin : SIGNAL Trace/BPT trap: 5"))
        #expect(e.contains("Message : Fatal error: Unexpectedly found nil"))
        #expect(e.contains("Fil fautif 1 (com.apple.main-thread) :"))
        #expect(e.contains("0  Hoverguard  f0 + 4"))
        #expect(e.contains("13  libswiftCore.dylib  f13 + 4"))
        #expect(e.contains("14  Hoverguard  f14 + 4"))
        #expect(!e.contains("f15"))
    }

    @Test func unRapportIllisibleDonneSonDebut() {
        #expect(RapportPlantage.extrait(ips: "pas du json") == "pas du json")
    }

    @Test func anonymisationDuDossierDeLUtilisateurEtDeLaMachine() {
        let texte = "/Users/jdupont/Applications/Hoverguard.app sur MacBook-Pro-de-Jean.local, compte jdupont (Jean Dupont)"
        let a = RapportPlantage.anonymiser(texte, dossierPersonnel: "/Users/jdupont", utilisateurs: ["jdupont", "Jean Dupont"],
                                           machines: ["MacBook-Pro-de-Jean.local", "MacBook-Pro-de-Jean"])
        #expect(a == "~/Applications/Hoverguard.app sur <machine>, compte <utilisateur> (<utilisateur>)")
        // Un nom trop court n'est pas remplacé partout.
        #expect(RapportPlantage.anonymiser("abc", dossierPersonnel: "", utilisateurs: ["a"], machines: [""]) == "abc")
    }

    @Test func urlDeLIssuePreRemplie() throws {
        let url = RapportPlantage.url(version: "1.1.2", systeme: "macOS 26.6", architecture: "arm64", extrait: "Exception : SIGTRAP")
        let c = try #require(URLComponents(url: url, resolvingAgainstBaseURL: false))
        #expect(c.host == "github.com" && c.path == "/AnthonyTrtre/hoverguard/issues/new")
        let q = Dictionary(uniqueKeysWithValues: (c.queryItems ?? []).map { ($0.name, $0.value ?? "") })
        #expect(q["labels"] == "plantage")
        #expect(q["title"] == "Plantage de Hoverguard 1.1.2")
        #expect(q["body"]?.contains("**Version** : 1.1.2") == true)
        #expect(q["body"]?.contains("Exception : SIGTRAP") == true)
        #expect(q["body"]?.contains(RapportPlantage.tronque) == false)
    }

    @Test func unRapportTropLongEstTronque() throws {
        let long = String(repeating: "ligne é + & = #\n", count: 2000)
        let url = RapportPlantage.url(version: "1.1.2", systeme: "macOS", architecture: "arm64", extrait: long)
        #expect(url.absoluteString.count <= RapportPlantage.tailleMaxURL)
        let corps = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems?.first { $0.name == "body" }?.value
        #expect(corps?.contains(RapportPlantage.tronque) == true)
        #expect(corps?.contains("ligne é + & = #") == true)
    }
}
