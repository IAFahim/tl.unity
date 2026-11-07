#!/usr/bin/env bash
set -euo pipefail

PKG_NAME="com.tl.unity"
PKG_URL="https://github.com/IAFahim/tl.unity.git?path=/com.tl.unity#main"
TL_REPO="https://github.com/IAFahim/tl.git"
TL_HOME="${TL_HOME:-$HOME/.tl}"
TLB_REL="tools/Tl.Bake/bin/Release/net10.0/Tl.Bake.dll"
GUIDE="https://github.com/IAFahim/tl.unity/blob/main/docs/USING-ECS.md"

say() { printf '[tl-init] %s\n' "$*"; }
die() { printf '[tl-init] ERROR: %s\n' "$*" >&2; exit 1; }

case "$(uname -s)" in
  MINGW*|MSYS*|CYGWIN*) OS=windows ;;
  Darwin*) OS=macos ;;
  *) OS=linux ;;
esac

native() {
  if [ "$OS" = windows ]; then cygpath -m "$1"; else printf '%s' "$1"; fi
}

abs() {
  local dir base
  dir="$(dirname "$1")"
  base="$(basename "$1")"
  mkdir -p "$dir"
  printf '%s/%s' "$(cd "$dir" && pwd)" "$base"
}

[ $# -ge 1 ] || die "usage: bash tl-init.sh <new-project-path>"
PROJECT="$(abs "$1")"
LOGS="$TL_HOME/logs"
mkdir -p "$LOGS"

editor_roots() {
  case "$OS" in
    windows)
      local pf="${PROGRAMFILES:-C:\\Program Files}"
      printf '%s\n' "$(cygpath -u "$pf")/Unity/Hub/Editor"
      ;;
    macos) printf '%s\n' "/Applications/Unity/Hub/Editor" ;;
    *) printf '%s\n' "$HOME/Unity/Hub/Editor" ;;
  esac
}

editor_binary() {
  case "$OS" in
    windows) printf '%s/Editor/Unity.exe' "$1" ;;
    macos) printf '%s/Unity.app/Contents/MacOS/Unity' "$1" ;;
    *) printf '%s/Editor/Unity' "$1" ;;
  esac
}

find_unity7() {
  local root dir best=""
  while IFS= read -r root; do
    [ -d "$root" ] || continue
    for dir in "$root"/7000.*; do
      [ -x "$(editor_binary "$dir")" ] || continue
      best="$best
$dir"
    done
  done < <(editor_roots)
  best="$(printf '%s\n' "$best" | sed '/^$/d' | sort -V | tail -n 1)"
  [ -n "$best" ] || return 1
  editor_binary "$best"
}

find_unity_cli() {
  local candidate
  if command -v unity >/dev/null 2>&1; then command -v unity; return 0; fi
  for candidate in \
    "$HOME/.local/bin/unity" \
    "${LOCALAPPDATA:-/nonexistent}/Unity/bin/unity.exe" \
    "${LOCALAPPDATA:-/nonexistent}/Unity/bin/unity"; do
    if [ -x "$candidate" ]; then printf '%s' "$candidate"; return 0; fi
  done
  return 1
}

resolve_unity() {
  if [ -n "${UNITY:-}" ]; then
    [ -x "$UNITY" ] || die "UNITY is set to '$UNITY' but it is not an executable file"
    return 0
  fi
  if UNITY="$(find_unity7)"; then return 0; fi
  say "no Unity 7 editor installed, installing the newest one with the Unity CLI"
  local cli
  cli="$(find_unity_cli)" || die "no Unity 7 editor and no Unity CLI. Install the CLI (Windows: irm https://unity.com/install.ps1 | iex ; Linux/macOS: curl -fsSL https://unity.com/install.sh | bash), open a new shell, then run this again. Or set UNITY to an editor binary."
  "$cli" --no-banner install 7 --yes --accept-eula </dev/null
  UNITY="$(find_unity7)" || die "Unity 7 install finished but no 7000.* editor was found under $(editor_roots)"
}

run_unity() {
  local log="$1"
  shift
  local rc=0
  "$UNITY" -batchmode -nographics -quit -logFile "$(native "$log")" "$@" </dev/null || rc=$?
  return "$rc"
}

error_count() {
  local n
  n="$(grep -c 'error CS' "$1" 2>/dev/null || true)"
  printf '%s' "${n:-0}"
}

pick_json_tool() {
  local tool
  for tool in python3 python node; do
    if command -v "$tool" >/dev/null 2>&1 && "$tool" -c "1" >/dev/null 2>&1; then
      printf '%s' "$tool"
      return 0
    fi
  done
  return 1
}

add_dependency() {
  local manifest="$1" tool
  tool="$(pick_json_tool)" || die "need python3, python or node to edit $manifest"
  local file
  file="$(native "$manifest")"
  case "$tool" in
    node)
      node -e '
        const fs = require("fs");
        const [file, name, url] = process.argv.slice(1);
        const json = JSON.parse(fs.readFileSync(file, "utf8"));
        json.dependencies = json.dependencies || {};
        if (json.dependencies[name] === url) { console.log("present"); process.exit(0); }
        json.dependencies[name] = url;
        fs.writeFileSync(file, JSON.stringify(json, null, 2) + "\n");
        console.log("added");
      ' "$file" "$PKG_NAME" "$PKG_URL"
      ;;
    *)
      "$tool" -I -c '
import json, sys
file, name, url = sys.argv[1:4]
with open(file, encoding="utf-8") as f:
    data = json.load(f)
deps = data.setdefault("dependencies", {})
if deps.get(name) == url:
    print("present")
    sys.exit(0)
deps[name] = url
with open(file, "w", encoding="utf-8", newline="\n") as f:
    json.dump(data, f, indent=2)
    f.write("\n")
print("added")
' "$file" "$PKG_NAME" "$PKG_URL"
      ;;
  esac
}

ensure_tlb() {
  command -v git >/dev/null 2>&1 || die "git is required"
  command -v dotnet >/dev/null 2>&1 || die "the .NET 10 SDK is required to build tlb: https://dotnet.microsoft.com/download/dotnet/10.0"
  dotnet --list-sdks | grep -q '^10\.' || die "the .NET 10 SDK is required to build tlb (found: $(dotnet --list-sdks | tr '\n' ' ')): https://dotnet.microsoft.com/download/dotnet/10.0"
  local checkout="$TL_HOME/tl" before after
  local dll="$checkout/$TLB_REL"
  if [ -d "$checkout/.git" ]; then
    before="$(git -C "$checkout" rev-parse HEAD)"
    git -C "$checkout" pull --ff-only --quiet </dev/null || say "git pull failed, using the existing checkout"
    after="$(git -C "$checkout" rev-parse HEAD)"
  else
    mkdir -p "$TL_HOME"
    git clone --depth 1 "$TL_REPO" "$checkout" </dev/null
    before=""
    after="$(git -C "$checkout" rev-parse HEAD)"
  fi
  if [ ! -f "$dll" ] || [ "$before" != "$after" ]; then
    say "building tlb"
    dotnet build "$checkout/tools/Tl.Bake" -c Release -nologo -v q </dev/null
  else
    say "tlb is up to date"
  fi
  [ -f "$dll" ] || die "tlb build did not produce $dll"
  TLB="$(native "$dll")"
}

write_asmdef() {
  local dir="$PROJECT/Assets/Game" file="$PROJECT/Assets/Game/Game.asmdef"
  mkdir -p "$dir"
  if [ -f "$file" ]; then say "Assets/Game/Game.asmdef exists, left as is"; return 0; fi
  cat > "$file" <<'JSON'
{
  "name": "Game",
  "rootNamespace": "Game",
  "references": [
    "Tl.Core",
    "Tl.Unity",
    "Tl.Unity.Authoring",
    "Tl.Unity.Generate",
    "Unity.Burst",
    "Unity.Collections",
    "Unity.Entities",
    "Unity.Entities.Hybrid",
    "Unity.Mathematics"
  ],
  "allowUnsafeCode": true,
  "autoReferenced": true
}
JSON
  say "wrote Assets/Game/Game.asmdef"
}

START="$(date +%s)"

say "1/6 Unity 7 editor"
resolve_unity
say "editor: $UNITY"

say "2/6 project: $PROJECT"
if [ -f "$PROJECT/ProjectSettings/ProjectVersion.txt" ]; then
  say "project exists"
else
  mkdir -p "$PROJECT"
  CREATE_LOG="$LOGS/create-$(basename "$PROJECT").log"
  rc=0
  run_unity "$CREATE_LOG" -createProject "$(native "$PROJECT")" || rc=$?
  [ -f "$PROJECT/ProjectSettings/ProjectVersion.txt" ] || die "project creation failed (exit $rc), see $CREATE_LOG"
fi

say "3/6 package $PKG_NAME"
MANIFEST="$PROJECT/Packages/manifest.json"
[ -f "$MANIFEST" ] || die "missing $MANIFEST"
say "manifest: $(add_dependency "$MANIFEST")"

say "4/6 tlb"
ensure_tlb
mkdir -p "$PROJECT/.tl"
printf '%s\n' "$TLB" > "$PROJECT/.tl/tlb.path"
say "tlb: $TLB"

say "5/6 game assembly"
write_asmdef

say "6/6 compile"
COMPILE_LOG="$LOGS/compile-$(basename "$PROJECT").log"
rc=0
run_unity "$COMPILE_LOG" -projectPath "$(native "$PROJECT")" || rc=$?
ERRORS="$(error_count "$COMPILE_LOG")"
if [ "$rc" -ne 0 ] || [ "$ERRORS" -ne 0 ]; then
  grep 'error CS' "$COMPILE_LOG" | head -n 20 || true
  die "compile failed (exit $rc, $ERRORS error CS), see $COMPILE_LOG"
fi
RESOLVED="$(ls "$PROJECT/Library/PackageCache" 2>/dev/null | grep "^$PKG_NAME@" || true)"
[ -n "$RESOLVED" ] || die "compile passed but $PKG_NAME is not in Library/PackageCache, see $COMPILE_LOG"

ELAPSED=$(( $(date +%s) - START ))
say "compile: exit $rc, error CS $ERRORS"
say "package resolved: $RESOLVED"
say "done in ${ELAPSED}s"
printf '\nexport TL_TLB="%s"\n\n' "$TLB"
say "next: follow $GUIDE section 3 onward (the Game assembly replaces Demo in step 1)"
