#!/usr/bin/env bash
# Puts a Windows release built by Tennis -> Build Windows Release on the server, where installed games look for updates:
#
#   Tools/Release/deploy-windows.sh [release_dir] [user@host] [site_dir]
#
# The zip goes up first and latest.json last, under a temporary name renamed into place, so a game checking meanwhile
# sees either the old release or the whole new one. Older zips are removed afterwards. Defaults come from DEPLOY_HOST
# and DEPLOY_PATH, then from the values below. Run from the project root, in Git Bash on Windows.
set -euo pipefail

RELEASE_DIR="${1:-Build/WindowsRelease}"
HOST="${2:-${DEPLOY_HOST:-user1@fp.cloud28.umolab.ru}}"
SITE_DIR="${3:-${DEPLOY_PATH:-/var/www/fastuser/data/www/tennis.umolab.ru}}"
REMOTE_DIR="$SITE_DIR/download/windows"
SSH_OPTS="${SSH_OPTS:-}"

shopt -s nullglob
zips=("$RELEASE_DIR"/TennisSimulator-*.zip)
if [[ ${#zips[@]} -ne 1 || ! -f "$RELEASE_DIR/latest.json" ]]; then
  echo "No release in $RELEASE_DIR: build one with Tennis -> Build Windows Release" >&2
  exit 1
fi
zip="${zips[0]}"
name="$(basename "$zip")"

echo "Uploading $name to $HOST:$REMOTE_DIR"
# Group-writable with the group kept on new files, like the rest of the site, so the hosting panel's user can manage them.
ssh $SSH_OPTS "$HOST" "mkdir -p '$REMOTE_DIR' && { chmod 2775 '$SITE_DIR/download' '$REMOTE_DIR' 2>/dev/null || true; }"
scp $SSH_OPTS "$zip" "$HOST:$REMOTE_DIR/$name"
scp $SSH_OPTS "$RELEASE_DIR/latest.json" "$HOST:$REMOTE_DIR/latest.json.new"
# The download page, https://tennis.umolab.ru/download/: it reads latest.json, so it always offers this release.
scp $SSH_OPTS "$(dirname "$0")/download/index.html" "$HOST:$SITE_DIR/download/index.html"
ssh $SSH_OPTS "$HOST" "set -e; cd '$REMOTE_DIR'; chmod 664 '$name' latest.json.new; chmod 664 ../index.html 2>/dev/null || true; \
  mv -f latest.json.new latest.json; for old in TennisSimulator-*.zip; do [ \"\$old\" = '$name' ] || rm -f \"\$old\"; done"

url="https://tennis.umolab.ru/download/windows/latest.json"
echo "Published: $(curl --fail --silent --show-error --max-time 30 "$url" | tr -d '\n ')"
echo "Download page: https://tennis.umolab.ru/download/"
