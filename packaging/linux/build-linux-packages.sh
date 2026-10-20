#!/bin/sh
# Construit le paquet .deb et l'archive .tar.gz Linux à partir d'une publication « dotnet publish ».
#
# Usage : ./build-linux-packages.sh <dossier-publish> <x64|arm64> <version> <dossier-sortie>
#
# À exécuter sous Linux (ou WSL) : dpkg-deb et les droits Unix sont nécessaires.
# build.ps1 l'appelle automatiquement via WSL quand il est disponible.
set -eu

PUBLISH_DIR=$1
ARCH=$2
VERSION=$3
OUT_DIR=$4

HERE=$(dirname "$(readlink -f "$0")")
case "$ARCH" in
    x64) DEB_ARCH=amd64 ;;
    arm64) DEB_ARCH=arm64 ;;
    *) echo "Architecture inconnue : $ARCH (x64 ou arm64)" >&2; exit 1 ;;
esac

[ -f "$PUBLISH_DIR/NetShareHub" ] || { echo "NetShareHub introuvable dans $PUBLISH_DIR" >&2; exit 1; }
mkdir -p "$OUT_DIR"
OUT_DIR=$(readlink -f "$OUT_DIR")

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT

# --- Arborescence commune -----------------------------------------------------------------------
stage_app() {
    dest=$1
    mkdir -p "$dest"
    cp -a "$PUBLISH_DIR/." "$dest/"
    install -m 755 "$HERE/netsharehub-elevate" "$dest/netsharehub-elevate"
    # Les fichiers venant d'un disque Windows arrivent avec des droits arbitraires : on les normalise.
    find "$dest" -type f -exec chmod 644 {} +
    find "$dest" -type d -exec chmod 755 {} +
    chmod 755 "$dest/NetShareHub" "$dest/netsharehub-elevate"
}

# --- .deb ---------------------------------------------------------------------------------------
DEB_ROOT="$WORK/deb"
stage_app "$DEB_ROOT/opt/netsharehub"
install -D -m 644 "$HERE/netsharehub.desktop" "$DEB_ROOT/usr/share/applications/netsharehub.desktop"
install -D -m 644 "$HERE/../../Src/NetShareHub/Desktop/Assets/icon.png" "$DEB_ROOT/usr/share/icons/hicolor/256x256/apps/netsharehub.png"
install -D -m 644 "$HERE/com.netsharehub.policy" "$DEB_ROOT/usr/share/polkit-1/actions/com.netsharehub.policy"
mkdir -p "$DEB_ROOT/usr/bin"
ln -s /opt/netsharehub/NetShareHub "$DEB_ROOT/usr/bin/netsharehub"

INSTALLED_KB=$(du -sk "$DEB_ROOT" | cut -f1)
mkdir -p "$DEB_ROOT/DEBIAN"
cat > "$DEB_ROOT/DEBIAN/control" <<EOF
Package: netsharehub
Version: $VERSION
Section: net
Priority: optional
Architecture: $DEB_ARCH
Installed-Size: $INSTALLED_KB
Depends: libc6, libfontconfig1, libx11-6, libice6, libsm6, policykit-1 | polkitd
Recommends: network-manager, iproute2, iptables
Maintainer: Marie-giovanni B. ALODEWOU <aldwgiovanni@gmail.com>
Homepage: https://github.com/aldwgiovanni/NetShareHub
Description: Partage de connexion Internet et agrégation de liaisons
 Partage la connexion Internet de votre ordinateur par un hotspot Wi-Fi ou vers
 un ou plusieurs adaptateurs (Ethernet, USB…), et cumule plusieurs connexions
 (Wi-Fi, Ethernet, 4G) grâce à un proxy SOCKS5/HTTP qui répartit les
 connexions entre les liaisons (Dispatch).
EOF
cat > "$DEB_ROOT/DEBIAN/postinst" <<'EOF'
#!/bin/sh
set -e
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q /usr/share/applications || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -t /usr/share/icons/hicolor || true
exit 0
EOF
cat > "$DEB_ROOT/DEBIAN/postrm" <<'EOF'
#!/bin/sh
set -e
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q /usr/share/applications || true
exit 0
EOF
chmod 755 "$DEB_ROOT/DEBIAN/postinst" "$DEB_ROOT/DEBIAN/postrm"

DEB_FILE="$OUT_DIR/netsharehub_${VERSION}_${DEB_ARCH}.deb"
dpkg-deb --build --root-owner-group "$DEB_ROOT" "$DEB_FILE" >/dev/null
echo "Paquet : $DEB_FILE"

# --- .tar.gz (toutes distributions) -------------------------------------------------------------
TAR_ROOT="$WORK/tar/netsharehub-$VERSION-linux-$ARCH"
stage_app "$TAR_ROOT/app"
install -m 644 "$HERE/netsharehub.desktop" "$TAR_ROOT/netsharehub.desktop"
install -m 644 "$HERE/../../Src/NetShareHub/Desktop/Assets/icon.png" "$TAR_ROOT/netsharehub.png"
install -m 644 "$HERE/com.netsharehub.policy" "$TAR_ROOT/com.netsharehub.policy"
install -m 755 "$HERE/install.sh" "$TAR_ROOT/install.sh"
install -m 755 "$HERE/uninstall.sh" "$TAR_ROOT/uninstall.sh"

TAR_FILE="$OUT_DIR/netsharehub-$VERSION-linux-$ARCH.tar.gz"
tar -C "$WORK/tar" --owner=0 --group=0 -czf "$TAR_FILE" "netsharehub-$VERSION-linux-$ARCH"
echo "Archive : $TAR_FILE"
