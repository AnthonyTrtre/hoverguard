// Drone 3D de Hoverguard pour Windows : même scène que macos/Sources/Drone.swift (SceneKit), refaite avec three.js.
// Rendu « studio » : éclairage d'environnement, matériaux PBR, ombres douces, sol réfléchissant qui se fond dans le
// noir, une dizaine de lasers qui balaient tout l'écran et une grille de scan. Trois modes : posé (aucun agent),
// en vol (agents actifs), approche (quelqu'un touche le clavier : le drone fonce vers lui).
import * as THREE from 'three';
import { RoomEnvironment } from './three/RoomEnvironment.js';
import { Reflector } from './three/Reflector.js';
import { RoundedBoxGeometry } from './three/RoundedBoxGeometry.js';

const parametres = new URLSearchParams(location.search);
/** Écran principal : rendu complet. Autres écrans : sans ombres ni reflet, 20 images/s (comme sur macOS). */
const principal = parametres.get('principal') !== '0';
const IPS_MAX = principal ? 30 : 20;
const CYAN = new THREE.Color(0x14ccff), ROUGE = new THREE.Color(0xff0f09), VERT = new THREE.Color(0x1fff6b);
const ORANGE = new THREE.Color(0xff8a1e), BLANC = new THREE.Color(0xffffff);
const COUCHE_EFFETS = 1;  // lasers, grilles, halos : ni ombres ni reflet
const COUCHE_SOL = 2;     // le sol ne se reflète pas dans lui-même

// ── Rendu ───────────────────────────────────────────────────────────────────────────────────────────────────────────
const rendu = new THREE.WebGLRenderer({ antialias: true, powerPreference: principal ? 'high-performance' : 'low-power' });
rendu.setPixelRatio(Math.min(window.devicePixelRatio || 1, principal ? 1.5 : 0.75));
rendu.setClearColor(0x000000, 1);
rendu.outputColorSpace = THREE.SRGBColorSpace;
rendu.toneMapping = THREE.ACESFilmicToneMapping;
rendu.toneMappingExposure = 1.05;
rendu.shadowMap.enabled = principal;
rendu.shadowMap.type = THREE.PCFSoftShadowMap;
document.body.prepend(rendu.domElement);

const scene = new THREE.Scene();
scene.background = new THREE.Color(0x000000);
const pmrem = new THREE.PMREMGenerator(rendu);
scene.environment = pmrem.fromScene(new RoomEnvironment(rendu), 0.04).texture;

const camera = new THREE.PerspectiveCamera(36, 16 / 10, 0.08, 100);
camera.layers.enable(COUCHE_EFFETS);
camera.layers.enable(COUCHE_SOL);
const VISEE = new THREE.Vector3(0, -1.05, 0);

// ── Matériaux ───────────────────────────────────────────────────────────────────────────────────────────────────────
const M = {
  peinture: new THREE.MeshPhysicalMaterial({ color: 0xe8ebee, metalness: 0.08, roughness: 0.3, clearcoat: 1, clearcoatRoughness: 0.07 }),
  titane: new THREE.MeshStandardMaterial({ color: 0x3f4247, metalness: 0.78, roughness: 0.32 }),
  noir: new THREE.MeshStandardMaterial({ color: 0x0b0b0c, metalness: 0.02, roughness: 0.62 }),
  argent: new THREE.MeshStandardMaterial({ color: 0xc9ccd0, metalness: 1, roughness: 0.22 }),
  carbone: new THREE.MeshPhysicalMaterial({ color: 0x15171a, metalness: 0.25, roughness: 0.38, clearcoat: 0.7, clearcoatRoughness: 0.15 }),
  verre: new THREE.MeshPhysicalMaterial({ color: 0x03090c, metalness: 0.2, roughness: 0.05, clearcoat: 1, clearcoatRoughness: 0.03 }),
  capteur: new THREE.MeshPhysicalMaterial({ color: 0x061218, metalness: 0.45, roughness: 0.08, clearcoat: 1 }),
};
const lueur = (couleur, opacite = 1) => new THREE.MeshBasicMaterial({
  color: couleur, transparent: true, opacity: opacite, blending: THREE.AdditiveBlending, depthWrite: false, toneMapped: false,
});

// ── Petits outils de géométrie ──────────────────────────────────────────────────────────────────────────────────────
const maille = (geometrie, matiere, parent, position) => {
  const m = new THREE.Mesh(geometrie, matiere);
  if (position) m.position.copy(position);
  m.castShadow = m.receiveShadow = true;
  if (parent) parent.add(m);
  return m;
};
const cylindre = (rayon, hauteur, matiere, parent, segments = 32) =>
  maille(new THREE.CylinderGeometry(rayon, rayon, hauteur, segments), matiere, parent);
const boite = (l, h, p, rayon, matiere, parent) =>
  maille(rayon > 0 ? new RoundedBoxGeometry(l, h, p, 3, rayon) : new THREE.BoxGeometry(l, h, p), matiere, parent);
const V = (x, y, z) => new THREE.Vector3(x, y, z);
/** Barre ou tube orienté d'un point à un autre. */
function entre(geometrie, debut, fin, matiere, parent) {
  const m = maille(geometrie, matiere, parent);
  m.position.copy(debut).add(fin).multiplyScalar(0.5);
  m.quaternion.setFromUnitVectors(V(0, 1, 0), fin.clone().sub(debut).normalize());
  return m;
}
const tube = (debut, fin, rayon, matiere, parent) =>
  entre(new THREE.CylinderGeometry(rayon, rayon, debut.distanceTo(fin), 16), debut, fin, matiere, parent);
const effet = (objet) => { objet.traverse(o => { o.layers.set(COUCHE_EFFETS); o.castShadow = false; o.receiveShadow = false; }); return objet; };

/** Largeur de la coque (demi-largeur en x) selon la position z : un fuseau effilé vers l'arrière. */
const largeurCoque = (z) => 0.36 * Math.pow(Math.max(0, 1 - Math.pow(Math.abs(z + 0.02) / 0.68, 2.4)), 0.42) * (z > 0 ? 1 : 0.92);

/** Panneau de carénage : profil du fuseau extrudé avec un biseau généreux, posé à plat. */
function carenage(dessus, hauteur) {
  const forme = new THREE.Shape();
  const pas = 48;
  for (let i = 0; i <= pas; i++) {
    const z = -0.66 + (1.32 * i) / pas;
    const x = largeurCoque(z) - 0.06;
    i === 0 ? forme.moveTo(x, z) : forme.lineTo(x, z);
  }
  for (let i = pas; i >= 0; i--) forme.lineTo(-(largeurCoque(-0.66 + (1.32 * i) / pas) - 0.06), -0.66 + (1.32 * i) / pas);
  const g = new THREE.ExtrudeGeometry(forme, { depth: hauteur, bevelEnabled: true, bevelThickness: 0.07, bevelSize: 0.06, bevelSegments: 8, curveSegments: 24 });
  g.rotateX(dessus ? -Math.PI / 2 : Math.PI / 2);
  g.translate(0, dessus ? 0.005 : -0.005, 0);
  return g;
}

/** Pale effilée et vrillée. */
function pale(sens) {
  const g = new THREE.BoxGeometry(0.5, 0.008, 0.075, 24, 1, 2);
  const p = g.attributes.position;
  for (let i = 0; i < p.count; i++) {
    const x = p.getX(i), t = x / 0.25;
    const corde = 1 - Math.abs(t) * 0.55;
    const vrille = sens * (0.32 - Math.abs(t) * 0.22) * Math.sign(x || 1);
    const z = p.getZ(i) * corde, y = p.getY(i);
    p.setZ(i, z * Math.cos(vrille) - y * Math.sin(vrille) + 0.012 * Math.sign(x) * (1 - corde));
    p.setY(i, z * Math.sin(vrille) + y * Math.cos(vrille));
  }
  g.computeVertexNormals();
  return g;
}

/** Texture radiale (flou des hélices, halos, fondu du sol). */
function degrade(arrets) {
  const c = document.createElement('canvas');
  c.width = c.height = 256;
  const x = c.getContext('2d');
  const d = x.createRadialGradient(128, 128, 0, 128, 128, 128);
  for (const [position, couleur] of arrets) d.addColorStop(position, couleur);
  x.fillStyle = d;
  x.fillRect(0, 0, 256, 256);
  const t = new THREE.CanvasTexture(c);
  t.colorSpace = THREE.SRGBColorSpace;
  return t;
}

// ── Le drone ────────────────────────────────────────────────────────────────────────────────────────────────────────
const racine = new THREE.Group();       // position dans la scène
const attitude = new THREE.Group();     // vol stationnaire
const coque = new THREE.Group();        // cap et inclinaisons
scene.add(racine);
racine.add(attitude);
attitude.add(coque);
const rotors = [], pales = [], flous = [], feux = [];

// Coque : panneau peint dessus, panneau titane dessous, joint noir.
maille(carenage(true, 0.13), M.peinture, coque);
maille(carenage(false, 0.11), M.titane, coque);
const joint = maille(new THREE.TorusGeometry(1, 0.012, 8, 96), M.noir, coque);
joint.rotation.x = Math.PI / 2;
joint.scale.set(0.37, 0.68, 1);
// Batterie encastrée et ses verrous.
boite(0.34, 0.02, 0.54, 0.008, M.noir, coque).position.set(0, 0.215, -0.08);
boite(0.31, 0.09, 0.5, 0.025, M.peinture, coque).position.set(0, 0.255, -0.08);
for (const x of [-0.125, 0.125]) boite(0.032, 0.025, 0.13, 0.006, M.argent, coque).position.set(x, 0.303, -0.08);
for (const cote of [-1, 1]) {
  // Ouïes latérales.
  for (let i = 0; i < 7; i++) {
    const z = i * 0.055 - 0.3, o = boite(0.012, 0.055, 0.026, 0.003, M.noir, coque);
    o.position.set(cote * (largeurCoque(z) + 0.002), 0, z);
  }
  // Vis du capot.
  for (const z of [-0.27, 0.11]) cylindre(0.014, 0.009, M.argent, coque, 16).position.set(cote * 0.105, 0.305, z);
  // Antennes.
  const antenne = cylindre(0.019, 0.27, M.noir, coque, 12);
  antenne.position.set(cote * 0.27, 0.32, -0.46);
  antenne.rotation.z = cote * -0.2;
  // Capteurs stéréo avant et arrière.
  for (const z of [-0.62, 0.62]) {
    const c = cylindre(0.044, 0.025, M.noir, coque, 24);
    c.rotation.x = Math.PI / 2;
    c.position.set(cote * 0.15, 0.025, z);
    const lentille = maille(new THREE.SphereGeometry(1, 24, 12), M.capteur, coque);
    lentille.scale.set(0.028, 0.028, 0.008);
    lentille.position.set(cote * 0.15, 0.025, z + Math.sign(z) * 0.018);
  }
  // Bandeau lumineux latéral.
  const bandeau = maille(new RoundedBoxGeometry(0.006, 0.012, 0.46, 2, 0.0025), lueur(CYAN, 0.6), coque);
  bandeau.position.set(cote * (largeurCoque(0.08) + 0.012), 0.06, 0.08);
  feux.push(effet(bandeau));
}

// Bras en carbone, moteurs, hélices.
const MOTEURS = [V(-0.99, 0, 0.78), V(0.99, 0, 0.78), V(-0.99, 0, -0.78), V(0.99, 0, -0.78)];
const flouTexture = degrade([[0, 'rgba(255,255,255,0)'], [0.18, 'rgba(255,255,255,0.05)'], [0.86, 'rgba(210,225,235,0.32)'],
  [0.95, 'rgba(220,235,245,0.12)'], [1, 'rgba(255,255,255,0)']]);
MOTEURS.forEach((point, i) => {
  const depart = V(point.x * 0.23, -0.025, point.z * 0.3);
  const longueur = depart.distanceTo(point);
  const bras = maille(new RoundedBoxGeometry(0.135, 0.075, longueur, 3, 0.03), M.carbone, coque);
  bras.position.copy(depart).add(point).multiplyScalar(0.5);
  bras.lookAt(point.clone().applyMatrix4(coque.matrixWorld));
  const lisere = maille(new THREE.BoxGeometry(0.009, 0.004, longueur * 0.92), M.titane, coque);
  lisere.position.copy(bras.position).add(V(0, 0.04, 0));
  lisere.quaternion.copy(bras.quaternion);
  const moteur = new THREE.Group();
  moteur.position.copy(point);
  coque.add(moteur);
  for (const [r, h, y, m] of [[0.145, 0.045, 0.015, M.noir], [0.116, 0.15, 0.105, M.titane], [0.12, 0.018, 0.18, M.argent], [0.076, 0.028, 0.2, M.noir]])
    cylindre(r, h, m, moteur, 40).position.y = y;
  for (let cran = 0; cran < 8; cran++) {
    const a = (cran * Math.PI) / 4, ailette = boite(0.027, 0.075, 0.013, 0.004, M.noir, moteur);
    ailette.position.set(Math.sin(a) * 0.115, 0.105, Math.cos(a) * 0.115);
    ailette.rotation.y = a;
  }
  const rotor = new THREE.Group();
  rotor.position.y = 0.23;
  moteur.add(rotor);
  rotors.push(rotor);
  const bipale = new THREE.Group();
  for (const a of [0, Math.PI]) {
    const p = maille(pale(i % 2 === 0 ? 1 : -1), M.carbone.clone(), bipale);
    p.position.x = 0.25 * Math.cos(a);
    p.rotation.y = a;
  }
  rotor.add(bipale);
  pales.push(bipale);
  cylindre(0.047, 0.045, M.argent, rotor, 24);
  const flou = new THREE.Mesh(new THREE.CircleGeometry(0.52, 48), new THREE.MeshBasicMaterial({
    map: flouTexture, transparent: true, opacity: 0.55, depthWrite: false, side: THREE.DoubleSide }));
  flou.rotation.x = -Math.PI / 2;
  flou.position.y = 0.23;
  moteur.add(effet(flou));
  flous.push(flou);
  const feu = maille(new THREE.SphereGeometry(0.023, 16, 8), lueur(ORANGE, 0.9), coque);
  feu.position.set(point.x * 1.035, -0.055, point.z);
  feux.push(effet(feu));
});
const feuArriere = maille(new THREE.SphereGeometry(0.018, 16, 8), lueur(BLANC, 0.7), coque);
feuArriere.position.set(0, 0.09, -0.655);
feux.push(effet(feuArriere));

// Train d'atterrissage.
for (const x of [-0.4, 0.4]) {
  for (const z of [-0.36, 0.36]) tube(V(x * 0.72, -0.08, z), V(x * 1.35, -0.54, z * 1.2), 0.027, M.titane, coque);
  tube(V(x * 1.35, -0.54, -0.63), V(x * 1.35, -0.54, 0.66), 0.033, M.noir, coque);
  for (const z of [-0.5, 0.51]) boite(0.1, 0.045, 0.18, 0.022, M.noir, coque).position.set(x * 1.35, -0.56, z);
}

// Nacelle et caméra stabilisée.
cylindre(0.07, 0.14, M.titane, coque).position.set(0, -0.19, 0.32);
const cardan = new THREE.Group(), roulis = new THREE.Group(), tangage = new THREE.Group();
cardan.position.set(0, -0.32, 0.36);
coque.add(cardan);
cardan.add(roulis);
roulis.add(tangage);
const axe = cylindre(0.055, 0.07, M.titane, roulis);
axe.rotation.x = Math.PI / 2;
axe.position.set(0, 0.06, -0.12);
for (const x of [-0.17, 0.17]) {
  boite(0.035, 0.19, 0.09, 0.014, M.argent, roulis).position.set(x, 0.04, 0);
  const a = cylindre(0.043, 0.045, M.noir, roulis);
  a.rotation.z = Math.PI / 2;
  a.position.x = x;
}
maille(new THREE.SphereGeometry(1, 40, 24), M.titane, tangage).scale.set(0.145, 0.13, 0.14);
const objectif = cylindre(0.095, 0.085, M.noir, tangage, 40);
objectif.rotation.x = Math.PI / 2;
objectif.position.z = 0.13;
const bague = maille(new THREE.TorusGeometry(0.078, 0.009, 12, 48), M.argent, tangage);
bague.position.z = 0.177;
const verre = maille(new THREE.SphereGeometry(1, 32, 16), M.verre, tangage);
verre.scale.set(0.071, 0.071, 0.018);
verre.position.z = 0.18;

// ── Studio : lumières, sol réfléchissant, ombre ─────────────────────────────────────────────────────────────────────
const SOL = -0.86;
const principale = new THREE.DirectionalLight(0xfff5e6, 2.6);
principale.position.set(-3, 7, 5);
principale.castShadow = principal;
principale.shadow.mapSize.set(2048, 2048);
principale.shadow.bias = -0.0004;
Object.assign(principale.shadow.camera, { left: -4, right: 4, top: 4, bottom: -4, near: 0.5, far: 24 });
scene.add(principale);
const bleue = new THREE.DirectionalLight(0x85d1ff, 1.2);
bleue.position.set(2, 3, -4);
scene.add(bleue);
const douce = new THREE.DirectionalLight(0xebf4ff, 0.8);
douce.position.set(5, 2, 6);
scene.add(douce);

let reflet = null;
if (principal) {
  // Reflet basse résolution, volontairement adouci ; seules les pièces solides s'y reflètent.
  reflet = new Reflector(new THREE.CircleGeometry(30, 64), {
    textureWidth: Math.round(innerWidth * 0.35), textureHeight: Math.round(innerHeight * 0.35), color: 0x8a8f96, multisample: 0 });
  reflet.rotation.x = -Math.PI / 2;
  reflet.position.y = SOL - 0.002;
  reflet.camera.layers.set(0);
  scene.add(reflet);
}
// Le sol : un voile sombre au-dessus du reflet, plus opaque vers les bords pour fondre le studio dans le noir.
const fondu = degrade([[0, 'rgba(255,255,255,0.80)'], [0.22, 'rgba(255,255,255,0.86)'], [0.55, 'rgba(255,255,255,0.97)'], [0.7, 'rgba(255,255,255,1)']]);
const surface = new THREE.Mesh(new THREE.CircleGeometry(30, 64), new THREE.MeshBasicMaterial({
  color: 0x000000, transparent: true, alphaMap: fondu, depthWrite: false }));
surface.rotation.x = -Math.PI / 2;
surface.position.y = SOL;
surface.renderOrder = -2;  // d'abord le fondu, puis l'ombre et la lumière par-dessus
surface.layers.set(COUCHE_SOL);
scene.add(surface);
const ombre = new THREE.Mesh(new THREE.PlaneGeometry(14, 14), new THREE.ShadowMaterial({ opacity: 0.45 }));
ombre.rotation.x = -Math.PI / 2;
ombre.position.y = SOL + 0.001;
ombre.receiveShadow = true;
ombre.layers.set(COUCHE_SOL);
ombre.renderOrder = -1;
scene.add(ombre);
// Flaque de lumière froide sous le drone, comme sous les projecteurs d'un studio ; le reste se perd dans le noir.
const flaque = new THREE.Mesh(new THREE.CircleGeometry(7, 64), new THREE.MeshBasicMaterial({
  map: degrade([[0, 'rgba(150,175,200,0.20)'], [0.35, 'rgba(110,130,155,0.10)'], [1, 'rgba(0,0,0,0)']]),
  transparent: true, depthWrite: false, blending: THREE.AdditiveBlending, toneMapped: false }));
flaque.rotation.x = -Math.PI / 2;
flaque.position.y = SOL + 0.0015;
flaque.layers.set(COUCHE_SOL);
scene.add(flaque);
if (!principal) {
  // Écrans secondaires : une tache sombre douce sous le drone tient lieu d'ombre.
  const tache = new THREE.Mesh(new THREE.CircleGeometry(1.4, 48), new THREE.MeshBasicMaterial({
    map: degrade([[0, 'rgba(0,0,0,0.7)'], [1, 'rgba(0,0,0,0)']]), transparent: true, depthWrite: false }));
  tache.rotation.x = -Math.PI / 2;
  tache.position.y = SOL + 0.002;
  scene.add(tache);
}

// ── Balayage : grilles de scan, anneaux, repères, lasers ────────────────────────────────────────────────────────────
const effets = new THREE.Group();
scene.add(effets);
const balayage = new THREE.Group();
balayage.position.y = SOL + 0.004;
effets.add(balayage);
const anneau = (rayon, epaisseur, couleur, opacite) => {
  const m = new THREE.Mesh(new THREE.RingGeometry(rayon - epaisseur, rayon + epaisseur, 128), lueur(couleur, opacite));
  m.rotation.x = -Math.PI / 2;
  return m;
};
const cercle = anneau(1.08, 0.006, CYAN, 0.35);
balayage.add(cercle);
balayage.add(anneau(0.92, 0.002, CYAN, 0.14));
for (let i = 0; i < 48; i++) {
  const a = (i * Math.PI) / 24, l = i % 4 === 0 ? 0.12 : 0.035;
  const r = new THREE.Mesh(new THREE.PlaneGeometry(0.007, l), lueur(CYAN, 0.28));
  r.rotation.x = -Math.PI / 2;
  r.rotation.z = -a;
  r.position.set(Math.sin(a) * 1.26, 0.002, Math.cos(a) * 1.26);
  balayage.add(r);
}
const grilles = [0, 1].map(i => {
  const g = new THREE.GridHelper(40, 100, i === 0 ? CYAN : VERT, i === 0 ? CYAN : VERT);
  g.material = lueur(i === 0 ? CYAN : VERT, i === 0 ? 0.07 : 0.03);
  g.position.y = SOL + 0.004 + i * 0.002;
  effets.add(g);
  return g;
});
const bandeScan = new THREE.Mesh(new THREE.PlaneGeometry(40, 0.05), lueur(CYAN, 0.45));
bandeScan.rotation.x = -Math.PI / 2;
bandeScan.position.y = SOL + 0.006;
effets.add(bandeScan);

// Dix lasers : six visent le sol, quatre balaient le ciel (donc tout l'écran).
const lasers = [];
const impacts = new THREE.Group();
effets.add(impacts);
for (let i = 0; i < 10; i++) {
  const couleur = i === 2 || i === 7 ? VERT : (i % 2 === 0 ? CYAN : ROUGE);
  const emetteur = new THREE.Object3D();
  emetteur.position.set(i % 2 === 0 ? -0.3 : 0.3, -0.17, i < 6 ? 0.28 : -0.25);
  coque.add(emetteur);
  const faisceau = new THREE.Group();
  for (const [r, o] of [[0.0028, 0.7], [0.012, 0.07]]) {
    const c = new THREE.Mesh(new THREE.CylinderGeometry(r, r, 1, 6, 1, true), lueur(couleur, o));
    c.position.y = 0.5;
    faisceau.add(c);
  }
  effets.add(faisceau);
  const impact = new THREE.Group();
  if (i < 6) {
    const point = new THREE.Mesh(new THREE.SphereGeometry(0.024, 12, 6), lueur(couleur, 0.95));
    point.scale.y = 0.2;
    impact.add(point);
    const halo = anneau(0.075, 0.003, couleur, 0.4);
    impact.add(halo);
    impact.position.y = SOL + 0.01;
    impacts.add(impact);
  }
  lasers.push({ emetteur, faisceau, impact, sol: i < 6, i, phase: Math.random() * 10 });
}
effet(effets);

// ── Cadrage ─────────────────────────────────────────────────────────────────────────────────────────────────────────
let rapport = 1.6;
const positionApproche = V(0, 0, 0);
let capApproche = 0;
function cadrer() {
  const l = innerWidth, h = innerHeight;
  rendu.setSize(l, h, false);
  rapport = l / Math.max(1, h);
  camera.aspect = rapport;
  // Champ vertical constant du portable à l'ultra-large ; on recule si l'écran est en portrait.
  const recul = Math.max(1, 1.15 / rapport);
  camera.position.set(4.6 * recul, -1.05 + 2.5 * recul, 8.6 * recul);
  camera.lookAt(VISEE);
  camera.updateProjectionMatrix();
  camera.updateMatrixWorld();
  // En gros plan : environ 45 % de la hauteur, au-dessus du milieu de l'écran.
  const d = 4.45 * Math.max(1, 1.45 / rapport);
  positionApproche.set(0, d * Math.tan((18 * Math.PI) / 180) * 0.3, -d).applyMatrix4(camera.matrixWorld);
  capApproche = Math.atan2(camera.position.x - positionApproche.x, camera.position.z - positionApproche.z);
  if (reflet) reflet.getRenderTarget().setSize(Math.round(l * 0.35), Math.round(h * 0.35));
}
addEventListener('resize', cadrer);
cadrer();

/** Point du ciel visé par le laser aérien n° k au temps t : il balaie l'écran d'un bord à l'autre. */
function cibleCiel(k, t) {
  const cote = k % 2 === 0 ? -1 : 1, profondeur = k < 2 ? 7 : 1.8;
  const demi = profondeur * Math.tan((18 * Math.PI) / 180);
  const u = 0.5 - 0.5 * Math.cos(t / (2.8 + k * 0.65));
  const x = THREE.MathUtils.lerp(cote * demi * rapport * 1.2, -cote * demi * rapport * 0.85, u);
  const y = THREE.MathUtils.lerp(demi * 0.9, demi * (k < 2 ? 0.25 : -0.7), u);
  return V(x, y, -profondeur).applyMatrix4(camera.matrixWorld);
}
/** Point du sol visé par le laser n° i : va et vient sur une large zone. */
function cibleSol(i, t) {
  const a = (i * Math.PI) / 3, portee = Math.max(5, 5 * rapport);
  const u = 0.5 - 0.5 * Math.cos(t / (2.5 + i * 0.55));
  const debut = V(Math.sin(a) * portee, SOL, Math.cos(a) * 5), fin = V(Math.sin(a + 1.2) * portee, SOL, Math.cos(a + 1.2) * 7);
  return debut.lerp(fin, u);
}

// ── États et animation ──────────────────────────────────────────────────────────────────────────────────────────────
const etat = { enVol: false, approche: false };
const lisse = { y: -0.2775, pos: V(0, -0.2775, 0), cap: 0, inclinaison: 0, rotors: 0, effets: 0, flou: 0 };
let mode = 'pose', depuis = 0, horloge = 0;
const amortir = (actuel, cible, vitesse, dt) => actuel + (cible - actuel) * (1 - Math.exp(-vitesse * dt));
const POSE = V(0, -0.2775, 0), VOL = V(0, 0.45, 0);

function changerMode() {
  const nouveau = etat.approche ? 'approche' : (etat.enVol ? 'surveillance' : 'pose');
  if (nouveau === mode) return;
  mode = nouveau;
  depuis = horloge;
  document.body.classList.toggle('approche', mode === 'approche');
  // Couleur des feux : rouge et cyan en surveillance, orange qui respire sinon.
  feux.forEach((f, i) => {
    f.material.color.copy(mode === 'surveillance' ? (i < 2 ? CYAN : (i < 6 ? (i % 2 === 0 ? ROUGE : CYAN) : CYAN)) : ORANGE);
  });
}

function animer(dt) {
  horloge += dt;
  const t = horloge, ecoule = t - depuis;
  const vol = mode !== 'pose', surveillance = mode === 'surveillance', approche = mode === 'approche';

  // Trajet : l'approche est un piqué rapide (≈ 0,8 s), le reste plus posé.
  const destination = approche ? positionApproche : (vol ? VOL : POSE);
  const vitesse = approche ? 5.5 : (ecoule < 3 ? 2.2 : 3);
  lisse.pos.x = amortir(lisse.pos.x, destination.x, vitesse, dt);
  lisse.pos.y = amortir(lisse.pos.y, destination.y, vitesse, dt);
  lisse.pos.z = amortir(lisse.pos.z, destination.z, vitesse, dt);
  racine.position.copy(lisse.pos);

  // Vol stationnaire.
  attitude.position.y = surveillance ? 0.0325 * Math.sin(t * 1.35) + 0.0075 : amortir(attitude.position.y, 0, 4, dt);

  // Cap, tangage, roulis.
  const capCible = approche ? capApproche : (surveillance ? 0.015 + 0.135 * Math.sin(t / 1.5) : 0);
  lisse.cap = amortir(lisse.cap, capCible, approche ? 6 : 1.6, dt);
  const piquer = approche && ecoule < 0.45 ? 0.16 : 0;
  lisse.inclinaison = amortir(lisse.inclinaison, piquer + (surveillance ? 0.0225 * Math.sin(t / 1.3) : 0), 5, dt);
  coque.rotation.set(lisse.inclinaison, lisse.cap, surveillance ? 0.022 * Math.sin(t / 1.45) : amortir(coque.rotation.z, 0, 4, dt), 'YXZ');

  // Nacelle : balaie le terrain en surveillance, fixe l'intrus pendant l'approche.
  if (approche) {
    const ici = new THREE.Vector3(), q = new THREE.Quaternion();
    cardan.getWorldPosition(ici);
    const vise = new THREE.Matrix4().lookAt(camera.position, ici, V(0, 1, 0));
    q.setFromRotationMatrix(vise);
    const parent = new THREE.Quaternion();
    coque.getWorldQuaternion(parent);
    cardan.quaternion.slerp(parent.invert().multiply(q), 1 - Math.exp(-8 * dt));
    tangage.rotation.x = amortir(tangage.rotation.x, 0, 6, dt);
  } else {
    const lacet = surveillance ? 0.04 + 0.34 * Math.sin(t / 1.3) : 0;
    cardan.quaternion.slerp(new THREE.Quaternion().setFromEuler(new THREE.Euler(0, lacet, 0)), 1 - Math.exp(-3 * dt));
    tangage.rotation.x = amortir(tangage.rotation.x, surveillance ? 0.03 + 0.15 * Math.sin(t / 1.0) : 0, 3, dt);
    roulis.rotation.z = surveillance ? 0.02 * Math.sin(t / 1.5) : amortir(roulis.rotation.z, 0, 3, dt);
  }

  // Hélices : 11 tours/s en vol, freinage progressif à l'atterrissage.
  lisse.rotors = amortir(lisse.rotors, vol ? (Math.PI * 2) / 0.09 : 0, vol ? 3 : 0.9, dt);
  const enRotation = lisse.rotors > 12;
  rotors.forEach((r, i) => { r.rotation.y += (i % 2 === 0 ? 1 : -1) * lisse.rotors * dt; });
  lisse.flou = amortir(lisse.flou, enRotation ? 1 : 0, 6, dt);
  flous.forEach(f => { f.visible = lisse.flou > 0.02; f.material.opacity = 0.55 * lisse.flou; });
  pales.forEach(p => p.children.forEach(m => {
    m.material.transparent = enRotation;
    m.material.opacity = enRotation ? 0.14 : 1;
    m.castShadow = !enRotation;
  }));

  // Feux : respiration lente quand le drone n'est pas en surveillance.
  const respiration = surveillance ? 0.8 : 0.45 + 0.4 * (0.5 + 0.5 * Math.sin(t * 2.2));
  feux.forEach(f => { f.material.opacity = respiration; });

  // Effets de surveillance (fondu), remis seulement une fois le drone revenu de l'approche.
  lisse.effets = amortir(lisse.effets, surveillance && ecoule > (depuis > 0 ? 0.6 : 0) ? 1 : 0, surveillance ? 2.5 : 8, dt);
  effets.visible = lisse.effets > 0.01;
  if (effets.visible) {
    const k = lisse.effets;
    cercle.scale.setScalar(1.45 - 0.45 * Math.cos(t * 1.2));
    cercle.material.opacity = 0.35 * k;
    grilles[0].position.z = ((t * 0.73) % 0.4);
    grilles[1].position.z = -((t * 0.51) % 0.4);
    grilles[0].material.opacity = 0.07 * k;
    grilles[1].material.opacity = 0.03 * k;
    bandeScan.position.z = -8 + ((t * 2.9) % 16);
    bandeScan.material.opacity = 0.45 * k;
    balayage.position.x = racine.position.x;
    balayage.position.z = racine.position.z;
    balayage.children.forEach(c => { if (c !== cercle) c.material.opacity = (c.userData.o ??= c.material.opacity) * k; });
    coque.updateMatrixWorld(true);
    const depart = new THREE.Vector3();
    let ciel = 0;
    for (const l of lasers) {
      const but = l.sol ? cibleSol(l.i, t + l.phase) : cibleCiel(ciel++, t + l.phase);
      l.emetteur.getWorldPosition(depart);
      // Faisceau long : prolongé au-delà de la cible, coupé au sol.
      const direction = but.clone().sub(depart).normalize();
      let longueur = l.sol ? depart.distanceTo(but) : 60;
      if (!l.sol && direction.y < -0.01) longueur = Math.min(longueur, (SOL - depart.y) / direction.y);
      l.faisceau.position.copy(depart);
      l.faisceau.quaternion.setFromUnitVectors(V(0, 1, 0), direction);
      l.faisceau.scale.set(1, longueur, 1);
      l.faisceau.children.forEach((c, j) => { c.material.opacity = (j === 0 ? 0.7 : 0.07) * k; });
      if (l.sol) { l.impact.position.set(but.x, SOL + 0.01, but.z); l.impact.children.forEach(c => { c.material.opacity = (c.userData.o ??= c.material.opacity) * k; }); }
    }
  }
}

// ── Boucle de rendu, plafonnée en images par seconde ────────────────────────────────────────────────────────────────
let precedent = performance.now(), images = 0, mesure = precedent, pret = false;
const intervalle = 1000 / IPS_MAX;
function boucle(maintenant) {
  requestAnimationFrame(boucle);
  if (maintenant - precedent < intervalle - 2) return;
  const dt = Math.min(0.1, (maintenant - precedent) / 1000);
  precedent = maintenant;
  changerMode();
  animer(dt);
  rendu.render(scene, camera);
  images++;
  if (!pret) { pret = true; envoyer({ pret: true }); }
  if (maintenant - mesure > 5000) {
    envoyer({ ips: Math.round((images * 10000) / (maintenant - mesure)) / 10 });
    images = 0;
    mesure = maintenant;
  }
}
requestAnimationFrame(boucle);

// ── Textes et messages de Hoverguard ────────────────────────────────────────────────────────────────────────────────
const $ = (id) => document.getElementById(id);
function envoyer(message) { try { window.chrome?.webview?.postMessage(message); } catch { /* hors de Hoverguard */ } }
function heure() { $('heure').textContent = new Date().toLocaleTimeString('fr-FR', { hour: '2-digit', minute: '2-digit' }); }
heure();
setInterval(heure, 10_000);

function appliquer(m) {
  etat.enVol = !!m.enVol;
  etat.approche = !!m.approche;
  $('phrase').textContent = m.phrase ?? '';
  $('compteur').textContent = m.compteur ?? '';
  $('compteur').classList.toggle('actif', etat.enVol);
  $('equipe').textContent = m.equipe ?? '';
  $('batterie').textContent = m.batterie ?? '';
  $('textes').classList.toggle('cache', etat.approche);
  $('aide').textContent = etat.approche ? 'Échap pour laisser le drone surveiller' : 'Appuyez sur une touche pour vous identifier';
}
window.chrome?.webview?.addEventListener('message', (e) => appliquer(e.data));
if (!window.chrome?.webview) {
  // Ouverte seule dans un navigateur (développement) : démonstration des trois modes.
  appliquer({ phrase: 'Ici, on bosse. Admirez avec les yeux seulement 😉', compteur: '3 agents au travail (1 principal + 2 sous-agents)',
    equipe: "L'équipe tourne à plein : 3 agents sur le pont", enVol: true });
  let n = 0;
  setInterval(() => { n++; appliquer({ phrase: 'Démonstration', compteur: '', equipe: '', enVol: n % 3 !== 2, approche: n % 3 === 1 }); }, 6000);
}
