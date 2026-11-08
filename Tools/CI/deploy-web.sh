#!/usr/bin/env bash
# Uploads the WebGL build to the web server.
#
#   Tools/CI/deploy-web.sh [build_dir] [user@host] [remote_dir]
#
# Defaults come from DEPLOY_HOST and DEPLOY_PATH, then from the values below. With rsync the upload is
# incremental, removes files the build no longer has, and swaps changed files in at the end (--delay-updates),
# so visitors rarely see a half-uploaded game. Without rsync (Git Bash on Windows) it falls back to tar over ssh.
#
# The CI deploy key is restricted on the server to rrsync in the site folder, so CI passes an empty remote_dir:
# paths are then relative to that folder.
set -euo pipefail

BUILD_DIR="${1:-Build/WebGL}"
HOST="${2:-${DEPLOY_HOST:-user1@fp.cloud28.umolab.ru}}"
REMOTE_DIR="${3-${DEPLOY_PATH-/var/www/fastuser/data/www/tennis.umolab.ru}}"
SSH="ssh ${SSH_OPTS:-}"

if [[ ! -f "$BUILD_DIR/index.html" || ! -d "$BUILD_DIR/Build" ]]; then
  echo "No WebGL build in $BUILD_DIR" >&2
  exit 1
fi

echo "Deploying $BUILD_DIR to $HOST:${REMOTE_DIR:-<site folder>}"
if command -v rsync >/dev/null 2>&1; then
  # Group-writable files, so both the hosting panel's user and the deploy user can replace them.
  rsync -rlt --delete --delay-updates --chmod=Dug=rwx,Do=rx,Fug=rw,Fo=r -e "$SSH" "$BUILD_DIR/" "$HOST:$REMOTE_DIR/"
else
  if [[ -z "$REMOTE_DIR" ]]; then
    echo "The tar fallback needs the full remote path" >&2
    exit 1
  fi
  # The site folder itself belongs to the hosting panel's user: leave its mode and times alone, touch only what we upload.
  tar -C "$BUILD_DIR" -czf - . | $SSH "$HOST" \
    "set -e; cd '$REMOTE_DIR'; find . -mindepth 1 -delete; tar -xzf - -o -m --no-overwrite-dir; \
     find . -mindepth 1 -user \$(id -un) -exec chmod ug+rw,o+r {} +; find . -mindepth 1 -type d -user \$(id -un) -exec chmod g+s {} +"
fi
echo "Deployed."
