# Sécurité de Hoverguard

## En une phrase
Hoverguard empêche **une personne présente devant l'ordinateur** de voir l'écran et d'utiliser le clavier, la souris
et le trackpad pendant que des agents IA travaillent. **Ce n'est pas un verrou de sécurité** : pour protéger vos
données, verrouillez la session (⌃⌘Q sur Mac, ⊞ + L sur Windows) et chiffrez le disque (FileVault, BitLocker).

## Ce que Hoverguard bloque (macOS)
- Clavier, souris, trackpad, molette, gestes du trackpad, touches système (luminosité, son, média, Dictée), tablettes
  graphiques : tout ce qui vient du **matériel** est avalé pendant la garde.
- Pendant la saisie du mot de passe, les touches sont lues par Hoverguard lui-même : **aucune frappe n'atteint les
  apps**, même si une autre app a le focus (le mot de passe ne peut pas partir dans un terminal).
- Le menu de Hoverguard ne permet ni de lever la garde ni de quitter pendant la garde ; quitter l'app est refusé
  (sauf extinction ou fermeture de session).
- Aucune URL ne lève la garde. `hoverguard://verrouiller` est limité à une fois par minute ; les noms d'agents reçus
  par URL sont filtrés (lettres, chiffres, espace, `._-`, 40 caractères) et n'apparaissent jamais dans une notification.
- Écran branché, résolution changée, réveil de veille : le voile est reposé sur chaque écran.

## Quand la garde ne peut plus être garantie, la vraie session est verrouillée
Hoverguard verrouille la session macOS (écran de connexion du système) au lieu de laisser le Mac ouvert si :
- le système coupe le filtre d'entrées (autorisation retirée, blocage) ;
- une app active la **saisie sécurisée** (Secure Event Input : le filtre ne verrait plus le clavier) ;
- **VoiceOver** s'active (il lirait l'écran à voix haute) ;
- Hoverguard s'est arrêté en pleine garde (plantage, arrêt forcé) : verrouillage au relancement.
Les agents en ligne de commande continuent ; ceux qui pilotent l'écran s'arrêtent. Ce verrouillage utilise une
fonction non publique du système (`SACLockScreenImmediate`) ; si elle manque, l'écran est au moins éteint.

## Limites connues (ce que Hoverguard ne bloque pas)
- **Les logiciels** : par conception, les événements fabriqués par un logiciel passent (c'est ce qui laisse travailler
  les agents). Toute app autorisée en Accessibilité, un iPad en **Contrôle universel**, un logiciel de remappage
  (BetterTouchTool, Logi Options+, Hammerspoon…), la **Commande vocale** ou un outil de contrôle à distance agissent
  donc normalement sous le voile. Seuls Touch ID et le mot de passe lèvent la garde.
- **La voix** : Siri reste disponible (lire des messages, envoyer un message…). Désactivez « Dis Siri » si cela compte.
- **La Touch Bar** de certains MacBook Pro envoie ses contrôles directement à l'app au premier plan.
- **Partage d'écran** : le voile est exclu des captures, donc un partage d'écran ou une visio déjà lancés montrent
  tout ce qui est dessous. Arrêtez-les avant de partir.
- **Lumière résiduelle** : le voile est noir à 99,8 % (à 100 %, le système croit les fenêtres cachées et le navigateur
  ralentit). Une photo en pose longue d'un écran très lumineux pourrait laisser deviner des formes.
- **Accès physique à la machine** : arrêt forcé, redémarrage, démarrage sur un autre disque.

## Mot de passe et Touch ID
- Le mot de passe est vérifié par le système (OpenDirectory) puis effacé ; il n'est ni stocké ni journalisé.
- 4 essais libres, puis une attente croissante (3 s, 6 s… jusqu'à 2 min). Touch ID reste toujours possible.
- La politique de verrouillage de compte de macOS peut aussi s'appliquer après de nombreux échecs.

## Hooks des agents
Hoverguard ajoute des « hooks » aux réglages de Claude Code, Codex, Gemini CLI et Cursor **seulement si vous le
demandez**. Il écrit dans la vraie cible des liens symboliques, garde les droits d'origine du fichier, en fait **une**
copie (`….avant-hoverguard`), refuse de toucher un fichier illisible, et ne retire jamais vos propres hooks. Les
commandes n'utilisent que des chemins absolus et ne transmettent que l'état et 4 caractères de l'identifiant de
session. « Débrancher » retire tout ce que Hoverguard a ajouté.

## Données et réseau
- **Compte des agents** : Hoverguard lit la liste des processus et la **date de modification** des journaux de
  conversation des agents (`~/.claude/projects`, `~/.codex/sessions`, `~/.gemini/tmp`) pour savoir lesquels travaillent.
  Il n'ouvre jamais ces fichiers : leur contenu (vos conversations) n'est ni lu ni transmis.
- Aucune télémétrie. Seule connexion réseau : une fois par jour, la recherche de mises à jour dans les Releases GitHub
  de ce dépôt (Mac : `releases/latest/download/appcast.xml` ; Windows : `releases.win.json` via l'API GitHub) ; aucun
  identifiant ni donnée n'est envoyé. Désactivable dans Réglages › Garde.
- Photos d'intrus (option **désactivée par défaut** ; photographier quelqu'un peut être encadré par la loi) : caméra
  intégrée uniquement, dossier `~/Library/Application Support/com.utahpik.hoverguard/intrus` (droits 700/600),
  effacées après 30 jours.
- Notifications iMessage (option désactivée par défaut) : envoyées par votre app Messages, vers vous-même.

## Autorisations demandées (macOS)
| Autorisation | Pourquoi | Si refusée |
|---|---|---|
| Accessibilité | bloquer le matériel, entendre ⌃⌘W | la garde ne peut pas être posée |
| Caméra (option) | photo après 2 mots de passe faux | pas de photo |
| Automatisation de Messages (option) | notifications iMessage | pas de notification |

## Windows
Même modèle de menace et mêmes choix que sur macOS ; ce qui change :
- **Filtre d'entrées** : crochets bas niveau `WH_KEYBOARD_LL` / `WH_MOUSE_LL`. Les événements marqués « injectés »
  (`LLKHF_INJECTED`, `LLMHF_INJECTED`, c'est-à-dire envoyés par `SendInput`) passent : ce sont ceux des agents, mais
  aussi ceux de n'importe quel logiciel (prise en main à distance, clavier visuel déjà ouvert…).
- **Pendant la saisie du mot de passe, aucune frappe physique n'atteint les apps** : le crochet clavier avale toutes
  les touches, les traduit lui-même (`ToUnicodeEx`, disposition du clavier de la fenêtre active, AltGr et touches mortes
  compris) et remplit le mot de passe ; la fenêtre d'identification ne prend pas le clavier (`WS_EX_NOACTIVATE`).
  Même si le terminal d'un agent a le focus, le mot de passe ne peut pas y partir.
- **Mot de passe Hoverguard** : choisi par l'utilisateur au premier lancement (4 caractères au moins), c'est le seul
  moyen de lever la garde (ni Windows Hello ni le mot de passe de la session). Seule une empreinte PBKDF2-SHA256 salée
  (600 000 itérations) est enregistrée dans `%APPDATA%\Utahpik\Hoverguard\motdepasse.json` ; le texte tapé vit dans un
  `SecureString` et des tampons effacés après le calcul, jamais dans une chaîne .NET ni dans le journal. Sans mot de
  passe choisi, la garde ne se pose pas. 4 essais libres, puis 3 s, 6 s… jusqu'à 2 min d'attente ; l'échec compte
  même si Échap est pressé pendant la vérification, et Échap ne raccourcit pas l'attente. Mot de passe oublié sous la
  garde : Ctrl + Alt + Suppr › Se déconnecter (la session Windows protège alors le PC).
- **Quand la garde ne peut plus être garantie, la vraie session Windows est verrouillée** (`LockWorkStation`, ou à
  défaut l'écran est éteint) au lieu de lever la garde : crochet retiré par Windows (une sonde le détecte en quelques
  secondes ; il est d'abord reposé), Narrateur ou lecteur d'écran activé, voile impossible à reposer, ou relancement de
  Hoverguard après un arrêt en pleine garde (plantage, arrêt forcé : un fichier témoin le signale).
- **Écran branché, résolution changée, réveil de veille** (`DisplaySettingsChanged`, `PowerModeChanged`) : le voile est
  reposé sur chaque écran.
- **Touches multimédia** (volume, lecture, navigateur, veille) avalées sans réveiller le drone. **Luminosité** : les
  touches des portables ne passent pas par le crochet ; Hoverguard note la luminosité à la pose et la remet si elle
  change (au mieux : certains pilotes ne passent pas par le schéma d'alimentation). **Toucher et stylet** (écran, pavé
  tactile) : les messages souris qu'ils produisent sont traités comme du matériel, même marqués « injectés ».
- **Le voile** est exclu des captures par `WDA_EXCLUDEFROMCAPTURE` (Windows 10 2004 minimum ; sinon la garde refuse de
  se poser). Ce n'est pas une protection contre une photo de l'écran.
- **Le schéma `hoverguard://`** (inscrit dans HKCU) n'accepte que « poser la garde » et les signaux des agents.
  `verrouiller` est limité à une fois par minute ; les noms d'agents sont filtrés (lettres, chiffres, espace, `._-`,
  40 octets) et n'apparaissent jamais dans une notification ; 50 sessions d'agents au plus sont suivies. Les URL
  passent d'une instance à l'autre par un tube nommé local réservé à l'utilisateur, connexions réseau refusées.
- **Le drone** est une page embarquée dans l'exe, servie à WebView2 par Hoverguard ; toute autre adresse est refusée
  et la page ne peut pas ouvrir de connexion (`connect-src 'none'`).
- **Pendant la garde, le menu de l'icône ne propose ni « Lever la garde », ni « Quitter », ni les réglages** : un clic
  d'agent (qui, lui, passe) ne doit rien pouvoir y changer. Quitter est refusé pendant la garde, sauf à l'arrêt de
  Windows ou à la fermeture de session.
- **Données locales** : réglages dans `%APPDATA%\Utahpik\Hoverguard`, journal (sans mot de passe) et photos d'intrus
  dans `%LOCALAPPDATA%\Utahpik\Hoverguard`.

### Limites propres à Windows
- **Ctrl+Alt+Suppr et Win+L** ne peuvent pas être bloqués, et ne doivent pas l'être : ils mènent à l'écran de
  verrouillage de Windows, qui protège mieux que Hoverguard.
- **Fenêtres lancées en administrateur** : l'isolation UIPI empêche un crochet sans droits de filtrer les touches qui
  leur sont destinées. Si une telle fenêtre a le clavier pendant la garde, les touches physiques l'atteignent.
- **Écrans tactiles et stylets** : filtrés seulement quand Windows les transforme en messages souris ; les gestes du
  pavé tactile traités par le système (bureaux virtuels…) et le clavier tactile (entrées injectées) passent.
- **Changer le mot de passe Hoverguard** est possible écran libre, sans l'ancien : la personne devant un PC sans garde
  a déjà tout l'accès. Ce mot de passe protège la sortie de la garde, pas vos données.
- **Installateur non signé** : Edge et Windows SmartScreen avertissent au téléchargement et au premier lancement.
  Compilez depuis les sources si vous préférez.
- **Mises à jour (Windows)** : [Velopack](https://github.com/velopack/velopack) 1.2.161 (MIT), version épinglée. Les
  paquets viennent des Releases GitHub en HTTPS et sont vérifiés par leur somme SHA-256 publiée avec la Release ; ils
  ne sont pas signés à part (contrairement au Mac). Installation jamais pendant une garde.

### Autorisations demandées (Windows)
| Autorisation | Pourquoi | Si refusée |
|---|---|---|
| Aucune (pas de droits d'administrateur) | crochets clavier et souris, registre de l'utilisateur | — |
| Caméra, réglage global de Windows (option, désactivée par défaut) | photo après 2 mots de passe faux | pas de photo |

## Distribution
Les versions publiées dans les Releases sont signées avec le certificat « Utahpik Code Signing » (le même à chaque
version, ce qui permet à macOS de garder l'autorisation Accessibilité), mais ne sont pas notarisées par Apple : au tout
premier lancement, macOS demande « Ouvrir quand même ». Comparez la somme SHA-256 publiée avec celle du fichier
téléchargé. Les builds compilés soi-même depuis les sources sont signés « ad hoc » : l'autorisation est alors à
réaccorder après chaque compilation.

## Mises à jour (macOS)
- Moteur : [Sparkle](https://github.com/sparkle-project/Sparkle) 2.10.0 (MIT), version épinglée.
- Chaque archive est signée EdDSA par Utahpik ; la clé publique est dans l'app (`SUPublicEDKey`), la clé privée ne
  quitte pas le Trousseau d'Utahpik. Une archive non signée ou modifiée est refusée, et la signature de code de la
  nouvelle version doit correspondre à celle de l'app installée.
- Téléchargement en arrière-plan, installation à la fermeture de l'app ; **jamais pendant une garde** (la relance est
  repoussée jusqu'à la levée de la garde).
- Le certificat Utahpik n'étant pas émis par Apple, l'app désactive la validation des bibliothèques du Hardened Runtime
  (`disable-library-validation`) pour pouvoir charger Sparkle.framework ; les variables `DYLD_*` restent bloquées.

## Signaler une faille
Utilisez le **signalement privé de vulnérabilités** de GitHub (onglet Security du dépôt) plutôt qu'un ticket public.
Merci !
