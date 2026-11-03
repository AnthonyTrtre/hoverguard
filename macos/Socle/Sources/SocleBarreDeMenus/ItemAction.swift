import AppKit

/// Élément de menu qui exécute une fermeture : `menu.addItem(ItemAction("Quitter", touche: "q") { NSApp.terminate(nil) })`.
@MainActor
public final class ItemAction: NSMenuItem {
    private let geste: @MainActor () -> Void

    public init(_ titre: String, coche: Bool = false, touche: String = "", geste: @escaping @MainActor () -> Void) {
        self.geste = geste
        super.init(title: titre, action: #selector(executer), keyEquivalent: touche)
        target = self
        state = coche ? .on : .off
    }

    required init(coder: NSCoder) { fatalError("non utilisé") }

    @objc private func executer() { geste() }
}
