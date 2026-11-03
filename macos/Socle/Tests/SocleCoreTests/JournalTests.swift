import Testing
@testable import SocleCore

@Test func journalDisponible() {
    Journal.app.debug("test")
    #expect(Bool(true))
}
