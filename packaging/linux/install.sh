#!/bin/sh
# Installe NetShare Hub à partir de l'archive décompressée (toutes distributions Linux).
# Usage : sudo ./install.sh
set -eu

if [ "$(id -u)" -ne 0 ]; then
    echo "Ce script doit être lancé en root :  sudo ./install.sh" >&2
    exit 1
fi

SRC=$(dirname "$(readlink -f "$0")")
DEST=/opt/netsharehub

echo "Installation dans $DEST …"
rm -rf "$DEST"
mkdir -p "$DEST"
cp -a "$SRC/app/." "$DEST/"
chmod 755 "$DEST/NetShareHub" "$DEST/netsharehub-elevate"
chown -R root:root "$DEST"

install -D -m 644 "$SRC/netsharehub.desktop" /usr/share/applications/netsharehub.desktop
install -D -m 644 "$SRC/netsharehub.png" /usr/share/icons/hicolor/256x256/apps/netsharehub.png
install -D -m 644 "$SRC/com.netsharehub.policy" /usr/share/polkit-1/actions/com.netsharehub.policy
ln -sf "$DEST/NetShareHub" /usr/bin/netsharehub

command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q /usr/share/applications || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -t /usr/share/icons/hicolor || true

echo "NetShare Hub est installé. Lancez-le depuis le menu des applications ou avec la commande « netsharehub »."
echo "Pour partager la connexion, l'application proposera de se relancer en administrateur (pkexec)."
echo
echo "Outils utilisés selon les fonctions : NetworkManager (nmcli) pour le hotspot et le partage, iproute2 (ip)"
echo "pour l'agrégation, iptables en l'absence de NetworkManager."
