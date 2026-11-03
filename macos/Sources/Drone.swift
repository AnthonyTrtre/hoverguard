import SceneKit
import SwiftUI
import AppKit
import QuartzCore
import simd

/// Drone procédural sur fond transparent ; une scène indépendante par écran.
@MainActor
struct VueDrone: NSViewRepresentable {
    let enVol: Bool
    let approche: Bool
    /// Écran principal : rendu complet. Autres écrans : sans ombres ni occlusion, 20 images/s (mesuré : 84 % d'un
    /// cœur avec trois écrans en rendu complet).
    var principal = true
    /// Luminosité des lasers et du balayage, de 0 (éteints) à 1 (vifs). Réglable par l'utilisateur.
    var intensiteLasers: Double = 0.35

    func makeCoordinator() -> Scene3D { Scene3D() }

    func makeNSView(context: Context) -> SCNView {
        let vue = VueScene()
        let scene = context.coordinator
        vue.scene = scene.scene
        vue.pointOfView = scene.camera
        vue.backgroundColor = .clear
        vue.antialiasingMode = principal ? .multisampling4X : .multisampling2X
        vue.preferredFramesPerSecond = principal ? 30 : 20
        if !principal {
            scene.scene.rootNode.enumerateHierarchy { noeud, _ in noeud.light?.castsShadow = false }
            scene.camera.camera?.screenSpaceAmbientOcclusionIntensity = 0
        }
        vue.rendersContinuously = true
        vue.isPlaying = true
        vue.allowsCameraControl = false
        vue.redimensionner = { [weak scene] taille in scene?.cadrer(taille) }
        scene.actualiser(enVol: enVol, approche: approche, anime: false)
        scene.reglerLasers(intensiteLasers)
        return vue
    }

    func updateNSView(_ vue: SCNView, context: Context) {
        context.coordinator.cadrer(vue.bounds.size)
        context.coordinator.actualiser(enVol: enVol, approche: approche, anime: true)
        context.coordinator.reglerLasers(intensiteLasers)
    }

    static func dismantleNSView(_ vue: SCNView, coordinator: Scene3D) {
        (vue as? VueScene)?.redimensionner = nil
        vue.isPlaying = false
        vue.rendersContinuously = false
        vue.scene = nil
    }

    @MainActor
    final class Scene3D {
        let scene = SCNScene()
        let camera = SCNNode()
        private let drone = Drone()
        private let identification = SCNNode()
        private var rapport: CGFloat = 0
        private var approcheActive = false

        init() {
            scene.background.contents = NSColor.clear
            scene.rootNode.addChildNode(drone.racine)
            scene.rootNode.addChildNode(drone.sol)
            scene.rootNode.addChildNode(drone.ciblesAeriennes)
            let optique = SCNCamera()
            optique.usesOrthographicProjection = false
            optique.projectionDirection = .vertical
            optique.fieldOfView = 36
            optique.zNear = 0.08
            optique.zFar = 100
            optique.wantsHDR = true
            optique.wantsExposureAdaptation = false
            optique.bloomIntensity = 0.12
            optique.bloomThreshold = 1.5
            optique.bloomBlurRadius = 3
            optique.screenSpaceAmbientOcclusionIntensity = 0.65
            optique.screenSpaceAmbientOcclusionRadius = 0.16
            optique.screenSpaceAmbientOcclusionBias = 0.012
            optique.screenSpaceAmbientOcclusionDepthThreshold = 0.12
            optique.screenSpaceAmbientOcclusionNormalThreshold = 0.3
            // Budget GPU réservé aux ombres, à l'occlusion et au reflet sur plusieurs écrans.
            optique.wantsDepthOfField = false
            optique.vignettingIntensity = 0
            camera.camera = optique
            camera.position = SCNVector3(4.6, 1.45, 8.6)
            camera.look(at: SCNVector3(0, -1.05, 0))
            scene.rootNode.addChildNode(camera)
            // Le brouillard atténue les géométries lointaines sans créer de fond opaque.
            scene.fogColor = NSColor(white: 0.015, alpha: 0)
            scene.fogStartDistance = 18
            scene.fogEndDistance = 45
            scene.fogDensityExponent = 1.4
            scene.lightingEnvironment.contents = Fabrique.environnementStudio
            scene.lightingEnvironment.intensity = 0.85
            construireEclairageStudio()
            construireIdentification()
            cadrer(CGSize(width: 16, height: 10))
        }

        func cadrer(_ taille: CGSize) {
            guard taille.width > 0, taille.height > 0 else { return }
            let nouveau = taille.width / taille.height
            guard abs(nouveau - rapport) > 0.001 else { return }
            rapport = nouveau
            // Champ vertical constant du portable à l'ultra-large ; adaptation si écran portrait.
            let recul = max(1, 1.15 / rapport)
            camera.position = SCNVector3(4.6 * recul, -1.05 + 2.5 * recul, 8.6 * recul)
            camera.look(at: SCNVector3(0, -1.05, 0))
            identification.scale.x = rapport
            drone.cadrer(camera: camera, rapport: rapport)
        }

        func reglerLasers(_ niveau: Double) { drone.reglerLasers(niveau) }

        func actualiser(enVol: Bool, approche: Bool, anime: Bool) {
            // Une demande d'approche entraîne le décollage même depuis la veille.
            drone.actualiser(enVol: enVol, approche: approche, camera: camera, anime: anime)
            guard approcheActive != approche else { return }
            approcheActive = approche
            identification.removeAllActions()
            identification.isHidden = !approche
            if approche {
                identification.position.y = 0.40
                identification.runAction(.sequence([
                    .wait(duration: anime ? 0.8 : 0),
                    .repeatForever(.sequence([
                        Fabrique.doux(.move(to: SCNVector3(0, -0.40, -1.2), duration: 3.4)),
                        .hide(), .move(to: SCNVector3(0, 0.40, -1.2), duration: 0),
                        .wait(duration: 0.8), .unhide()
                    ]))
                ]), forKey: "identification")
            }
        }

        private func construireIdentification() {
            camera.addChildNode(identification)
            identification.position.z = -1.2
            identification.isHidden = true
            // Bandeau très fin dans le repère caméra : couvre toute la largeur de l'écran.
            for (hauteur, intensite) in [(0.0018, 0.42), (0.016, 0.028), (0.065, 0.005)] {
                let plan = SCNPlane(width: 0.80, height: hauteur)
                let matiere = Fabrique.lueur(.orange, alpha: intensite)
                matiere.readsFromDepthBuffer = false
                plan.firstMaterial = matiere
                let bande = SCNNode(geometry: plan)
                bande.renderingOrder = 30
                bande.categoryBitMask = CategorieRendu.effets
                bande.castsShadow = false
                identification.addChildNode(bande)
            }
        }

        private func construireEclairageStudio() {
            let principale = SCNLight()
            principale.type = .directional
            principale.color = NSColor(red: 1, green: 0.96, blue: 0.9, alpha: 1)
            principale.intensity = 850
            principale.castsShadow = true
            principale.shadowMode = .deferred
            principale.shadowColor = NSColor(white: 0, alpha: 0.42)
            principale.shadowRadius = 6
            principale.shadowSampleCount = 8
            principale.shadowMapSize = CGSize(width: 1_536, height: 1_536)
            principale.shadowBias = 0.5
            principale.automaticallyAdjustsShadowProjection = false
            principale.orthographicScale = 7
            principale.zNear = 0.5
            principale.zFar = 24
            principale.maximumShadowDistance = 24
            principale.categoryBitMask = CategorieRendu.structure | CategorieRendu.sol
            let source = SCNNode()
            source.light = principale
            source.position = SCNVector3(-3, 7, 5)
            source.look(at: SCNVector3(0, 0, 0))
            scene.rootNode.addChildNode(source)
            // Une seule carte d'ombres. Les grandes sources secondaires dessinent les volumes.
            for (position, couleur, puissance, largeur, hauteur) in [
                (SCNVector3(2, 3, -4), NSColor(red: 0.52, green: 0.82, blue: 1, alpha: 1), 420.0, 3.0, 2.0),
                (SCNVector3(5, 2, 6), NSColor(red: 0.92, green: 0.96, blue: 1, alpha: 1), 280.0, 4.0, 3.0)
            ] {
                let lumiere = SCNLight()
                lumiere.type = .area
                lumiere.areaType = .rectangle
                lumiere.areaExtents = SIMD3<Float>(Float(largeur), Float(hauteur), 0)
                lumiere.drawsArea = false
                lumiere.color = couleur
                lumiere.intensity = puissance
                lumiere.castsShadow = false
                lumiere.categoryBitMask = CategorieRendu.structure | CategorieRendu.sol
                let panneau = SCNNode()
                panneau.light = lumiere
                panneau.position = position
                panneau.look(at: SCNVector3(0, 0, 0))
                scene.rootNode.addChildNode(panneau)
            }
        }
    }
}

@MainActor
private final class VueScene: SCNView {
    var redimensionner: (@MainActor (CGSize) -> Void)?

    override func layout() {
        super.layout()
        redimensionner?(bounds.size)
    }
}

private enum CategorieRendu {
    static let structure = 1 << 0
    static let sol = 1 << 1
    static let effets = 1 << 2
    static let masque = 1 << 3
}

@MainActor
private final class Drone {
    let racine = SCNNode()
    let sol = SCNNode()
    private let attitude = SCNNode()
    private let coque = SCNNode()
    private let cardan = SCNNode()
    private let lasers = SCNNode()
    let ciblesAeriennes = SCNNode()
    private let balayage = SCNNode()
    private let impacts = SCNNode()
    private let roulis = SCNNode()
    private let tangage = SCNNode()
    private var rotors: [SCNNode] = []
    private var pales: [SCNNode] = []
    private var flous: [SCNNode] = []
    private var feux: [SCNNode] = []
    private var ciblesSol: [SCNNode] = []
    private var ciblesCiel: [SCNNode] = []
    private var grilles: [SCNNode] = []
    private var etat: Etat?
    private var positionApproche = SCNVector3Zero
    private var capApproche: CGFloat = 0
    private enum Etat { case pose, surveillance, approche }

    private let carbone = Fabrique.carbone()
    private let peinture = Fabrique.peinture()
    private let titane = Fabrique.matiere(NSColor(white: 0.25, alpha: 1), metal: 0.78, rugosite: 0.32)
    private let noir = Fabrique.matiere(NSColor(white: 0.045, alpha: 1), metal: 0.02, rugosite: 0.62)
    private let argent = Fabrique.aluminium()
    private let cyan = NSColor(red: 0.08, green: 0.8, blue: 1, alpha: 1)

    init() {
        racine.name = "drone"
        sol.name = "sol"
        racine.addChildNode(attitude)
        attitude.addChildNode(coque)
        construireCoque()
        construireBras()
        construireTrain()
        construireCamera()
        construireDetails()
        Fabrique.classer(racine, categorie: CategorieRendu.structure, projetteOmbre: true)
        for effet in feux + flous {
            Fabrique.classer(effet, categorie: CategorieRendu.effets, projetteOmbre: false)
        }
        construireBalayage()
        construireSol()
        Fabrique.classer(lasers, categorie: CategorieRendu.effets, projetteOmbre: false)
    }

    private func construireCoque() {
        // Deux panneaux profilés raccordés par un joint périphérique fin.
        let dessus = SCNNode(geometry: Fabrique.carenage(dessus: true, hauteur: 0.20, retrait: 0.0015))
        dessus.geometry?.firstMaterial = peinture
        coque.addChildNode(dessus)
        let dessous = SCNNode(geometry: Fabrique.carenage(dessus: false, hauteur: 0.18, retrait: 0.0015))
        dessous.geometry?.firstMaterial = titane
        coque.addChildNode(dessous)
        for dessus in [true, false] {
            let joint = SCNNode(geometry: Fabrique.carenage(dessus: dessus, hauteur: 0.0025, retrait: 0))
            joint.geometry?.firstMaterial = noir
            coque.addChildNode(joint)
        }
        // Trappe encastrée : le rebord suit la batterie, sans liseré métallique surdimensionné.
        let encadrement = Fabrique.boite(0.34, 0.018, 0.54, rayon: 0.008, matiere: noir)
        encadrement.position = SCNVector3(0, 0.235, -0.08)
        coque.addChildNode(encadrement)
    }

    private func construireDetails() {
        let details = SCNNode()
        for cote: CGFloat in [-1, 1] {
            for i in 0..<7 {
                let ouie = Fabrique.boite(0.009, 0.055, 0.026, rayon: 0.003, matiere: noir)
                let z = CGFloat(i) * 0.055 - 0.3
                let largeur = Fabrique.largeurCoque(z: z, y: 0)
                let pente = (Fabrique.largeurCoque(z: z + 0.001, y: 0)
                    - Fabrique.largeurCoque(z: z - 0.001, y: 0)) / 0.002
                ouie.position = SCNVector3(cote * (largeur + 0.001), 0, z)
                ouie.eulerAngles.y = cote * atan(pente)
                details.addChildNode(ouie)
            }
            for z: CGFloat in [-0.27, 0.11] {
                let vis = Fabrique.cylindre(0.014, 0.009, matiere: argent)
                vis.position = SCNVector3(cote * 0.105, 0.305, z)
                details.addChildNode(vis)
                let empreinte = Fabrique.boite(0.016, 0.002, 0.003, rayon: 0, matiere: noir)
                empreinte.position = SCNVector3(cote * 0.105, 0.311, z)
                details.addChildNode(empreinte)
            }
            let antenne = Fabrique.cylindre(0.019, 0.27, matiere: noir)
            antenne.position = SCNVector3(cote * 0.27, 0.32, -0.46)
            antenne.eulerAngles.z = cote * -0.2
            details.addChildNode(antenne)
            let bandeau = Fabrique.bandeau(cote: cote, matiere: Fabrique.lueur(cyan, alpha: 0.4))
            coque.addChildNode(bandeau)
            feux.append(bandeau)
            // Paire stéréo avant et arrière, plus capteurs latéraux.
            for z: CGFloat in [-0.62, 0.62] {
                let capteur = Fabrique.cylindre(0.044, 0.025, matiere: noir)
                capteur.eulerAngles.x = .pi / 2
                capteur.position = SCNVector3(cote * 0.15, 0.025, z)
                details.addChildNode(capteur)
                Fabrique.ellipsoide(parent: details, rayons: SCNVector3(0.028, 0.028, 0.008),
                    position: SCNVector3(cote * 0.15, 0.025, z + (z > 0 ? 0.018 : -0.018)),
                    matiere: Fabrique.matiere(NSColor(red: 0.025, green: 0.07, blue: 0.1, alpha: 1),
                                              metal: 0.45, rugosite: 0.08))
            }
            let lateral = Fabrique.cylindre(0.038, 0.025, matiere: noir)
            lateral.eulerAngles.z = .pi / 2
            lateral.position = SCNVector3(cote * (Fabrique.largeurCoque(z: 0.35, y: -0.035) + 0.008), -0.035, 0.35)
            details.addChildNode(lateral)
        }
        let batterie = Fabrique.boite(0.31, 0.09, 0.5, rayon: 0.025, matiere: peinture)
        batterie.position = SCNVector3(0, 0.255, -0.08)
        details.addChildNode(batterie)
        for x: CGFloat in [-0.125, 0.125] {
            let verrou = Fabrique.boite(0.032, 0.025, 0.13, rayon: 0.006, matiere: argent)
            verrou.position = SCNVector3(x, 0.303, -0.08)
            details.addChildNode(verrou)
        }
        coque.addChildNode(details.flattenedClone())
    }

    private func construireBras() {
        for (i, point) in [SCNVector3(-0.99, 0, 0.78), SCNVector3(0.99, 0, 0.78),
                           SCNVector3(-0.99, 0, -0.78), SCNVector3(0.99, 0, -0.78)].enumerated() {
            let depart = SCNVector3(point.x * 0.23, -0.025, point.z * 0.3)
            let bras = Fabrique.barre(de: depart, vers: point, largeur: 0.135, hauteur: 0.075, matiere: carbone)
            coque.addChildNode(bras)
            let lisere = Fabrique.barre(de: SCNVector3(depart.x, 0.018, depart.z),
                                       vers: SCNVector3(point.x, 0.043, point.z),
                                       largeur: 0.009, hauteur: 0.004, matiere: titane)
            coque.addChildNode(lisere)
            let moteur = SCNNode()
            moteur.position = point
            coque.addChildNode(moteur)
            for (rayon, hauteur, y, matiere) in [(0.145, 0.045, 0.015, noir),
                                               (0.116, 0.15, 0.105, titane),
                                               (0.12, 0.018, 0.18, argent),
                                               (0.076, 0.028, 0.2, noir)] {
                let piece = Fabrique.cylindre(rayon, hauteur, matiere: matiere)
                piece.position.y = y
                moteur.addChildNode(piece)
            }
            for cran in 0..<8 {
                let angle = CGFloat(cran) * .pi / 4
                let event = Fabrique.boite(0.027, 0.075, 0.013, rayon: 0.004, matiere: noir)
                event.position = SCNVector3(sin(angle) * 0.115, 0.105, cos(angle) * 0.115)
                event.eulerAngles.y = angle
                moteur.addChildNode(event)
            }
            let rotor = SCNNode()
            rotor.position.y = 0.23
            moteur.addChildNode(rotor)
            rotors.append(rotor)
            let bipale = SCNNode()
            for angle: CGFloat in [0, .pi] {
                let pale = SCNNode(geometry: Fabrique.pale(sens: i.isMultiple(of: 2) ? 1 : -1))
                pale.geometry?.firstMaterial = carbone
                pale.eulerAngles.y = angle
                bipale.addChildNode(pale)
            }
            rotor.addChildNode(bipale)
            pales.append(bipale)
            let moyeu = Fabrique.cylindre(0.047, 0.045, matiere: argent)
            rotor.addChildNode(moyeu)
            // Le flou des hélices est une surface mate transparente, sans émission lumineuse.
            let disque = SCNPlane(width: 1.04, height: 1.04)
            disque.firstMaterial = Fabrique.matiereHelice()
            let flou = SCNNode(geometry: disque)
            flou.eulerAngles.x = -.pi / 2
            flou.position.y = 0.23
            moteur.addChildNode(flou)
            flous.append(flou)
            let feu = Fabrique.sphere(0.023, matiere: Fabrique.lueur(.orange, alpha: 0.5))
            feu.position = SCNVector3(point.x * 1.035, -0.055, point.z)
            coque.addChildNode(feu)
            feux.append(feu)
        }
        let feuArriere = Fabrique.sphere(0.018, matiere: Fabrique.lueur(.white, alpha: 0.4))
        feuArriere.position = SCNVector3(0, 0.09, -0.655)
        coque.addChildNode(feuArriere)
        feux.append(feuArriere)
    }

    private func construireTrain() {
        for x: CGFloat in [-0.4, 0.4] {
            for z: CGFloat in [-0.36, 0.36] {
                let jambe = Fabrique.tube(de: SCNVector3(x * 0.72, -0.08, z),
                                         vers: SCNVector3(x * 1.35, -0.54, z * 1.2), rayon: 0.027, matiere: titane)
                coque.addChildNode(jambe)
            }
            coque.addChildNode(Fabrique.tube(de: SCNVector3(x * 1.35, -0.54, -0.63),
                                            vers: SCNVector3(x * 1.35, -0.54, 0.66), rayon: 0.033, matiere: noir))
            for z: CGFloat in [-0.5, 0.51] {
                let semelle = Fabrique.boite(0.1, 0.045, 0.18, rayon: 0.022, matiere: noir)
                semelle.position = SCNVector3(x * 1.35, -0.56, z)
                coque.addChildNode(semelle)
            }
        }
    }

    private func construireCamera() {
        let fixation = Fabrique.cylindre(0.07, 0.14, matiere: titane)
        fixation.position = SCNVector3(0, -0.19, 0.32)
        coque.addChildNode(fixation)
        cardan.position = SCNVector3(0, -0.32, 0.36)
        coque.addChildNode(cardan)
        cardan.addChildNode(roulis)
        roulis.addChildNode(tangage)
        let axeRoulis = Fabrique.cylindre(0.055, 0.07, matiere: titane)
        axeRoulis.eulerAngles.x = .pi / 2
        axeRoulis.position = SCNVector3(0, 0.06, -0.12)
        roulis.addChildNode(axeRoulis)
        for x: CGFloat in [-0.17, 0.17] {
            let fourche = Fabrique.boite(0.035, 0.19, 0.09, rayon: 0.014, matiere: argent)
            fourche.position = SCNVector3(x, 0.04, 0)
            roulis.addChildNode(fourche)
            let axe = Fabrique.cylindre(0.043, 0.045, matiere: noir)
            axe.eulerAngles.z = .pi / 2
            axe.position.x = x
            roulis.addChildNode(axe)
        }
        Fabrique.ellipsoide(parent: tangage, rayons: SCNVector3(0.145, 0.13, 0.14),
                            position: SCNVector3(0, 0, 0), matiere: titane)
        let objectif = Fabrique.cylindre(0.095, 0.085, matiere: noir)
        objectif.eulerAngles.x = .pi / 2
        objectif.position.z = 0.13
        tangage.addChildNode(objectif)
        let bague = Fabrique.anneau(0.078, epaisseur: 0.009, matiere: argent)
        bague.eulerAngles.x = .pi / 2
        bague.position.z = 0.177
        tangage.addChildNode(bague)
        let verre = Fabrique.matiere(NSColor(red: 0.012, green: 0.035, blue: 0.047, alpha: 1), metal: 0.2, rugosite: 0.055)
        verre.clearCoat.contents = 1.0
        verre.clearCoatRoughness.contents = 0.035
        Fabrique.ellipsoide(parent: tangage, rayons: SCNVector3(0.071, 0.071, 0.018),
                            position: SCNVector3(0, 0, 0.18), matiere: verre)
        // Le reflet sur le verre provient des panneaux de l'environnement, pas d'une tache lumineuse peinte.
    }

    private func construireBalayage() {
        coque.addChildNode(lasers)
        let rouge = NSColor(red: 1, green: 0.06, blue: 0.035, alpha: 1)
        let vert = NSColor(red: 0.12, green: 1, blue: 0.42, alpha: 1)
        for i in 0..<10 {
            let couleur = i == 2 || i == 7 ? vert : (i.isMultiple(of: 2) ? cyan : rouge)
            let emetteur = SCNNode()
            emetteur.position = SCNVector3(i.isMultiple(of: 2) ? -0.3 : 0.3, -0.17,
                                           i < 6 ? 0.28 : -0.25)
            lasers.addChildNode(emetteur)
            let cible = SCNNode()
            if i < 6 {
                impacts.addChildNode(cible)
                ciblesSol.append(cible)
                let point = Fabrique.sphere(0.024, matiere: Fabrique.lueur(couleur, alpha: 0.85))
                point.scale.y = 0.2
                point.position.y = 0.012
                cible.addChildNode(point)
                let haloImpact = Fabrique.anneau(0.075, epaisseur: 0.003,
                                                matiere: Fabrique.lueur(couleur, alpha: 0.22))
                haloImpact.position.y = 0.009
                cible.addChildNode(haloImpact)
            } else {
                ciblesAeriennes.addChildNode(cible)
                ciblesCiel.append(cible)
            }
            let visee = SCNLookAtConstraint(target: cible)
            visee.isGimbalLockEnabled = false
            emetteur.constraints = [visee]
            // Faisceaux longs, découpés au niveau du sol. Les impacts sont les cibles réelles.
            for (rayon, intensite) in [(0.0025, 0.46), (0.012, 0.032)] {
                let trait = Fabrique.cylindre(rayon, 70, matiere: Fabrique.lueur(couleur, alpha: intensite))
                (trait.geometry as? SCNCylinder)?.radialSegmentCount = 6
                trait.eulerAngles.x = .pi / 2
                trait.position.z = -35
                trait.renderingOrder = 3
                emetteur.addChildNode(trait)
            }
        }
    }

    private func construireSol() {
        sol.position.y = -0.86
        let masque = SCNMaterial()
        masque.lightingModel = .constant
        masque.colorBufferWriteMask = []
        masque.writesToDepthBuffer = true
        masque.isDoubleSided = true
        let plan = SCNPlane(width: 180, height: 180)
        plan.firstMaterial = masque
        let limite = SCNNode(geometry: plan)
        limite.eulerAngles.x = -.pi / 2
        limite.position.y = -0.008
        limite.renderingOrder = -10
        sol.addChildNode(limite)
        limite.categoryBitMask = CategorieRendu.masque
        limite.castsShadow = false
        let surface = SCNFloor()
        surface.width = 40
        surface.length = 40
        surface.reflectivity = 0.17
        surface.reflectionFalloffStart = 0.05
        surface.reflectionFalloffEnd = 3.4
        // Reflet volontairement adouci à basse résolution ; seuls les éléments solides sont redessinés.
        surface.reflectionResolutionScaleFactor = 0.125
        surface.reflectionCategoryBitMask = CategorieRendu.structure
        surface.firstMaterial = Fabrique.solStudio()
        let plateau = SCNNode(geometry: surface)
        plateau.categoryBitMask = CategorieRendu.sol
        plateau.castsShadow = false
        plateau.renderingOrder = -5
        sol.addChildNode(plateau)
        sol.addChildNode(balayage)
        sol.addChildNode(impacts)
        let cercle = Fabrique.anneau(1.08, epaisseur: 0.006, matiere: Fabrique.lueur(cyan, alpha: 0.24))
        balayage.addChildNode(cercle)
        cercle.runAction(.repeatForever(.sequence([
            Fabrique.doux(.scale(to: 1.9, duration: 2.6)),
            Fabrique.doux(.scale(to: 1, duration: 2.6))
        ])))
        let interieur = Fabrique.anneau(0.92, epaisseur: 0.002, matiere: Fabrique.lueur(cyan, alpha: 0.08))
        balayage.addChildNode(interieur)
        let reperes = SCNNode()
        for i in 0..<48 {
            let angle = CGFloat(i) * .pi / 24
            let repere = Fabrique.boite(0.007, 0.002, i.isMultiple(of: 4) ? 0.12 : 0.035,
                                       rayon: 0, matiere: Fabrique.lueur(cyan, alpha: 0.16))
            repere.position = SCNVector3(sin(angle) * 1.26, 0.006, cos(angle) * 1.26)
            repere.eulerAngles.y = angle
            reperes.addChildNode(repere)
        }
        balayage.addChildNode(reperes.flattenedClone())
        for i in 0..<2 {
            let grille = SCNNode(geometry: Fabrique.grille())
            grille.geometry?.firstMaterial = Fabrique.lueur(i == 0 ? cyan : .green, alpha: i == 0 ? 0.026 : 0.012)
            grille.position.y = 0.004 + CGFloat(i) * 0.002
            sol.addChildNode(grille)
            grilles.append(grille)
        }
        for effet in [balayage, impacts] + grilles {
            Fabrique.classer(effet, categorie: CategorieRendu.effets, projetteOmbre: false)
        }
    }

    func cadrer(camera: SCNNode, rapport: CGFloat) {
        // En gros plan : environ 45 % de hauteur, centre au-dessus du milieu de l'écran.
        let distance: CGFloat = 4.45 * max(1, 1.45 / rapport)
        positionApproche = camera.convertPosition(SCNVector3(0, distance * tan(.pi * 18 / 180) * 0.30, -distance), to: nil)
        capApproche = atan2(camera.position.x - positionApproche.x, camera.position.z - positionApproche.z)
        if etat == .approche {
            racine.runAction(Fabrique.doux(.move(to: positionApproche, duration: 0.25)), forKey: "trajet")
        }
        for (i, cible) in ciblesCiel.enumerated() {
            cible.removeAllActions()
            let cote: CGFloat = i.isMultiple(of: 2) ? -1 : 1
            let profondeur: CGFloat = i < 2 ? 7 : 1.8
            let demiHauteur = profondeur * tan(.pi * 18 / 180)
            let debut = camera.convertPosition(SCNVector3(cote * demiHauteur * rapport * 1.2,
                                                          demiHauteur * 0.9, -profondeur), to: nil)
            let fin = camera.convertPosition(SCNVector3(-cote * demiHauteur * rapport * 0.85,
                                                        demiHauteur * (i < 2 ? 0.25 : -0.7), -profondeur), to: nil)
            cible.position = debut
            cible.runAction(.repeatForever(.sequence([
                Fabrique.doux(.move(to: fin, duration: 2.6 + Double(i) * 0.7)),
                Fabrique.doux(.move(to: debut, duration: 3.1 + Double(i) * 0.6))
            ])))
        }
        for (i, cible) in ciblesSol.enumerated() {
            cible.removeAllActions()
            let angle = CGFloat(i) * .pi / 3
            let portee = max(5, 5 * rapport)
            let debut = SCNVector3(sin(angle) * portee, 0, cos(angle) * 5)
            let fin = SCNVector3(sin(angle + 1.2) * portee, 0, cos(angle + 1.2) * 7)
            cible.position = debut
            cible.runAction(.repeatForever(.sequence([
                Fabrique.doux(.move(to: fin, duration: 2.2 + Double(i) * 0.6)),
                Fabrique.doux(.move(to: debut, duration: 2.8 + Double(i) * 0.5))
            ])))
        }
    }

    private var niveauLasers: Double = -1

    /// Atténue tous les effets lumineux de surveillance. En mélange additif, SceneKit ignore la transparence :
    /// on multiplie donc la couleur rendue (`multiply`), et on masque tout à 0.
    func reglerLasers(_ niveau: Double) {
        let n = min(max(niveau, 0), 1)
        guard abs(n - niveauLasers) > 0.001 else { return }
        niveauLasers = n
        let gris = NSColor(white: n, alpha: 1)
        for effet in [lasers, balayage, impacts] + grilles {
            effet.opacity = n == 0 ? 0 : 1
            effet.enumerateHierarchy { noeud, _ in
                noeud.geometry?.materials.forEach { $0.multiply.contents = gris }
            }
        }
    }

    func actualiser(enVol: Bool, approche: Bool, camera: SCNNode, anime: Bool) {
        let nouveau: Etat = approche ? .approche : (enVol ? .surveillance : .pose)
        guard etat != nouveau else { return }
        let precedent = etat
        etat = nouveau
        let vol = nouveau != .pose
        let surveillance = nouveau == .surveillance
        let duree: TimeInterval = anime ? (approche ? 0.8 : (precedent == .approche ? 1 : 1.5)) : 0
        let destination = approche ? positionApproche : SCNVector3(0, vol ? 0.45 : -0.2775, 0)
        // Repartir de la présentation évite un saut lors d'appuis rapprochés sur le clavier.
        let position = racine.presentation.position
        racine.removeAction(forKey: "trajet")
        racine.position = position
        if duree == 0 { racine.position = destination }
        else { racine.runAction(Fabrique.doux(.move(to: destination, duration: duree)), forKey: "trajet") }
        for noeud in [attitude, coque, cardan, roulis, tangage] {
            let angles = noeud.presentation.eulerAngles
            let position = noeud.presentation.position
            noeud.removeAllActions()
            noeud.eulerAngles = angles
            noeud.position = position
        }
        cardan.constraints = nil
        let remise = Fabrique.doux(SCNAction.group([
            .move(to: SCNVector3Zero, duration: duree),
            .rotateTo(x: 0, y: 0, z: 0, duration: duree, usesShortestUnitArc: true)
        ]))
        attitude.runAction(remise)
        if surveillance {
            attitude.runAction(.sequence([.wait(duration: duree), .repeatForever(.sequence([
                Fabrique.doux(.move(to: SCNVector3(0, 0.04, 0), duration: 2)),
                Fabrique.doux(.move(to: SCNVector3(0, -0.025, 0), duration: 2.6))
            ]))]), forKey: "stationnaire")
            coque.runAction(.sequence([
                Fabrique.doux(.rotateTo(x: 0, y: 0, z: 0, duration: duree, usesShortestUnitArc: true)),
                .repeatForever(.sequence([
                    Fabrique.doux(.rotateTo(x: 0.025, y: -0.12, z: -0.025, duration: 4.2)),
                    Fabrique.doux(.rotateTo(x: -0.02, y: 0.15, z: 0.02, duration: 5.1))
                ]))
            ]))
            cardan.runAction(.repeatForever(.sequence([
                Fabrique.doux(.rotateTo(x: 0, y: -0.3, z: 0, duration: 3.5)),
                Fabrique.doux(.rotateTo(x: 0, y: 0.38, z: 0, duration: 4.5))
            ])))
            tangage.runAction(.repeatForever(.sequence([
                Fabrique.doux(.rotateTo(x: 0.18, y: 0, z: 0, duration: 2.7)),
                Fabrique.doux(.rotateTo(x: -0.12, y: 0, z: 0, duration: 3.7))
            ])))
        } else {
            if approche && anime {
                coque.runAction(.sequence([
                    Fabrique.doux(.rotateTo(x: 0.15, y: capApproche, z: -0.025, duration: 0.32)),
                    Fabrique.doux(.rotateTo(x: 0, y: capApproche, z: 0, duration: 0.48))
                ]))
            } else {
                coque.runAction(Fabrique.doux(.rotateTo(x: 0, y: approche ? capApproche : 0, z: 0,
                                                       duration: duree, usesShortestUnitArc: true)))
            }
            tangage.runAction(Fabrique.doux(.rotateTo(x: 0, y: 0, z: 0, duration: duree)))
            if approche {
                let visee = SCNLookAtConstraint(target: camera)
                visee.localFront = SCNVector3(0, 0, 1)
                visee.isGimbalLockEnabled = true
                cardan.constraints = [visee]
            } else {
                cardan.runAction(Fabrique.doux(.rotateTo(x: 0, y: 0, z: 0, duration: duree)))
            }
        }
        let stabilisation = Fabrique.doux(SCNAction.rotateTo(x: 0, y: 0, z: 0, duration: duree))
        if surveillance {
            roulis.runAction(.sequence([stabilisation, .repeatForever(.sequence([
                Fabrique.doux(.rotateTo(x: 0, y: 0, z: 0.02, duration: 4.2)),
                Fabrique.doux(.rotateTo(x: 0, y: 0, z: -0.02, duration: 5.1))
            ]))]))
        } else { roulis.runAction(stabilisation) }
        for (i, rotor) in rotors.enumerated() {
            rotor.removeAllActions()
            let sens: CGFloat = i.isMultiple(of: 2) ? 1 : -1
            if vol {
                rotor.runAction(.repeatForever(.rotateBy(x: 0, y: sens * .pi * 2, z: 0, duration: 0.09)))
            } else if anime {
                let freinage = SCNAction.rotateBy(x: 0, y: sens * .pi * 12, z: 0, duration: duree)
                freinage.timingMode = .easeOut
                rotor.runAction(freinage)
            }
            flous[i].isHidden = !vol
            pales[i].opacity = vol ? 0.14 : 1
            Fabrique.classer(pales[i], categorie: CategorieRendu.structure, projetteOmbre: !vol)
        }
        for (i, feu) in feux.enumerated() {
            let couleur: NSColor = surveillance ? (i < 4 ? (i.isMultiple(of: 2) ? .red : cyan) : cyan) : .orange
            let matiere = Fabrique.lueur(couleur, alpha: surveillance ? 0.42 : 0.38)
            feu.geometry?.firstMaterial = matiere
            if !vol { Fabrique.respirer(matiere, couleur: couleur) }
        }
        // Extinction explicite : ne pas dépendre de la transparence d'un matériau additif.
        ciblesAeriennes.isPaused = !surveillance
        impacts.isPaused = !surveillance
        balayage.isPaused = !surveillance
        let reprise = surveillance && precedent == .approche ? duree : 0
        for effet in [lasers, balayage, impacts] + grilles {
            effet.removeAction(forKey: "visibilite")
            effet.isHidden = !surveillance || reprise > 0
            if surveillance && reprise > 0 {
                effet.runAction(.sequence([.wait(duration: reprise), .unhide()]), forKey: "visibilite")
            }
        }
        for (i, grille) in grilles.enumerated() {
            grille.removeAction(forKey: "passage")
            if surveillance {
                grille.position.z = i == 0 ? -8 : 6
                grille.runAction(.repeatForever(.sequence([
                    Fabrique.doux(.move(to: SCNVector3(0, grille.position.y, i == 0 ? 8 : -6), duration: 5.5)),
                    Fabrique.doux(.move(to: SCNVector3(0, grille.position.y, i == 0 ? -8 : 6), duration: 5.5))
                ])), forKey: "passage")
            }
        }
    }
}

@MainActor
private enum Fabrique {
    /// Image linéaire HDR partagée entre les écrans ; aucune ressource chargée sur disque.
    static let environnementStudio: CGImage? = {
        let largeur = 512, hauteur = 256
        var pixels = [Float](repeating: 0, count: largeur * hauteur * 4)
        func panneau(_ u: Double, _ v: Double, centre: CGPoint, taille: CGSize) -> Double {
            let ecart = abs(u - centre.x)
            let dx = min(ecart, 1 - ecart) / taille.width
            let dy = abs(v - centre.y) / taille.height
            return exp(-pow(dx, 8) - pow(dy, 8))
        }
        for y in 0..<hauteur {
            let v = (Double(y) + 0.5) / Double(hauteur)
            for x in 0..<largeur {
                let u = (Double(x) + 0.5) / Double(largeur)
                let plafond = 0.035 + 0.12 * exp(-pow((v - 0.18) / 0.28, 2))
                let principal = panneau(u, v, centre: CGPoint(x: 0.18, y: 0.28),
                                        taille: CGSize(width: 0.095, height: 0.11))
                let lateral = panneau(u, v, centre: CGPoint(x: 0.70, y: 0.39),
                                      taille: CGSize(width: 0.025, height: 0.17))
                let remplissage = panneau(u, v, centre: CGPoint(x: 0.46, y: 0.24),
                                           taille: CGSize(width: 0.13, height: 0.075))
                let indice = (y * largeur + x) * 4
                pixels[indice] = Float(plafond + 3.4 * principal + 1.2 * lateral + 0.65 * remplissage)
                pixels[indice + 1] = Float(plafond + 3.25 * principal + 1.55 * lateral + 0.70 * remplissage)
                pixels[indice + 2] = Float(plafond + 3.05 * principal + 1.8 * lateral + 0.78 * remplissage)
                pixels[indice + 3] = 1
            }
        }
        let donnees = pixels.withUnsafeBytes { Data($0) } as CFData
        guard let espace = CGColorSpace(name: CGColorSpace.extendedLinearSRGB),
              let fournisseur = CGDataProvider(data: donnees) else { return nil }
        let format: CGBitmapInfo = [.floatComponents, .byteOrder32Little,
                                    CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedLast.rawValue)]
        return CGImage(width: largeur, height: hauteur, bitsPerComponent: 32, bitsPerPixel: 128,
                       bytesPerRow: largeur * 16, space: espace, bitmapInfo: format,
                       provider: fournisseur, decode: nil, shouldInterpolate: true, intent: .defaultIntent)
    }()

    static func classer(_ noeud: SCNNode, categorie: Int, projetteOmbre: Bool) {
        noeud.categoryBitMask = categorie
        noeud.castsShadow = projetteOmbre
        for enfant in noeud.childNodes { classer(enfant, categorie: categorie, projetteOmbre: projetteOmbre) }
    }

    static func peinture() -> SCNMaterial {
        let matiere = matiere(NSColor(red: 0.26, green: 0.28, blue: 0.30, alpha: 1),
                              metal: 0.55, rugosite: 0.26)
        matiere.clearCoat.contents = 0.95
        matiere.clearCoatRoughness.contents = 0.12
        return matiere
    }

    static func solStudio() -> SCNMaterial {
        let matiere = matiere(NSColor(red: 0.075, green: 0.08, blue: 0.087, alpha: 1),
                              metal: 0.18, rugosite: 0.46)
        matiere.blendMode = .alpha
        matiere.writesToDepthBuffer = true
        // Le plateau se fond dans le fond transparent ; les coordonnées restent indépendantes des UV du SCNFloor.
        matiere.shaderModifiers = [
            .geometry: """
                #pragma varyings
                float2 positionSol;
                #pragma body
                out.positionSol = _geometry.position.xz;
                """,
            .fragment: """
                #pragma transparent
                #pragma body
                float distanceSol = length(in.positionSol / float2(1.15, 1.0));
                float presenceSol = 1.0 - smoothstep(4.0, 12.0, distanceSol);
                _output.color *= presenceSol;
                """
        ]
        return matiere
    }

    /// Coque fermée aux extrémités, normales lissées ; le carbone reste sur les éléments structurels.
    static func carenage(dessus: Bool, hauteur: CGFloat, retrait: CGFloat) -> SCNGeometry {
        let tranches = 40, secteurs = 32
        func puissanceSignee(_ valeur: CGFloat, _ exposant: CGFloat) -> CGFloat {
            guard abs(valeur) > 0.0000001 else { return 0 }
            return (valeur < 0 ? -1 : 1) * pow(abs(valeur), exposant)
        }
        func point(_ u: CGFloat, _ v: CGFloat) -> SIMD3<Float> {
            let longitude = max(-CGFloat.pi / 2, min(.pi / 2, u))
            let section = pow(max(0, cos(longitude)), 0.55)
            let largeur = 0.39 * (1 - 0.06 * sin(longitude))
            let x = largeur * section * puissanceSignee(cos(v), 0.62)
            let y = 0.04 + (dessus ? retrait : -retrait)
                + hauteur * section * puissanceSignee(sin(v), 0.72)
            let z = 0.64 * puissanceSignee(sin(longitude), 0.78)
            return SIMD3<Float>(Float(x), Float(y), Float(z))
        }
        var sommets: [SCNVector3] = []
        var normales: [SCNVector3] = []
        var uv: [CGPoint] = []
        var indices: [Int32] = []
        for i in 0...tranches {
            let u = -CGFloat.pi / 2 + CGFloat(i) * .pi / CGFloat(tranches)
            for j in 0...secteurs {
                let v = (dessus ? 0 : CGFloat.pi) + CGFloat(j) * .pi / CGFloat(secteurs)
                let position = point(u, v)
                let longitudinal = point(u + 0.0005, v) - point(u - 0.0005, v)
                let transversal = point(u, v + 0.0005) - point(u, v - 0.0005)
                let produit = simd_cross(transversal, longitudinal)
                let normale = simd_length_squared(produit) > 1e-16
                    ? simd_normalize(produit) : SIMD3<Float>(0, 0, i < tranches / 2 ? -1 : 1)
                sommets.append(SCNVector3(position))
                normales.append(SCNVector3(normale))
                uv.append(CGPoint(x: CGFloat(j) / CGFloat(secteurs), y: CGFloat(i) / CGFloat(tranches)))
                if i < tranches && j < secteurs {
                    let n = Int32(i * (secteurs + 1) + j)
                    let suivant = n + Int32(secteurs + 1)
                    indices += [n, n + 1, suivant, n + 1, suivant + 1, suivant]
                }
            }
        }
        return SCNGeometry(sources: [.init(vertices: sommets), .init(normals: normales), .init(textureCoordinates: uv)],
                           elements: [.init(indices: indices, primitiveType: .triangles)])
    }

    static func largeurCoque(z: CGFloat, y: CGFloat) -> CGFloat {
        let sinus = (z < 0 ? -1.0 : 1.0) * pow(min(1, abs(z) / 0.64), 1 / 0.78)
        let section = pow(sqrt(max(0, 1 - sinus * sinus)), 0.55)
        let dessus = y >= 0.04
        let centre = 0.04 + (dessus ? 0.0015 : -0.0015)
        let rayonVertical = (dessus ? 0.20 : 0.18) * section
        guard rayonVertical > 0 else { return 0 }
        let latitude = pow(min(1, abs(y - centre) / rayonVertical), 1 / 0.72)
        return 0.39 * (1 - 0.06 * sinus) * section * pow(sqrt(max(0, 1 - latitude * latitude)), 0.62)
    }

    static func bandeau(cote: CGFloat, matiere: SCNMaterial) -> SCNNode {
        var sommets: [SCNVector3] = []
        var indices: [Int32] = []
        for i in 0...20 {
            let z = -0.26 + CGFloat(i) * 0.023
            for y: CGFloat in [0.037, 0.043] {
                sommets.append(SCNVector3(cote * (largeurCoque(z: z, y: y) + 0.0015), y, z))
            }
            if i < 20 {
                let n = Int32(i * 2)
                indices += [n, n + 1, n + 2, n + 1, n + 3, n + 2]
            }
        }
        let geometrie = SCNGeometry(sources: [.init(vertices: sommets)],
                                    elements: [.init(indices: indices, primitiveType: .triangles)])
        geometrie.firstMaterial = matiere
        return SCNNode(geometry: geometrie)
    }

    private static let imageHelice: CGImage? = {
        let taille = 128
        var pixels = [UInt8](repeating: 0, count: taille * taille * 4)
        for y in 0..<taille {
            for x in 0..<taille {
                let dx = (Double(x) + 0.5) / Double(taille) * 2 - 1
                let dy = (Double(y) + 0.5) / Double(taille) * 2 - 1
                let rayon = sqrt(dx * dx + dy * dy)
                let centre = min(1, max(0, (rayon - 0.10) / 0.12))
                let bord = min(1, max(0, (1 - rayon) / 0.09))
                let densite = (0.055 + 0.035 * pow(sin(rayon * .pi), 2)) * centre * bord
                let indice = (y * taille + x) * 4
                pixels[indice] = 95
                pixels[indice + 1] = 100
                pixels[indice + 2] = 106
                pixels[indice + 3] = UInt8(densite * 255)
            }
        }
        let donnees = Data(pixels) as CFData
        guard let fournisseur = CGDataProvider(data: donnees),
              let espace = CGColorSpace(name: CGColorSpace.sRGB) else { return nil }
        return CGImage(width: taille, height: taille, bitsPerComponent: 8, bitsPerPixel: 32,
                       bytesPerRow: taille * 4, space: espace,
                       bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.last.rawValue),
                       provider: fournisseur, decode: nil, shouldInterpolate: true, intent: .defaultIntent)
    }()

    static func matiereHelice() -> SCNMaterial {
        let matiere = matiere(NSColor(white: 0.18, alpha: 1), metal: 0.15, rugosite: 0.52)
        matiere.diffuse.contents = imageHelice
        matiere.transparencyMode = .aOne
        matiere.blendMode = .alpha
        matiere.isDoubleSided = true
        matiere.writesToDepthBuffer = false
        return matiere
    }

    static func matiere(_ couleur: NSColor, metal: CGFloat, rugosite: CGFloat) -> SCNMaterial {
        let matiere = SCNMaterial()
        matiere.lightingModel = .physicallyBased
        matiere.diffuse.contents = couleur
        matiere.metalness.contents = metal
        matiere.roughness.contents = rugosite
        return matiere
    }

    static func carbone() -> SCNMaterial {
        let matiere = matiere(NSColor(white: 0.15, alpha: 1), metal: 0.3, rugosite: 0.42)
        // Tissage généré une seule fois par scène, sans ressource externe.
        let image = NSImage(size: NSSize(width: 64, height: 64))
        image.lockFocus()
        NSColor(white: 0.055, alpha: 1).setFill()
        NSRect(x: 0, y: 0, width: 64, height: 64).fill()
        for x in 0..<8 {
            for y in 0..<8 {
                let horizontal = (x + y).isMultiple(of: 2)
                for fil in 0..<4 {
                    NSColor(white: fil.isMultiple(of: 2) ? 0.11 : 0.075, alpha: 1).setFill()
                    NSRect(x: x * 8 + (horizontal ? 0 : fil * 2),
                           y: y * 8 + (horizontal ? fil * 2 : 0),
                           width: horizontal ? 7 : 1, height: horizontal ? 1 : 7).fill()
                }
            }
        }
        image.unlockFocus()
        matiere.diffuse.contents = image
        matiere.diffuse.wrapS = .repeat
        matiere.diffuse.wrapT = .repeat
        matiere.diffuse.contentsTransform = SCNMatrix4MakeScale(8, 8, 1)
        matiere.isDoubleSided = true
        return matiere
    }

    /// Matière additive. En mélange `.add`, SceneKit ignore `transparency` : l'intensité voulue est donc portée par
    /// la couleur elle-même (couleur × alpha), sinon un disque « à 5 % » s'affiche en blanc plein.
    static func lueur(_ couleur: NSColor, alpha: CGFloat = 1) -> SCNMaterial {
        let matiere = SCNMaterial()
        let c = couleur.usingColorSpace(.deviceRGB) ?? couleur
        let attenuee = NSColor(red: c.redComponent * alpha, green: c.greenComponent * alpha,
                               blue: c.blueComponent * alpha, alpha: 1)
        matiere.lightingModel = .constant
        matiere.diffuse.contents = attenuee
        matiere.emission.contents = attenuee
        matiere.emission.intensity = 1.4
        matiere.blendMode = .add
        matiere.isDoubleSided = true
        matiere.writesToDepthBuffer = false
        return matiere
    }

    static func respirer(_ matiere: SCNMaterial, couleur: NSColor) {
        let c = couleur.usingColorSpace(.deviceRGB) ?? couleur
        for propriete in ["diffuse.contents", "emission.contents"] {
            let animation = CABasicAnimation(keyPath: propriete)
            animation.fromValue = NSColor(red: c.redComponent * 0.15, green: c.greenComponent * 0.15,
                                          blue: c.blueComponent * 0.15, alpha: 1)
            animation.toValue = NSColor(red: c.redComponent * 0.65, green: c.greenComponent * 0.65,
                                        blue: c.blueComponent * 0.65, alpha: 1)
            animation.duration = 1.8
            animation.autoreverses = true
            animation.repeatCount = .infinity
            animation.timingFunction = CAMediaTimingFunction(name: .easeInEaseOut)
            matiere.addAnimation(animation, forKey: propriete)
        }
    }

    static func aluminium() -> SCNMaterial {
        let matiere = matiere(NSColor(white: 0.55, alpha: 1), metal: 0.85, rugosite: 0.32)
        let image = NSImage(size: NSSize(width: 64, height: 64))
        image.lockFocus()
        for y in 0..<64 {
            NSColor(white: 0.31 + CGFloat((y * 17) % 13) * 0.008, alpha: 1).setFill()
            NSRect(x: 0, y: y, width: 64, height: 1).fill()
        }
        image.unlockFocus()
        matiere.roughness.contents = image
        matiere.roughness.wrapS = .repeat
        matiere.roughness.wrapT = .repeat
        matiere.roughness.contentsTransform = SCNMatrix4MakeScale(1, 6, 1)
        return matiere
    }

    /// Une seule géométrie pour les traits de chaque grille au sol.
    static func grille() -> SCNGeometry {
        var sommets: [SCNVector3] = []
        var indices: [Int32] = []
        func rectangle(_ x: CGFloat, _ z: CGFloat, _ largeur: CGFloat, _ profondeur: CGFloat) {
            let n = Int32(sommets.count)
            sommets += [SCNVector3(x, 0, z), SCNVector3(x + largeur, 0, z),
                        SCNVector3(x, 0, z + profondeur), SCNVector3(x + largeur, 0, z + profondeur)]
            indices += [n, n + 2, n + 1, n + 1, n + 2, n + 3]
        }
        for i in -22...22 { rectangle(CGFloat(i) * 1.4, -3, 0.006, 6) }
        for i in -3...3 { rectangle(-31, CGFloat(i), 62, 0.006) }
        return SCNGeometry(sources: [.init(vertices: sommets)],
                           elements: [.init(indices: indices, primitiveType: .triangles)])
    }

    static func boite(_ largeur: CGFloat, _ hauteur: CGFloat, _ longueur: CGFloat,
                      rayon: CGFloat, matiere: SCNMaterial) -> SCNNode {
        let arrondi = min(rayon, min(largeur, min(hauteur, longueur)) / 2)
        let forme = SCNBox(width: largeur, height: hauteur, length: longueur, chamferRadius: arrondi)
        forme.chamferSegmentCount = arrondi > 0.015 ? 5 : 2
        forme.firstMaterial = matiere
        return SCNNode(geometry: forme)
    }

    static func cylindre(_ rayon: CGFloat, _ hauteur: CGFloat, matiere: SCNMaterial) -> SCNNode {
        let forme = SCNCylinder(radius: rayon, height: hauteur)
        forme.radialSegmentCount = rayon > 0.06 ? 32 : 16
        forme.firstMaterial = matiere
        return SCNNode(geometry: forme)
    }

    static func sphere(_ rayon: CGFloat, matiere: SCNMaterial) -> SCNNode {
        let forme = SCNSphere(radius: rayon)
        forme.segmentCount = 24
        forme.firstMaterial = matiere
        return SCNNode(geometry: forme)
    }

    static func ellipsoide(parent: SCNNode, rayons: SCNVector3, position: SCNVector3, matiere: SCNMaterial) {
        let noeud = sphere(1, matiere: matiere)
        noeud.scale = rayons
        noeud.position = position
        parent.addChildNode(noeud)
    }

    static func anneau(_ rayon: CGFloat, epaisseur: CGFloat, matiere: SCNMaterial) -> SCNNode {
        let forme = SCNTorus(ringRadius: rayon, pipeRadius: epaisseur)
        forme.ringSegmentCount = 64
        forme.pipeSegmentCount = 6
        forme.firstMaterial = matiere
        return SCNNode(geometry: forme)
    }

    static func barre(de debut: SCNVector3, vers fin: SCNVector3, largeur: CGFloat,
                      hauteur: CGFloat, matiere: SCNMaterial) -> SCNNode {
        let dx = fin.x - debut.x, dy = fin.y - debut.y, dz = fin.z - debut.z
        let noeud = boite(largeur, hauteur, sqrt(dx * dx + dy * dy + dz * dz), rayon: 0.018, matiere: matiere)
        noeud.position = SCNVector3((debut.x + fin.x) / 2, (debut.y + fin.y) / 2, (debut.z + fin.z) / 2)
        noeud.look(at: fin)
        return noeud
    }

    static func tube(de debut: SCNVector3, vers fin: SCNVector3, rayon: CGFloat, matiere: SCNMaterial) -> SCNNode {
        let dx = fin.x - debut.x, dy = fin.y - debut.y, dz = fin.z - debut.z
        let support = SCNNode()
        support.position = debut
        support.look(at: fin)
        let longueur = sqrt(dx * dx + dy * dy + dz * dz)
        let piece = cylindre(rayon, longueur, matiere: matiere)
        piece.eulerAngles.x = .pi / 2
        piece.position.z = -longueur / 2
        support.addChildNode(piece)
        return support
    }

    /// Ruban effilé : le pas diminue du pied au saumon de chaque pale.
    static func pale(sens: CGFloat) -> SCNGeometry {
        var sommets: [SCNVector3] = []
        var normales: [SCNVector3] = []
        var uv: [CGPoint] = []
        var indices: [Int32] = []
        let sections = 8
        for i in 0...sections {
            let t = CGFloat(i) / CGFloat(sections)
            let x = 0.035 + t * 0.485
            let largeur = (0.025 + sin(t * .pi) * 0.04) * (1 - 0.45 * t)
            let pas = sens * (0.38 - t * 0.3)
            for cote: CGFloat in [-1, 1] {
                sommets.append(SCNVector3(x, cote * largeur * sin(pas), -t * t * 0.065 + cote * largeur * cos(pas)))
                normales.append(SCNVector3(0, cos(pas), -sin(pas)))
                uv.append(CGPoint(x: t, y: (cote + 1) / 2))
            }
            if i < sections {
                let n = Int32(i * 2)
                indices += [n, n + 1, n + 2, n + 1, n + 3, n + 2]
            }
        }
        return SCNGeometry(sources: [.init(vertices: sommets), .init(normals: normales), .init(textureCoordinates: uv)],
                           elements: [SCNGeometryElement(indices: indices, primitiveType: .triangles)])
    }

    static func doux(_ action: SCNAction) -> SCNAction {
        action.timingMode = .easeInEaseOut
        return action
    }

}
