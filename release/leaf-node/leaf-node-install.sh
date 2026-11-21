#!/bin/sh
# Leaf node for Linux: the installer.
#
#   curl -fsSL https://github.com/RedBamboo-Interactive/redleaf-releases/releases/download/<release>/leaf-node-install.sh | sh
#
# Then pair the node with the code Leaf shows in Compute > Nodes > Add a node (a separate step):
#
#   ~/.local/bin/leaf-node pair --server https://<leaf address> --code ABCD-EFGH
#
# It reads the signed release feed's current RedCompute record (pointer, then record, whose SHA-256 the
# pointer names), takes leaf-node-manifest.v1.json from the same release, checks that it names that
# record's version and source commit, downloads leaf-node-linux-x64.tar.gz and checks that it has exactly
# the size and SHA-256 the manifest names. Only then does it run `leaf-node install`, which puts the agent
# in ~/.local/share/leaf-node/<version>, links ~/.local/bin/leaf-node and starts it with a systemd user
# unit. Running it again upgrades: the agent stops, the new version starts, the previous one is kept.
#
#   ... | sh -s -- --keep-running-when-logged-out   keep the agent running when you log out (linger)
#   ... | sh -s -- --uninstall                      remove leaf-node: key, unit, data and program files
#
# Nothing here needs root, and it refuses to run as root. It never writes a key, a token or a password:
# pairing (`leaf-node pair`) is the only step that makes the node's key.
#
# Environment (for testing a release before it is published; leave unset otherwise):
#   LEAF_NODE_FEED_URL  another release feed (https://, or file:// for a local dry run)
#   LEAF_NODE_CHANNEL   nightly (default) or stable
#   LEAF_NODE_HOME      an isolated agent in this folder: its files there, no systemd unit
set -eu

FEED="${LEAF_NODE_FEED_URL:-https://raw.githubusercontent.com/RedBamboo-Interactive/redleaf-releases/release-feed}"
FEED="${FEED%/}"
CHANNEL="${LEAF_NODE_CHANNEL:-nightly}"

say() { printf 'leaf-node installer: %s\n' "$*" >&2; }
die() { say "$*"; exit 1; }

action=install
install_options=""
for argument in "$@"; do
  case "$argument" in
    --uninstall) action=uninstall ;;
    --keep-running-when-logged-out) install_options="--keep-running-when-logged-out" ;;
    *) die "unknown option '$argument' (use --keep-running-when-logged-out or --uninstall)." ;;
  esac
done

[ "$(id -u)" -ne 0 ] || die "run this as your own user, not as root: leaf-node installs into your home directory."
case "$(uname -s)/$(uname -m)" in
  Linux/x86_64) ;;
  *) die "this installer is for Linux on x86_64; this machine is $(uname -s)/$(uname -m)." ;;
esac
case "$CHANNEL" in nightly|stable) ;; *) die "LEAF_NODE_CHANNEL must be nightly or stable." ;; esac

if [ -n "${LEAF_NODE_HOME:-}" ]; then
  programs="$LEAF_NODE_HOME/programs"
else
  programs="${XDG_DATA_HOME:-$HOME/.local/share}/leaf-node"
fi

# The installed agent, from current.json (empty when none is installed).
installed() {
  [ -f "$programs/current.json" ] || return 0
  folder="$(python3 -I -c 'import json,sys; print(json.load(open(sys.argv[1]))["path"])' "$programs/current.json" 2>/dev/null)" || return 0
  [ -x "$folder/leaf-node" ] && printf '%s\n' "$folder/leaf-node"
  return 0
}

if [ "$action" = uninstall ]; then
  agent="$(installed)"
  [ -n "$agent" ] || { say "leaf-node is not installed."; exit 0; }
  exec "$agent" uninstall
fi

for tool in curl tar sha256sum python3 mktemp; do
  command -v "$tool" >/dev/null 2>&1 || die "'$tool' is required and was not found."
done

work="$(mktemp -d "${TMPDIR:-/tmp}/leaf-node-install.XXXXXXXX")"
trap 'rm -rf "$work"' EXIT INT TERM
# try_fetch <url> <file>: https only; file:// only for a LEAF_NODE_FEED_URL dry run.
try_fetch() {
  case "$1" in
    file://*) [ -n "${LEAF_NODE_FEED_URL:-}" ] || die "refusing to read $1 (https only)."
              cp "${1#file://}" "$2" 2>/dev/null ;;
    https://*) curl -fsSL --proto '=https' --tlsv1.2 --retry 3 -o "$2" "$1" ;;
    *) die "refusing to download from $1 (https only)." ;;
  esac
}
fetch() { try_fetch "$1" "$2" || die "could not download $1"; }
# One small, isolated Python step reads each JSON document; nothing it reads is executed.
# read_json <file> <code> [arguments...]: the code sees sys.argv[1] = file, then the arguments.
read_json() { file="$1"; code="$2"; shift 2; python3 -I -c "$code" "$file" "$@" 2>/dev/null; }

say "reading the signed $CHANNEL release feed..."
fetch "$FEED/components/redcompute/channels/$CHANNEL.json" "$work/pointer.json"
pointer="$(read_json "$work/pointer.json" 'import json,sys
r = json.load(open(sys.argv[1]))["payload"]["record"]
print(r["url"], r["sha256"])')" || die "the feed's RedCompute pointer is unreadable."
set -- $pointer
record_url="$1"; record_sha="$2"
fetch "$record_url" "$work/record.json"
[ "$(sha256sum "$work/record.json" | cut -d' ' -f1)" = "$record_sha" ] \
  || die "the RedCompute record does not match the hash its pointer names."
record="$(read_json "$work/record.json" 'import json,sys
p = json.load(open(sys.argv[1]))["payload"]
assert p["componentId"] == "redcompute"
print(p["version"], p["sourceCommit"], p["artifact"]["url"])')" \
  || die "the feed's RedCompute record is unreadable."
set -- $record
version="$1"; commit="$2"; folder="${3%/*}"

try_fetch "$folder/leaf-node-manifest.v1.json" "$work/manifest.json" 2>/dev/null \
  || die "RedCompute $version was released without leaf-node; try again after the next Nightly."
entry="$(read_json "$work/manifest.json" 'import json,sys
m = json.load(open(sys.argv[1]))
assert m["schemaVersion"] == 1 and m["componentId"] == "leaf-node"
assert m["version"] == sys.argv[2] and m["sourceCommit"] == sys.argv[3]
a = [a for a in m["artifacts"] if a["rid"] == "linux-x64"]
assert len(a) == 1 and a[0]["name"] == "leaf-node-linux-x64.tar.gz"
print(a[0]["sha256"], a[0]["sizeBytes"])' "$version" "$commit")" \
  || die "the leaf-node manifest does not belong to RedCompute $version."
set -- $entry
archive_sha="$1"; archive_size="$2"

say "downloading leaf-node $version..."
fetch "$folder/leaf-node-linux-x64.tar.gz" "$work/leaf-node-linux-x64.tar.gz"
[ "$(wc -c < "$work/leaf-node-linux-x64.tar.gz" | tr -d ' ')" = "$archive_size" ] \
  && [ "$(sha256sum "$work/leaf-node-linux-x64.tar.gz" | cut -d' ' -f1)" = "$archive_sha" ] \
  || die "the download does not match the release manifest; nothing was installed."
mkdir "$work/leaf-node"
tar -xzf "$work/leaf-node-linux-x64.tar.gz" -C "$work/leaf-node" --no-same-owner \
  || die "the download is not a leaf-node archive."
[ -x "$work/leaf-node/leaf-node" ] || die "the archive has no leaf-node executable."

say "installing (an installed leaf-node is stopped and upgraded)..."
# shellcheck disable=SC2086 # install_options is empty or one fixed flag
"$work/leaf-node/leaf-node" install $install_options

shim="$HOME/.local/bin/leaf-node"
[ -z "${LEAF_NODE_HOME:-}" ] || shim="$LEAF_NODE_HOME/bin/leaf-node"
say "done. Pair this machine with the code from Leaf (Compute > Nodes > Add a node):"
say "  $shim pair --server https://<leaf address> --code <code>"
