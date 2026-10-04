#!/usr/bin/env bash
# Decompile a third-party mod DLL into a compilable C# project.
#
# Usage:  tools/recompile_mod.sh <Mod.dll> [--out <dir>] [--keep-going] [--deploy]
#
# What it does (see docs/mod-recompilation.md for the reasoning behind each step):
#   1. ilspycmd -p -ds UsingDeclarations=false  -> fully-qualified types, no `using` ambiguity
#      + -r reference paths, WITHOUT which every game/Harmony/MelonLoader type is lost
#   2. patch Properties/AssemblyInfo.cs  (the one file that cannot be fully qualified)
#   3. rewrite Volatile/Interlocked.Read(in x) -> Read(ref x)   (ilspycmd emits `in`)
#   4. dotnet build -c Release
#   5. verify the artifact: size sanity + MelonInfo survived in the binary
#
# The reference paths MUST be absolute: ilspycmd writes them into the generated csproj,
# and relative ones come out relative to the CWD, not to the project dir, so the build
# then fails with a wall of CS0246 that looks like a missing reference.
#
# NOTE: reading gamedir/ needs no elevation (AGENTS.md 3.2). Only --deploy writes.
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GAMEDIR="$REPO_ROOT/gamedir"
IL2CPP_DIR="$GAMEDIR/MelonLoader/Il2CppAssemblies"
MELON_DIR="$GAMEDIR/MelonLoader/net6"

OUT=""
DEPLOY=0
KEEP_GOING=0
DLL=""

usage() {
	sed -n '2,17p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
	exit "${1:-0}"
}

while [ $# -gt 0 ]; do
	case "$1" in
		-h|--help) usage 0 ;;
		--out)     OUT="${2:?--out needs a directory}"; shift 2 ;;
		--deploy)  DEPLOY=1; shift ;;
		--keep-going) KEEP_GOING=1; shift ;;
		-*)        echo "unknown option: $1" >&2; usage 1 ;;
		*)         DLL="$1"; shift ;;
	esac
done

[ -n "$DLL" ] || { echo "error: no DLL given" >&2; usage 1; }

# Accept either a path or a bare mod name; resolve bare names against gamedir/Mods.
if [ ! -f "$DLL" ]; then
	CAND="$GAMEDIR/Mods/$DLL"
	[ -f "$CAND" ] || CAND="$GAMEDIR/Mods/$DLL.dll"
	if [ -f "$CAND" ]; then
		DLL="$CAND"
	else
		echo "error: no such DLL: $DLL (also tried gamedir/Mods/)" >&2
		exit 1
	fi
fi

MOD="$(basename "$DLL" .dll)"
OUT="${OUT:-$REPO_ROOT/output/${MOD}_proj}"

for d in "$IL2CPP_DIR" "$MELON_DIR"; do
	[ -d "$d" ] || {
		echo "error: missing reference directory: $d" >&2
		echo "       (is the gamedir symlink set up? see AGENTS.md 2)" >&2
		exit 1
	}
done

command -v ilspycmd >/dev/null || { echo "error: ilspycmd not on PATH" >&2; exit 1; }
command -v dotnet   >/dev/null || { echo "error: dotnet not on PATH" >&2; exit 1; }

echo "==> mod:      $MOD"
echo "==> source:   $DLL"
echo "==> out:      $OUT"
echo

# ---------------------------------------------------------------------------
# 1. decompile with fully-qualified type names
echo "==> [1/5] decompiling (fully-qualified types + reference paths)"
rm -rf "$OUT"
# Absolute -r paths: ilspycmd turns these into correct ../../gamedir/... HintPaths.
# Relative ones would be emitted relative to the CWD and silently break the build.
ilspycmd -o "$OUT" -p -ds "UsingDeclarations=false" \
	-r "$IL2CPP_DIR" \
	-r "$MELON_DIR" \
	"$DLL" || { echo "error: ilspycmd failed" >&2; exit 1; }

PROJ="$OUT/$MOD.csproj"
[ -f "$PROJ" ] || { echo "error: expected project not generated: $PROJ" >&2; exit 1; }

# ---------------------------------------------------------------------------
# 2. AssemblyInfo.cs is the one file that cannot be fully qualified, because its
#    [assembly: ...] attributes sit at file scope and never see a type prefix.
# ---------------------------------------------------------------------------
echo "==> [2/5] patching Properties/AssemblyInfo.cs"
ASMINFO="$OUT/Properties/AssemblyInfo.cs"
if [ -f "$ASMINFO" ]; then
	if ! grep -q "^using MelonLoader;" "$ASMINFO"; then
		sed -i '1i using MelonLoader;\nusing UnityEngine;' "$ASMINFO"
		echo "    added: using MelonLoader; using UnityEngine;"
	else
		echo "    already patched, skipped"
	fi
else
	echo "    warning: no Properties/AssemblyInfo.cs found (MelonInfo may be missing)"
fi

# ---------------------------------------------------------------------------
# 2b. Top up the reference list.
#
# ilspycmd emits only the assemblies it directly observed being used, which is not
# enough: a mod whose signatures mention Il2CppObjectBase (or whose code touches
# Il2Cppmscorlib types) needs those transitively, and the build fails with CS0012
# "type X is defined in an assembly that is not referenced" rather than anything
# pointing at the csproj.
#
# Appending the project's standard set (AGENTS.md 3.3) is harmless when entries are
# already present - duplicate Reference items with the same Include are fine - and it
# is what makes mods that do not reference these directly compile at all.
# ---------------------------------------------------------------------------
echo "==> [2b/5] adding the standard reference set"
python3 - "$PROJ" "$REPO_ROOT" <<'PYEOF'
import sys, os

proj, repo = sys.argv[1], sys.argv[2]
# Paths are written relative to the PROJECT directory, which is what MSBuild resolves
# HintPath against. The project always lives at <repo>/output/<Mod>_proj/, so the
# repo root is two levels up.
rel = os.path.relpath(repo, os.path.dirname(proj)).replace(os.sep, '/')

refs = [
    ('Il2CppInterop.Runtime', 'MelonLoader/net6/Il2CppInterop.Runtime.dll'),
    ('Il2CppInterop.HarmonySupport', 'MelonLoader/net6/Il2CppInterop.HarmonySupport.dll'),
    ('MelonLoader', 'MelonLoader/net6/MelonLoader.dll'),
    ('0Harmony', 'MelonLoader/net6/0Harmony.dll'),
    ('Il2Cppmscorlib', 'MelonLoader/Il2CppAssemblies/Il2Cppmscorlib.dll'),
    ('Il2CppSystem', 'MelonLoader/Il2CppAssemblies/Il2CppSystem.dll'),
    ('Il2CppSystem.Core', 'MelonLoader/Il2CppAssemblies/Il2CppSystem.Core.dll'),
    ('Assembly-CSharp', 'MelonLoader/Il2CppAssemblies/Assembly-CSharp.dll'),
    ('UnityEngine', 'MelonLoader/Il2CppAssemblies/UnityEngine.dll'),
    ('UnityEngine.CoreModule', 'MelonLoader/Il2CppAssemblies/UnityEngine.CoreModule.dll'),
]

items = []
for name, tail in refs:
    path = '%s/gamedir/%s' % (rel, tail)
    if not os.path.exists(os.path.join(os.path.dirname(proj), path)):
        continue  # never reference something that is not actually there
    items.append(
        '    <Reference Include="%s">'
        '<HintPath>%s</HintPath><Private>false</Private></Reference>' % (name, path)
    )

block = ('  <!-- Added by tools/recompile_mod.sh: ilspycmd only emits directly-observed\n'
         '       references, which is not enough for transitive type requirements. -->\n'
         '  <ItemGroup>\n' + '\n'.join(items) + '\n  </ItemGroup>\n')

src = open(proj, encoding='utf-8').read()
marker = '</Project>'
idx = src.rindex(marker)
src = src[:idx] + block + src[idx:]
open(proj, 'w', encoding='utf-8').write(src)
print('    added %d reference(s)' % len(items))
PYEOF

# ---------------------------------------------------------------------------
# 3. ilspycmd emits `Volatile.Read(in x)` / `Interlocked.Read(in x)`, but those
#    parameters are `ref` -> CS1620. Purely mechanical.
# ---------------------------------------------------------------------------
echo "==> [3/5] rewriting Read(in x) -> Read(ref x)"
mapfile -t READ_FILES < <(grep -rl "Read(in " --include=*.cs "$OUT" 2>/dev/null)
if [ "${#READ_FILES[@]}" -gt 0 ]; then
	sed -i -E 's/(Volatile|Interlocked)\.Read\(in ([A-Za-z_][A-Za-z0-9_]*)\)/\1.Read(ref \2)/g' \
		"${READ_FILES[@]}"
	echo "    patched ${#READ_FILES[@]} file(s)"
else
	echo "    nothing to patch"
fi

# ---------------------------------------------------------------------------
# 4. build
# ---------------------------------------------------------------------------
echo "==> [4/5] building (Release)"
BUILD_LOG="$OUT/.build.log"
if ! dotnet build "$PROJ" -c Release >"$BUILD_LOG" 2>&1; then
	echo "    BUILD FAILED"
	grep -E "error (CS|MSB)[0-9]+" "$BUILD_LOG" | sed 's/^/    /' | head -25
	echo
	echo "    full log: $BUILD_LOG"
	echo

	# Group the errors by kind: the fix differs completely between classes, and the
	# decompiler's own bugs are NOT worth chasing - they need the source, or a different
	# decompiler, and no amount of csproj editing will help.
	echo "    error classes seen:"
	grep -oE "(CS|MSB)[0-9]+" "$BUILD_LOG" | sort | uniq -c | sort -rn \
		| awk '{printf "      %-6s x%s\n", $2, $1}' | head -6
	echo
	if grep -qE "error CS0246|error CS0012" "$BUILD_LOG"; then
		echo "    CS0246/CS0012 = missing type/reference:"
		echo "      - check the HintPaths in $PROJ resolve (relative-to-project, not CWD)"
		echo "      - confirm a type's real namespace on the ORIGINAL dll:"
		echo "          ilspycmd -m \"<member id>\" '$DLL'"
	fi
	if grep -qE "error CS0029|error CS0030|error CS1503|error CS1525|error CS1003|error CS1513" "$BUILD_LOG"; then
		echo "    CS0029/CS0030/CS1503/CS15xx = the DECOMPILER emitted wrong or invalid C#."
		echo "      These are ilspycmd bugs (observed: a game field emitted as List when it is"
		echo "      really a Dictionary; an 'a ? b ?) ?? c' syntax error). They are not fixable by"
		echo "      editing the project - that mod needs its original source, or manual repair"
		echo "      of each site. See docs/mod-recompilation.md 'known limitations'."
	fi
	[ "$KEEP_GOING" -eq 1 ] || exit 1
else
	# Surface the usual noise so it is not silently ignored, but do not fail on it.
	grep -E "warning (CS|MSB)[0-9]+" "$BUILD_LOG" | head -3 | sed 's/^/    /'
	echo "    ok"
fi

# The artifact is named after <AssemblyName> in the csproj, which is NOT necessarily the
# DLL's filename on disk. RefreshCraft.dll declares AssemblyName
# 'LongYinLiZhiZhuanMelonLoader', so assuming $MOD.dll silently looked for a file that
# was never going to exist (and made a successful build look like a failure).
ASM_NAME=$(sed -n 's:.*<AssemblyName>\([^<]*\)</AssemblyName>.*:\1:p' "$PROJ" | head -1)
[ -n "$ASM_NAME" ] || ASM_NAME="$MOD"
BUILT="$OUT/bin/Release/net6.0/$ASM_NAME.dll"
if [ ! -f "$BUILT" ]; then
	# Fall back to the artifact the build just produced. Guard on mtime so a stale
	# copy-local dependency (ReferenceCopyLocalPaths can drop game DLLs here) is never
	# mistaken for the mod itself.
	CAND=$(find "$OUT/bin/Release/net6.0" -maxdepth 1 -name '*.dll' -newer "$PROJ" 2>/dev/null | head -1)
	if [ -n "$CAND" ]; then BUILT="$CAND"; fi
fi
[ -f "$BUILT" ] || { echo "error: build reported success but no DLL found under $OUT/bin/Release/net6.0/" >&2; exit 1; }
[ "$ASM_NAME" != "$MOD" ] && echo "    note: assembly name ($ASM_NAME) differs from file name ($MOD.dll)"

# ---------------------------------------------------------------------------
# 5. verify the artifact. "Build succeeded" != "artifact is usable":
#    - a suspiciously small file means references got inlined or the build was partial
#    - MelonInfo/MelonGame must survive, or MelonLoader will not recognise the mod
# ---------------------------------------------------------------------------
echo "==> [5/5] verifying artifact"
NEW_SIZE=$(stat -c%s "$BUILT")
OLD_SIZE=$(stat -c%s "$DLL")
echo "    rebuilt: $NEW_SIZE bytes"
echo "    original: $OLD_SIZE bytes"

RATIO_PCT=$(( NEW_SIZE * 100 / OLD_SIZE ))
if [ "$RATIO_PCT" -lt 50 ]; then
	echo "    WARNING: rebuilt is only ${RATIO_PCT}% of the original size - inspect before using"
elif [ "$RATIO_PCT" -gt 200 ]; then
	echo "    WARNING: rebuilt is ${RATIO_PCT}% of the original size - inspect before using"
else
	echo "    size looks sane (${RATIO_PCT}% of original)"
fi

# MelonInfo's display name is the reliable proof that the attribute survived.
# The type is written qualified (MelonLoader.MelonInfo) under UsingDeclarations=false,
# so the pattern must allow for that - a bare 'MelonInfo(' never matches and would make
# this whole check a silent no-op.
DISPLAY_NAME=$(grep -oP '\[assembly:\s*(?:MelonLoader\.)?MelonInfo\([^,]+,\s*"\K[^"]+' "$ASMINFO" 2>/dev/null | head -1)
if [ -z "$DISPLAY_NAME" ]; then
	echo "    note: could not read the MelonInfo display name from AssemblyInfo.cs;"
	echo "          skipping the attribute-survival check (verify manually if it matters)"
fi
if [ -n "$DISPLAY_NAME" ]; then
	if python3 -c "
import sys
d = open(sys.argv[1], 'rb').read()
sys.exit(0 if sys.argv[2].encode('utf-8') in d else 1)
" "$BUILT" "$DISPLAY_NAME" 2>/dev/null; then
		echo "    MelonInfo display name present in binary: $DISPLAY_NAME"
	else
		echo "    WARNING: MelonInfo display name NOT found in the binary."
		echo "             MelonLoader may not recognise the mod. Check AssemblyInfo.cs."
	fi
fi

echo
echo "==> done"
echo "    project:  $OUT"
echo "    artifact: $BUILT"
echo
echo "    Reminder: recompiling is faithful but not identical - before changing code,"
echo "    confirm the real namespace of any ambiguous type with:"
echo "      ilspycmd -m \"<member id>\" \"$DLL\""
echo "    (docs/mod-recompilation.md 5)"

if [ "$DEPLOY" -eq 1 ]; then
	echo
	echo "==> deploying to $GAMEDIR/Mods/$MOD.dll"
	echo "    (requires write access to gamedir; sandbox may deny this)"
	if cp "$BUILT" "$GAMEDIR/Mods/$MOD.dll"; then
		md5sum "$BUILT" "$GAMEDIR/Mods/$MOD.dll"
		if [ "$(md5sum <"$BUILT" | cut -d' ' -f1)" = "$(md5sum <"$GAMEDIR/Mods/$MOD.dll" | cut -d' ' -f1)" ]; then
			echo "    md5 match - deployed"
			echo "    verify sizes match too: do not trust md5 alone (docs/ilrepack.md)"
			echo "    must COLD START the game before believing any behaviour change (AGENTS.md 3.2.1)"
		else
			echo "    ERROR: md5 mismatch after copy" >&2
			exit 1
		fi
	else
		echo "    ERROR: copy failed (gamedir is read-only without elevation)" >&2
		exit 1
	fi
fi
