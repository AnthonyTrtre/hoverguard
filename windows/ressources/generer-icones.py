#!/usr/bin/env python3
"""Régénère les icônes de la zone de notification de Hoverguard pour Windows.

Sources : sources/notification-libre.svg et sources/notification-garde.svg, dérivées du logo « Sentinelle »
(docs/logo/logo.svg) et des icônes de barre des menus du Mac (macos/Ressources/Assets.xcassets/BarreMenus-*).

Sortie : un .ico par état et par thème de la barre des tâches, chacun en 16, 20, 24 et 32 pixels (100 %, 125 %,
150 % et 200 % d'échelle), images PNG à transparence. L'icône de l'exe, elle, est hoverguard.ico (fournie telle
quelle, issue du logo).

Utilisation (depuis la racine du dépôt) :
    python3 -m pip install cairosvg==2.9.1
    python3 windows/ressources/generer-icones.py
"""
import io
import pathlib
import struct

import cairosvg

ICI = pathlib.Path(__file__).resolve().parent
TAILLES = (16, 20, 24, 32)

# Couleurs reprises du logo : bleu glace #29B6F6 (thème sombre) et sa variante foncée #0876B6 (thème clair).
THEMES = {
    "sombre": {"libre": "#FFFFFF", "garde": "#29B6F6"},  # barre des tâches sombre (défaut de Windows 11)
    "clair": {"libre": "#1B1F24", "garde": "#0876B6"},   # barre des tâches claire
}


def png(svg: str, taille: int) -> bytes:
    return cairosvg.svg2png(bytestring=svg.encode("utf-8"), output_width=taille, output_height=taille)


def ico(images: list[tuple[int, bytes]]) -> bytes:
    """Fichier .ico à entrées PNG (accepté par Windows Vista et suivants)."""
    entete = struct.pack("<HHH", 0, 1, len(images))
    repertoire, donnees = b"", b""
    decalage = 6 + 16 * len(images)
    for taille, contenu in images:
        repertoire += struct.pack("<BBBBHHII", taille % 256, taille % 256, 0, 0, 1, 32, len(contenu), decalage + len(donnees))
        donnees += contenu
    return entete + repertoire + donnees


def main() -> None:
    for etat in ("libre", "garde"):
        modele = (ICI / "sources" / f"notification-{etat}.svg").read_text(encoding="utf-8")
        for theme, couleurs in THEMES.items():
            svg = modele.replace("COULEUR", couleurs[etat])
            sortie = ICI / f"notification-{etat}-{theme}.ico"
            sortie.write_bytes(ico([(t, png(svg, t)) for t in TAILLES]))
            print(f"écrit : {sortie.relative_to(ICI.parent.parent)}")


if __name__ == "__main__":
    main()
