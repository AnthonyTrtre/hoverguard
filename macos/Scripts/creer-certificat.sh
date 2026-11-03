#!/bin/zsh
# Crée (une seule fois) le certificat de signature gratuit « Utahpik Code Signing » dans le Trousseau.
# Toutes les versions publiées sont signées avec lui : macOS reconnaît Hoverguard d'une version à l'autre et garde
# l'autorisation Accessibilité après chaque mise à jour. Sans compte Apple payant, le tout premier lancement demande
# toujours « Ouvrir quand même » (l'app n'est pas notarisée).
# La clé privée ne quitte pas le Trousseau : aucun fichier ni mot de passe n'est affiché ou conservé.
set -euo pipefail
NOM="Utahpik Code Signing"
if security find-identity -p codesigning | grep -q "\"$NOM\""; then
  echo "Le certificat « $NOM » existe déjà."; exit 0
fi
TMP=$(mktemp -d); trap 'rm -rf "$TMP"' EXIT
cat > "$TMP/conf" <<EOF
[req]
distinguished_name = dn
x509_extensions = ext
prompt = no
[dn]
CN = $NOM
O = Utahpik
[ext]
basicConstraints = critical, CA:false
keyUsage = critical, digitalSignature
extendedKeyUsage = critical, codeSigning
EOF
openssl req -x509 -newkey rsa:3072 -nodes -days 3650 -config "$TMP/conf" -keyout "$TMP/cle.pem" -out "$TMP/cert.pem" 2>/dev/null
MDP=$(openssl rand -hex 24)
openssl pkcs12 -export -legacy -inkey "$TMP/cle.pem" -in "$TMP/cert.pem" -name "$NOM" -passout "pass:$MDP" -out "$TMP/id.p12" 2>/dev/null \
  || openssl pkcs12 -export -inkey "$TMP/cle.pem" -in "$TMP/cert.pem" -name "$NOM" -passout "pass:$MDP" -out "$TMP/id.p12"
security import "$TMP/id.p12" -k "$HOME/Library/Keychains/login.keychain-db" -P "$MDP" -T /usr/bin/codesign >/dev/null
echo "Certificat « $NOM » créé dans le Trousseau (valable 10 ans)."
echo "Pense à l'exporter dans un coffre sûr (Trousseau › clic droit › Exporter) : sans lui, la prochaine version"
echo "serait vue comme une autre app et l'autorisation Accessibilité serait à redonner."
