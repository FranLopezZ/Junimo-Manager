#!/usr/bin/env bash
# Instala Junimo Manager para el usuario actual (Steam Deck en modo escritorio o cualquier Linux x64).
# Se puede ejecutar con doble clic (elige «Ejecutar») o desde una terminal:  bash install.sh
# Al terminar muestra una ventana/notificación con el resultado; el detalle queda en ~/.cache/JunimoManager-install.log
set -uo pipefail

LOG="$HOME/.cache/JunimoManager-install.log"
mkdir -p "$(dirname "$LOG")"
: > "$LOG"
exec > >(tee -a "$LOG") 2>&1

notify() { # $1 = título, $2 = mensaje, $3 = info|error
  local title="$1" msg="$2" kind="${3:-info}"
  echo "[$kind] $title - $msg"
  if command -v kdialog >/dev/null 2>&1; then
    if [ "$kind" = error ]; then kdialog --title "$title" --error "$msg" >/dev/null 2>&1
    else kdialog --title "$title" --msgbox "$msg" >/dev/null 2>&1; fi
  elif command -v zenity >/dev/null 2>&1; then
    if [ "$kind" = error ]; then zenity --error --title "$title" --text "$msg" >/dev/null 2>&1
    else zenity --info --title "$title" --text "$msg" >/dev/null 2>&1; fi
  elif command -v notify-send >/dev/null 2>&1; then
    notify-send -u "$([ "$kind" = error ] && echo critical || echo normal)" "$title" "$msg" >/dev/null 2>&1
  fi
}

fail() {
  notify "Junimo Manager: no se pudo instalar" "$1

Detalles en: $LOG" error
  exit 1
}

SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEST="$HOME/Applications/JunimoManager"
APPS="$HOME/.local/share/applications"

[ "$(uname -m)" = "x86_64" ] || fail "Este paquete es para procesadores x86_64 y tu equipo es $(uname -m)."
[ -f "$SRC/JunimoManager" ] || fail "No encuentro el archivo «JunimoManager» junto a este script. Extrae el .tar.gz completo y vuelve a ejecutarlo desde esa carpeta."

echo "Instalando en $DEST ..."
mkdir -p "$DEST" "$APPS" || fail "No se pudo crear la carpeta $DEST."
cp -f "$SRC/JunimoManager" "$DEST/JunimoManager" || fail "No se pudo copiar el programa a $DEST (¿falta espacio en disco?)."
chmod +x "$DEST/JunimoManager" || fail "No se pudo marcar el programa como ejecutable."
[ -f "$SRC/JunimoManager.png" ] && cp -f "$SRC/JunimoManager.png" "$DEST/JunimoManager.png"

# Comprobación real: el programa tiene que arrancar y decir su versión.
VERSION="$(timeout 30 "$DEST/JunimoManager" --version 2>&1)" || fail "El programa se copió pero no arranca en este sistema:
$VERSION"

MISSING=""
if command -v ldconfig >/dev/null 2>&1; then
  for lib in libfontconfig.so.1 libX11.so.6; do
    ldconfig -p 2>/dev/null | grep -q "$lib" || MISSING="$MISSING $lib"
  done
fi

cat > "$APPS/junimomanager.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Junimo Manager
Comment=Gestor de mods de Stardew Valley
Exec="$DEST/JunimoManager" %u
Icon=$DEST/JunimoManager.png
Terminal=false
Categories=Game;
MimeType=x-scheme-handler/nxm;
EOF
[ -f "$APPS/junimomanager.desktop" ] || fail "No se pudo crear el acceso en el menú de aplicaciones."

# Acceso directo en el escritorio (Steam Deck en modo escritorio)
DESKTOP_DIR="$(xdg-user-dir DESKTOP 2>/dev/null || echo "$HOME/Desktop")"
if [ -d "$DESKTOP_DIR" ]; then
  cp -f "$APPS/junimomanager.desktop" "$DESKTOP_DIR/Junimo Manager.desktop" && chmod +x "$DESKTOP_DIR/Junimo Manager.desktop"
fi

command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$APPS" >/dev/null 2>&1
command -v xdg-mime >/dev/null 2>&1 && xdg-mime default junimomanager.desktop x-scheme-handler/nxm >/dev/null 2>&1

MSG="Junimo Manager $VERSION se instaló correctamente.

Lo encontrarás en el menú de aplicaciones (Juegos) y en el escritorio.
Para usarlo en modo juego, añádelo a Steam como «juego no-Steam» desde el modo escritorio."
if [ -n "$MISSING" ]; then
  MSG="$MSG

Aviso: faltan estas librerías del sistema y la ventana podría no abrirse:$MISSING"
fi
notify "Junimo Manager instalado" "$MSG" info
