#!/bin/sh
# Désinstalle NetShare Hub (installé avec install.sh).
# Usage : sudo ./uninstall.sh
set -eu

if [ "$(id -u)" -ne 0 ]; then
    echo "Ce script doit être lancé en root :  sudo ./uninstall.sh" >&2
    exit 1
fi

rm -rf /opt/netsharehub
rm -f /usr/bin/netsharehub \
      /usr/share/applications/netsharehub.desktop \
      /usr/share/icons/hicolor/256x256/apps/netsharehub.png \
      /usr/share/polkit-1/actions/com.netsharehub.policy

command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q /usr/share/applications || true

echo "NetShare Hub est désinstallé."
echo "Vos préférences restent dans ~/.config/netsharehub (supprimez-les à la main si vous le souhaitez)."
