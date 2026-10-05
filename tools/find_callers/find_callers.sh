#!/usr/bin/env bash
# Build (once) and run tools/find_callers against the cpp2il output.
#
# Usage:  tools/find_callers/find_callers.sh <methodName> [find_callers options...]
#         tools/find_callers/find_callers.sh --build          # just build
#
# Examples:
#   tools/find_callers/find_callers.sh GetObstacleRemoveCostResource
#   tools/find_callers/find_callers.sh ObstacleCanDestroy --outgoing -v
#   tools/find_callers/find_callers.sh UpgradeCityHouse -t GameController
#
# Why a wrapper rather than a published binary: the tool depends on Mono.Cecil, which
# ships with MelonLoader. The wrapper resolves that path and builds on first use, so
# nothing has to be committed as a binary and no new dependency is introduced.
#
# Full rationale, including why this is a standalone tool rather than an MCP tool:
# docs/find-callers.md  (and the header of Program.cs)
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"

CECIL="$REPO/gamedir/MelonLoader/net6/Mono.Cecil.dll"
if [[ ! -f "$CECIL" ]]; then
    echo "error: Mono.Cecil not found at $CECIL" >&2
    echo "       It ships with MelonLoader; check that gamedir points at the game." >&2
    exit 1
fi

BIN="$HERE/bin/Release/net8.0/find_callers"

build() {
    dotnet build "$HERE/find_callers.csproj" -c Release -p:CecilPath="$CECIL" "$@"
}

if [[ "${1:-}" == "--build" ]]; then
    build
    exit 0
fi

# Build on first use, or when the sources are newer than the binary.
if [[ ! -x "$BIN" ]] || [[ -n "$(find "$HERE" -name '*.cs' -newer "$BIN" 2>/dev/null)" ]]; then
    build >/dev/null
fi

# Default the search directory to the repo's cpp2il output unless the caller overrides it.
if [[ "$*" != *"--dir"* && "$*" != *"-d "* ]]; then
    exec "$BIN" "$@" --dir "$REPO/output/cpp2il_out"
fi

exec "$BIN" "$@"
