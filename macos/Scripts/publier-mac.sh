#!/bin/zsh
# Fabrique la version Mac téléchargeable de Hoverguard : build Release (édition de lancement si demandée), signature
# Developer ID, DMG, notarisation Apple, agrafage, vérification Gatekeeper ; plus l'archive de mise à jour Sparkle
# (Hoverguard-<version>.zip, signée EdDSA) et appcast.xml, à joindre à la même Release GitHub : les apps installées
# lisent releases/latest/download/appcast.xml et se mettent à jour seules.
# La clé EdDSA de Sparkle reste dans le Trousseau (compte « utahpik-hoverguard ») ; elle n'est jamais affichée.
#
# Prérequis (une fois) :
#   - un certificat « Developer ID Application » dans le Trousseau (Xcode › Réglages › Comptes › Gérer les certificats) ;
#   - des identifiants de notarisation rangés dans le Trousseau, saisis par vous-même :
#       xcrun notarytool store-credentials hoverguard --apple-id <votre identifiant Apple> --team-id <ÉQUIPE>
#     (il demande un mot de passe pour app, créé sur appleid.apple.com ; il ne passe par aucun fichier)
#
# Sans compte Apple payant (choix d'Anthony : distribution par les Releases GitHub seulement), la signature se fait avec
# le certificat gratuit « Utahpik Code Signing » (Scripts/creer-certificat.sh) : même identité à chaque version, donc
# l'autorisation Accessibilité est gardée après les mises à jour ; pas de notarisation.
#
# Usage : Scripts/publier-mac.sh <version> [--lancement]     ex. Scripts/publier-mac.sh 1.0.0 --lancement
#         Scripts/publier-mac.sh <version> --essai            → signature locale jetable, pour tester le script
set -euo pipefail
cd "${0:A:h}/.."

VERSION=${1:?usage : publier-mac.sh <version> [--lancement|--essai]}
LANCEMENT=0; ESSAI=0
for a in "${@:2}"; do [[ $a == --lancement ]] && LANCEMENT=1; [[ $a == --essai ]] && ESSAI=1; done
SORTIE="$HOME/Developer/builds/hoverguard-publication"
DD="$SORTIE/derived"
APP="$DD/Build/Products/Release/Hoverguard.app"
DMG="$SORTIE/Hoverguard-$VERSION.dmg"
rm -rf "$SORTIE"; mkdir -p "$SORTIE"

if (( ESSAI )); then
  IDENTITE="-"; EQUIPE=""
else
  IDENTITE=$(security find-identity -v -p codesigning | sed -n 's/.*"\(Developer ID Application: .*\)"/\1/p' | head -1)
  EQUIPE=$(echo "$IDENTITE" | sed -n 's/.*(\([A-Z0-9]\{10\}\))$/\1/p')
  NOTARISER=1
  if [[ -z $IDENTITE ]]; then
    security find-identity -p codesigning | grep -q '"Utahpik Code Signing"' \
      || { echo "Ni Developer ID ni certificat Utahpik : lancer Scripts/creer-certificat.sh"; exit 1; }
    IDENTITE="Utahpik Code Signing"; EQUIPE=""; NOTARISER=0
  fi
  echo "Signature : $IDENTITE"
fi

CONDITIONS='$(inherited)'
(( LANCEMENT )) && CONDITIONS='$(inherited) EDITION_LANCEMENT' && echo "Édition de lancement : Pro offert à vie aux Fondateurs"

xcodegen generate -q
xcodebuild -project Hoverguard.xcodeproj -scheme Hoverguard -configuration Release -derivedDataPath "$DD" \
  MARKETING_VERSION="$VERSION" CURRENT_PROJECT_VERSION="$(date +%Y%m%d%H%M%S)" \
  SWIFT_ACTIVE_COMPILATION_CONDITIONS="$CONDITIONS" \
  CODE_SIGN_STYLE=Manual CODE_SIGN_IDENTITY="$IDENTITE" DEVELOPMENT_TEAM="$EQUIPE" \
  OTHER_CODE_SIGN_FLAGS="--options=runtime $([[ $IDENTITE == Developer\ ID* ]] && echo --timestamp)" -quiet build

codesign --verify --deep --strict --verbose=2 "$APP"
/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -u "$APP" 2>/dev/null || true

# Mise à jour Sparkle : archive zip de l'app + appcast.xml signés avec la clé EdDSA du Trousseau.
SPARKLE="$DD/SourcePackages/artifacts/sparkle/Sparkle/bin"
MAJ="$SORTIE/maj"; mkdir -p "$MAJ"
ditto -c -k --sequesterRsrc --keepParent "$APP" "$MAJ/Hoverguard-$VERSION.zip"
"$SPARKLE/generate_appcast" --account utahpik-hoverguard \
  --download-url-prefix "https://github.com/AnthonyTrtre/hoverguard/releases/download/v$VERSION/" \
  --link "https://github.com/AnthonyTrtre/hoverguard" --maximum-versions 1 "$MAJ"
mv "$MAJ/Hoverguard-$VERSION.zip" "$MAJ/appcast.xml" "$SORTIE/"; rm -rf "$MAJ"

# DMG : l'app et un raccourci vers Applications, à glisser.
PREPA="$SORTIE/dmg"; mkdir -p "$PREPA"
ditto "$APP" "$PREPA/Hoverguard.app"
ln -s /Applications "$PREPA/Applications"
hdiutil create -quiet -volname "Hoverguard $VERSION" -srcfolder "$PREPA" -fs HFS+ -format UDZO "$DMG"
rm -rf "$PREPA"

if (( ESSAI )) || (( ! NOTARISER )); then
  (( ESSAI )) || codesign --sign "$IDENTITE" --timestamp=none "$DMG"
  shasum -a 256 "$DMG" | tee "$DMG.sha256"
  echo "Prêt (sans notarisation Apple) : $DMG"
  echo "Joindre à la Release v$VERSION : le DMG, son .sha256, Hoverguard-$VERSION.zip et appcast.xml"
  exit 0
fi

codesign --sign "$IDENTITE" --timestamp "$DMG"
xcrun notarytool submit "$DMG" --keychain-profile hoverguard --wait
xcrun stapler staple "$DMG"
xcrun stapler validate "$DMG"
spctl --assess --type open --context context:primary-signature --verbose "$DMG"
shasum -a 256 "$DMG" | tee "$DMG.sha256"
echo "Prêt à publier : $DMG (joindre aussi Hoverguard-$VERSION.zip et appcast.xml)"
