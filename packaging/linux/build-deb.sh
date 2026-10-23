#!/usr/bin/env bash
# Builds dist/mooncam_<version>_amd64.deb. Installing it with apt installs and configures everything MoonCam
# needs (runtime, FFmpeg, adb, virtual camera driver), so the app is ready the first time it opens.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
VERSION="${1:-0.1.0}"
HERE="$ROOT/packaging/linux"
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT

dotnet publish "$ROOT/desktop/src/MoonCam.App" -c Release -r linux-x64 --self-contained false \
    -p:Version="$VERSION" -o "$STAGE/usr/lib/mooncam" -nologo -v quiet

# The privileged helper lives root-owned in /usr/libexec, never next to user-writable files.
rm -f "$STAGE/usr/lib/mooncam/mooncam-setup"
install -Dm755 "$HERE/mooncam-setup" "$STAGE/usr/libexec/mooncam/mooncam-setup"
install -Dm755 "$HERE/mooncam.sh" "$STAGE/usr/bin/mooncam"
install -Dm644 "$HERE/mooncam.desktop" "$STAGE/usr/share/applications/io.github.moontechnologiesco.MoonCam.desktop"
for size in 32 48 64 128 256 512; do
    install -Dm644 "$ROOT/assets/icon/mooncam-$size.png" "$STAGE/usr/share/icons/hicolor/${size}x${size}/apps/mooncam.png"
done
install -Dm644 "$ROOT/LICENSE" "$STAGE/usr/share/doc/mooncam/copyright"

chmod 755 "$STAGE"
chmod -R u=rwX,go=rX "$STAGE/usr" # nothing installed is writable by group or others
mkdir -p "$STAGE/DEBIAN"
sed -e "s/@VERSION@/$VERSION/" -e "s/@SIZE@/$(du -sk "$STAGE/usr" | cut -f1)/" "$HERE/debian/control.in" > "$STAGE/DEBIAN/control"
install -m755 "$HERE/debian/postinst" "$HERE/debian/postrm" "$STAGE/DEBIAN/"

mkdir -p "$ROOT/dist"
fakeroot dpkg-deb --build --root-owner-group "$STAGE" "$ROOT/dist/mooncam_${VERSION}_amd64.deb" >/dev/null
echo "Built $ROOT/dist/mooncam_${VERSION}_amd64.deb"
