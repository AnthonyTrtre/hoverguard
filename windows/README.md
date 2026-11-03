# Hoverguard pour Windows

**Un drone garde votre écran pendant que vos agents IA travaillent.** Développé par **Utahpik**.

Version Windows 10 (version 2004 ou plus récente) et Windows 11, à parité avec la version macOS (`../macos`).
Hoverguard vit dans la zone de notification, près de l'horloge : un clic gauche ou droit sur le bouclier ouvre le menu.

> Hoverguard protège **la vue et le clavier**. Ce n'est **pas** un verrou de sécurité : pour une vraie protection,
> verrouillez Windows (⊞ Win + L). Lisez [SECURITY.md](../SECURITY.md) avant de compter dessus.

## Installer

### Installer
Téléchargez `Hoverguard-Setup.exe` dans les [Releases](https://github.com/AnthonyTrtre/hoverguard/releases/latest) et
lancez-le. L'installateur ([Velopack](https://velopack.io), MIT) installe Hoverguard pour votre compte dans
`%LOCALAPPDATA%\Hoverguard` (aucun droit d'administrateur : c'est aussi ce qui permet des mises à jour silencieuses),
ajoute un raccourci au menu Démarrer et ouvre ses réglages : choisissez alors votre **mot de passe Hoverguard**,
sans lequel la garde ne se pose pas. Le fichier téléchargé peut ensuite être supprimé. Désinstaller :
Paramètres › Applications › Applications installées › Hoverguard (le schéma `hoverguard://` et l'ouverture au
démarrage sont retirés).

Hoverguard n'a pas de fenêtre principale : il vit dans la zone de notification, près de l'horloge. Windows 11 range
les nouvelles icônes sous la flèche **^** ; pour la garder visible, faites-la glisser dans la barre des tâches. Relancer
Hoverguard (menu Démarrer, double-clic) ouvre ses réglages.

**Mises à jour.** Une fois par jour, Hoverguard lit la dernière Release GitHub, télécharge la nouvelle version en
arrière-plan et l'installe quand le PC est inutilisé depuis 10 minutes ou quand vous le quittez, jamais pendant une
garde. Désactivable dans Réglages › Garde ; menu de l'icône › *Rechercher des mises à jour…* pour le faire tout de suite.

**Avertissement SmartScreen.** L'installateur n'est pas signé (pas encore de certificat de signature de code). Edge
peut demander de *Conserver* le fichier, puis Windows affiche « Windows a protégé votre ordinateur » : cliquez sur
*Informations complémentaires*, puis *Exécuter quand même*. Si vous préférez, compilez-le vous-même (ci-dessous).

### Compiler depuis les sources
Il faut le SDK .NET 8.
```
cd windows
dotnet test tests/Hoverguard.Tests
dotnet publish src/Hoverguard/Hoverguard.csproj -c Release -r win-x64 -o publication
```
L'exe est dans `windows/publication/Hoverguard.exe`. Depuis Linux ou macOS, `dotnet build` compile aussi le projet
(`EnableWindowsTargeting`), sans pouvoir le lancer. Édition de lancement (Pro offert à vie au premier lancement, voir
« Hoverguard Pro ») : ajoutez `-p:EditionLancement=true` à `dotnet publish`.

### Ce dont Hoverguard a besoin
- **Windows 10 version 2004 (build 19041) ou plus récent** : avant, Windows ne sait pas exclure une fenêtre des
  captures d'écran, et les agents ne verraient qu'un écran noir. Hoverguard refuse alors de poser la garde.
- **Le runtime WebView2** (moteur Edge), présent d'office sur Windows 11 et sur les Windows 10 à jour. Sans lui, le
  voile s'affiche quand même, avec les textes mais sans le drone 3D.

## Au premier lancement
- Hoverguard s'inscrit pour s'ouvrir à l'ouverture de session (réglable dans Réglages › Garde).
- Il inscrit le schéma `hoverguard://` dans votre profil (`HKCU\Software\Classes\hoverguard`), pour que vos agents
  puissent le prévenir.
- Aucune autorisation spéciale n'est demandée : les crochets clavier et souris bas niveau ne demandent pas de droits sous
  Windows (contrairement à l'autorisation Accessibilité de macOS).

## Raccourcis
| Action | Raccourci |
|---|---|
| Poser la garde | **Ctrl + Win + W** (ou le menu de l'icône) |
| Réveiller la garde | n'importe quelle touche ou clic (le drone s'approche) |
| Valider le mot de passe | Entrée |
| Renvoyer le drone surveiller | **Échap** (ou une minute sans rien taper) |

Pourquoi **Ctrl + Win + W** : Windows ne l'utilise pas (ses raccourcis Ctrl + Win servent aux bureaux virtuels, au
Narrateur et aux filtres de couleur), il ne touche pas AltGr (= Ctrl + Alt, indispensable pour taper « @ » ou « € »
sur un clavier français), et c'est le même geste que ⌃⌘W sur Mac. La lettre est celle de la touche marquée W, en
AZERTY comme en QWERTY. Ctrl + Alt + W a été écarté : sur certains claviers, c'est AltGr + W.

**Ctrl + Alt + Suppr** et **Win + L** ne passent jamais par Hoverguard : Windows les réserve. C'est voulu : ils restent
toujours disponibles pour vous (et pour un intrus, qui n'en tirera qu'un écran de verrouillage).

## Brancher vos agents
Réglages › Agents : un clic ajoute les « hooks » de **Claude Code**, **Codex**, **Gemini CLI** ou **Cursor**
(`%USERPROFILE%\.claude\settings.json`, `.codex\hooks.json`, `.gemini\settings.json`, `.cursor\hooks.json`), un autre
(« Débrancher ») les retire. Écriture prudente :
- si le fichier est un **lien symbolique** (dépôt de dotfiles), Hoverguard écrit dans sa cible et le lien reste ;
- le fichier garde ses **droits** (ACL sous Windows, conservées par `ReplaceFile`) ; l'écriture est atomique ;
- **une seule** copie, `….avant-hoverguard`, remplacée à chaque écriture ;
- un fichier qu'il ne sait pas relire sans perte (**commentaires**, virgules en trop, clé en double, racine qui n'est pas
  un objet) est **refusé** avec un message, sans y toucher ;
- seuls les hooks de Hoverguard sont retirés : un groupe qui contient aussi l'un de vos hooks reste en place.

Chaque hook lance Windows PowerShell par son **chemin absolu** (`C:/Windows/System32/WindowsPowerShell/v1.0/powershell.exe`,
pris dans le vrai dossier de Windows au moment du branchement ; un `PATH` piégé ne peut pas le remplacer). Le script lit
l'identifiant de session sur l'entrée standard, en garde 4 caractères et ouvre
`hoverguard://agent?nom=Claude%20a1b2&etat=travail` (ou `attente`, `fini`, `sous-agent-debut`, `sous-agent-fin`) par
`Start-Process`, sans rien écrire sur la sortie (sauf le JSON qu'exigent Gemini CLI et Cursor). Selon l'outil, un hook
est exécuté par cmd.exe, PowerShell ou Git Bash, qui ont chacun leurs règles de guillemets, de `$`, de `%VAR%` et de `\` :
la commande n'en contient aucun (chemin en `/`, script en `-EncodedCommand` Base64). Pour lire un hook en clair :
```
[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('<le texte après -EncodedCommand>'))
```
Mêmes événements que sur Mac : Claude Code (`UserPromptSubmit`, `Notification`, `Stop`, `PreToolUse` sur `Agent|Task`,
`SubagentStop`), Codex (`UserPromptSubmit`, `PermissionRequest`, `Stop`, `SubagentStart`, `SubagentStop`), Gemini CLI
(`BeforeAgent`, `Notification`, `AfterAgent`, délai en millisecondes), Cursor (`version: 1`, forme plate,
`beforeSubmitPrompt`, `stop`, `subagentStart`, `subagentStop`).
Codex demande d'approuver les nouveaux hooks une fois (`/hooks`). Cursor ne signale pas l'attente d'une réponse.

Vous pouvez aussi poser la garde depuis un script : `Start-Process "hoverguard://verrouiller"`.
**Aucune URL ne lève la garde.**

## Le voile, Chrome et les captures d'écran
Le voile est une fenêtre par écran, toujours au premier plan, avec ces réglages :

| Réglage | Effet |
|---|---|
| calque par pixel (`WS_EX_LAYERED` posé par WPF, `AllowsTransparency`), noir à **254/255** (99,6 %) | noir à l'œil ; jamais « opaque » pour Windows et Chrome |
| `WS_EX_TRANSPARENT` | transparente aux clics : ceux des agents atteignent l'app dessous ; ceux d'une main humaine sont arrêtés par le filtre |
| `WS_EX_TOOLWINDOW`, `WS_EX_NOACTIVATE` | absente de la barre des tâches et d'Alt+Tab, ne prend jamais le clavier à l'app de l'agent |
| `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` | absente des captures d'écran : l'agent voit l'app, pas le voile |

**Occlusion des fenêtres de Chrome et Edge** (« native window occlusion »). Chrome calcule si ses fenêtres sont
cachées ; une page cachée passe en `visibilityState = hidden`, ses animations s'arrêtent et ses minuteries ralentissent,
ce qui gêne un agent qui pilote le navigateur. D'après le code de Chromium (`ui/gfx/win/hwnd_util.cc`,
`IsWindowVisibleAndFullyOpaque`, lu le 2026-11-03), une fenêtre **ne cache rien** si elle a `WS_EX_TRANSPARENT`, ou
`WS_EX_TOOLWINDOW`, ou `WS_EX_LAYERED` avec une opacité inférieure à 255 ou posée par `UpdateLayeredWindow` (calque par
pixel, le cas de WPF). Le voile cumule les trois : chacun suffit, les autres servent de filet si une version future de
Chrome change une règle. L'opacité 254 coûte au plus un niveau de gris sur 255 : invisible à l'œil (sur macOS, 0,998
pour la même raison). Si un navigateur ralentit quand même, lancez-le avec
`--disable-features=CalculateNativeWinOcclusion`.

*Mesuré sur le runner Windows de la CI* : Windows ignore `WS_EX_LAYERED` ajouté après coup à une fenêtre WPF ordinaire
(et la fenêtre n'est alors plus transparente aux clics) ; d'où le calque WPF par pixel dès la création, et le drone
affiché par `WebView2CompositionControl`, seul contrôle WebView2 visible dans une telle fenêtre. En revanche, Edge ne
calcule pas l'occlusion sur ce runner (même une fenêtre opaque ordinaire ne le « cache » pas) : l'effet sur Chrome et
Edge reste à vérifier sur un vrai PC.

## Le filtre d'entrées
Deux crochets bas niveau (`WH_KEYBOARD_LL`, `WH_MOUSE_LL`) sur un fil dédié. Windows marque d'un drapeau
(`LLKHF_INJECTED`, `LLMHF_INJECTED`) tout ce qu'un logiciel envoie par `SendInput` : ces événements passent toujours, ce
sont ceux des agents. Le reste (le vrai clavier, la vraie souris) suit les mêmes règles que sur macOS
(`src/Hoverguard.Coeur/Regles.cs`, testées) :

| État | Touche | Clic | Mouvement, molette, relâchement, modificateur seul |
|---|---|---|---|
| Libre | passe (Ctrl+Win+W pose la garde) | passe | passe |
| Garde | **réveil** (avalée) | **réveil** (avalé) | bloqué |
| Identification | **avalée** et lue par le crochet pour le mot de passe | bloqué | bloqué |

Les touches multimédia (volume, lecture, navigateur, veille) comptent comme « mouvement » : bloquées sous la garde, sans
réveil. Pendant l'identification, aucune touche physique n'atteint une fenêtre : le crochet la traduit (`ToUnicodeEx`,
disposition du clavier de la fenêtre active, AltGr et touches mortes compris) et remplit lui-même le mot de passe
(`src/Hoverguard.Coeur/Securite.cs`, testé) ; la fenêtre d'identification ne prend jamais le clavier.

Deux détails propres à Windows : un relâchement dont l'appui était passé passe toujours (sinon Windows croirait Ctrl ou
Win encore enfoncés, et chaque lettre tapée par un agent deviendrait un raccourci) ; après Ctrl+Win+W, une touche
« masque » non attribuée est injectée pour que le menu Démarrer ne s'ouvre pas au relâchement de Win.

Windows retire en silence un crochet trop lent et ne prévient pas. Pendant la garde, une « sonde » (une touche non attribuée et un déplacement d'un pixel, injectés, marqués et avalés par le crochet : ni les apps ni le pointeur ne les voient)
vérifie toutes les 2 s que le crochet répond ; sinon il est reposé, et s'il ne repart pas la vraie session Windows
est verrouillée (`LockWorkStation`), comme sur macOS. De même si le Narrateur s'active ou si Hoverguard est relancé
après s'être arrêté en pleine garde.

## Lever la garde
Appuyez sur une touche : le drone fonce vers vous et l'identification s'affiche sur l'écran principal.
- **Le mot de passe Hoverguard**, choisi au premier lancement (Réglages › Garde, modifiable écran libre), est le seul
  moyen de lever la garde : ni Windows Hello ni le mot de passe de la session. Il peut être le même que votre code
  Windows. Seule son empreinte salée (PBKDF2-SHA256) est enregistrée ; le texte tapé ne transite que par un
  `SecureString` rempli par le crochet. 4 essais libres, puis 3 s, 6 s, 9 s… jusqu'à 2 min d'attente (Échap ne la
  raccourcit pas).
- **Mot de passe oublié sous la garde** : Ctrl + Alt + Suppr › Se déconnecter (ou redémarrer) ; choisissez-en un
  nouveau ensuite dans les réglages.
- **Échap** renvoie le drone surveiller ; une minute sans rien taper aussi.

## Garder le PC éveillé
Pendant la garde, le PC et l'écran restent éveillés (`SetThreadExecutionState`), et le voile demande d'être branché
quand le PC passe sur batterie. Le menu « Garder le PC éveillé » fait la même chose sans voile : 15 min, 30 min, 1 h,
2 h, 4 h ou jusqu'à l'arrêt, écran allumé ou non, arrêt automatique sur batterie (10 à 50 %).

## Options
- **Rapport de la garde** : durée, agents terminés, réveils, mots de passe faux, photos. Il attend dans le menu
  (« Rapport de la dernière garde… ») ; l'option « Ouvrir le rapport de la garde au déverrouillage » (désactivée par
  défaut) l'affiche tout seul.
- **Photo de l'intrus** (désactivée par défaut ; photographier quelqu'un peut être encadré par la loi, droit à
  l'image) : caméra intégrée uniquement (jamais une webcam USB ni un téléphone), après 2 mots de passe faux, puis toutes
  les 3 erreurs. Photos dans `%LOCALAPPDATA%\Utahpik\Hoverguard\intrus`, effacées après 30 jours.
- **Garde automatique** après 5 à 30 min sans activité, si des agents travaillent. Hoverguard n'utilise pas
  `GetLastInputInfo` : cette fonction compte aussi les entrées injectées, donc un agent actif empêcherait toute garde
  automatique. Il mesure l'inactivité avec ses propres crochets, qui ne comptent que le vrai clavier et la vraie souris.
- **Ouverture à l'ouverture de session** (`HKCU\…\Run`).
- **Notifications sur le téléphone** : emplacement réservé, désactivé. Il n'y a pas d'iMessage sous Windows, et aucun
  canal local ne respecte encore « aucune connexion réseau ». *(TODO)*

## Hoverguard Pro
Même format de licence et même clé publique que sur Mac (`macos/Sources/Licence.swift`) : une licence achetée marche
sur les deux. Code : `src/Hoverguard.Coeur/Licence.cs` (vérification, état) et `src/Hoverguard/Pro.cs` (onglet, lien).
- **Licence** `HG1-<base64url(e-mail|offre|achat|fin)>.<base64url(signature Ed25519)>`, offres `fondateur`, `pro`,
  `equipe`, vérifiée **hors ligne** avec la clé publique d'Utahpik (`/KrNPU4ssMVCT7SCIFmjtM0oPeOuNFAxJK7AmBipmik=`).
  Aucune activation en ligne : la clé privée n'est jamais dans l'app, une licence ne peut donc pas être fabriquée.
- **Mises à jour** : une licence couvre les versions compilées jusqu'à sa date de fin (la date de compilation est
  inscrite dans l'exe, `DateDeVersion`) ; la licence Fondateur couvre tout.
- **Essai** de 14 jours, démarré à la première vraie garde (pas à l'installation).
- **Édition de lancement** (`-p:EditionLancement=true`, constante `EDITION_LANCEMENT`) : Pro offert à vie au premier
  lancement, gardé par les versions suivantes.
- **Lien** `hoverguard://licence?cle=HG1-…` (moins de 2 000 caractères) : active la licence si sa signature est valide,
  sinon affiche une erreur. Pendant la garde, rien ne s'affiche (la fenêtre serait sous le voile) : la licence valide
  est activée en silence.
- **Onglet « Pro »** des réglages : statut, fonctions Pro à venir, champ pour coller une licence. Les liens d'achat et
  de soutien restent cachés tant qu'ils sont vides (`Pro.UrlAchat`, `Pro.UrlSoutien`).
- **Bibliothèque** : Ed25519 n'existe pas dans .NET 8. Hoverguard utilise
  [BouncyCastle.Cryptography](https://www.nuget.org/packages/BouncyCastle.Cryptography) **2.6.2** (licence MIT, code
  entièrement managé, sans bibliothèque native), version épinglée dans `Hoverguard.Coeur.csproj`.

## Le drone
Une page three.js (r160, licence MIT, `src/Hoverguard/Web/three/`) embarquée dans l'exe et servie à WebView2 par
Hoverguard lui-même : aucune requête réseau, aucun CDN (toute autre adresse reçoit un refus, et la page a une politique
de sécurité de contenu `connect-src 'none'`). Rendu studio : environnement d'éclairage, matériaux PBR, ombres douces,
sol réfléchissant qui se fond dans le noir, dix lasers qui balaient tout l'écran, grille de scan. Modes « en vol »
(agents actifs), « posé » (aucun agent), « approche » (quelqu'un touche le clavier). Écrans secondaires en qualité
allégée (sans ombres ni reflet, 20 images/s au lieu de 30). Pour le regarder dans un navigateur pendant le
développement : servez `src/Hoverguard/Web/` en HTTP local et ouvrez `drone.html` (démonstration des trois modes).

## Données locales
| Quoi | Où |
|---|---|
| Réglages | `%APPDATA%\Utahpik\Hoverguard\reglages.json` |
| Licence, début de l'essai, cadeau de lancement | `%APPDATA%\Utahpik\Hoverguard\pro.json` |
| Journal (1 Mo au plus, sans mot de passe) | `%LOCALAPPDATA%\Utahpik\Hoverguard\journal.txt` |
| Photos d'intrus | `%LOCALAPPDATA%\Utahpik\Hoverguard\intrus\` |
| Cache du moteur WebView2 | `%LOCALAPPDATA%\Utahpik\Hoverguard\WebView2\` |
| Schéma d'URL, ouverture au démarrage | `HKCU\Software\Classes\hoverguard`, `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` |

Aucune télémétrie. Seule connexion réseau : la recherche quotidienne de mises à jour dans les Releases GitHub
(désactivable). Les services d'arrière-plan du moteur WebView2 (mises à jour de
composants, rapports…) sont coupés par des options de lancement.

## Limites connues
- **Fenêtres administrateur.** Une app sans droits d'administrateur ne voit pas les touches destinées à une fenêtre
  lancée « en tant qu'administrateur » (isolation UIPI). Si une telle fenêtre a le clavier pendant la garde, les touches
  physiques l'atteignent. Gardez les agents dans des fenêtres normales.
- **Écrans tactiles et stylets** : filtrés seulement quand Windows les transforme en messages souris ; les gestes du
  pavé tactile traités par le système et le clavier tactile (injecté) passent.
- **Luminosité** : les touches des portables échappent au crochet ; Hoverguard remet la luminosité notée à la pose, au
  mieux (certains pilotes ne passent pas par le schéma d'alimentation).
- **Logiciels de prise en main à distance** (TeamViewer, AnyDesk…) : leurs entrées sont injectées, donc elles passent,
  comme celles des agents.
- **Économiseur d'écran avec mot de passe** : `SetThreadExecutionState` ne l'empêche pas ; s'il verrouille la session,
  les agents s'arrêtent. Désactivez-le pour les longues tâches.
- **Fenêtres « toujours au premier plan »** apparues après la pose : le voile repasse au-dessus toutes les 2 s.

## Tests
- `tests/Hoverguard.Tests` (xUnit) couvre toute la logique pure : règles du filtre et suivi des touches, suivi des
  agents, textes, URL (dont « aucune URL ne lève la garde »), hooks et sauvegarde, éveil, rapport, limiteur, réglages.
  Sous Windows, le test des hooks exécute vraiment la commande complète à travers cmd.exe, PowerShell et Git Bash.
- `Hoverguard.exe --autotest <dossier>` (lancé par la CI sur `windows-latest`) mesure sur un vrai Windows : styles et
  opacité du voile, exclusion des captures, clic et frappes injectés qui traversent le voile, sonde du crochet, rendu du
  drone, et l'occlusion d'Edge sous différents types de fenêtres. Les captures sont jointes à la CI (artefact
  `autotest`). Il ne peut pas tester le vrai clavier, la vraie souris, la webcam ni plusieurs écrans.

---
© 2026 Utahpik.

## Logo et icônes
L'exe porte le logo « Sentinelle » (`ressources/hoverguard.ico`, issu de `docs/logo/logo.svg`). L'icône de la zone de
notification a deux états, comme la barre des menus du Mac : drone posé (écran libre), bouclier plein (garde posée),
chacun pour une barre des tâches sombre ou claire. Sources : `ressources/sources/*.svg` ; pour régénérer les `.ico`
(16, 20, 24 et 32 px) :

```bash
python3 -m pip install cairosvg==2.9.1
```

```bash
python3 windows/ressources/generer-icones.py
```
