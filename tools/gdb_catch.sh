#!/usr/bin/env bash
# Attach gdb to the running LongYinLiZhiZhuan process and capture the crash.
#
# Usage:  tools/gdb_catch.sh <pid>
#
# Strategy:
#   - resolve the anonymous r-xp code region that holds the relocated GameAssembly code
#   - compute OnEnter runtime address = region_base_of_module + 0x873b20
#     (we derive the module base from the log's own "运行时 0x..." line, or by probing)
#   - catch SIGSEGV / SIGABRT and dump registers + backtrace + faulting address
#
# NOTE: must run with full access (bwrap unshare-pid hides the game otherwise).
set -uo pipefail

PID="${1:?usage: gdb_catch.sh <pid>}"

if [ ! -d "/proc/$PID" ]; then
  echo "no such pid: $PID" >&2
  exit 1
fi

echo "=== target: $(tr -d '\0' < /proc/$PID/comm) (pid $PID) ==="

# Find the anonymous executable region containing the game's JIT'd/relocated code.
# GameAssembly.dll only maps its header from file; the real code is an anon r-xp region.
echo "=== candidate code regions (r-xp, anon, >4MB) ==="
python3 - "$PID" <<'PY'
import sys
pid = sys.argv[1]
for line in open(f"/proc/{pid}/maps"):
    p = line.split()
    lo, hi = (int(x, 16) for x in p[0].split('-'))
    if "r-xp" in p[1] and p[-1] == "0" and (hi - lo) > 4 * 1024 * 1024:
        print(f"  {p[0]}  size={ (hi-lo)//1024//1024 }MB")
PY

GDB_SCRIPT=$(mktemp)
cat > "$GDB_SCRIPT" <<'GDB'
set pagination off
set confirm off
set print pretty off
set disassembly-flavor intel

# Silence the bogus "i386 not compatible" noise from managed DLLs.
set logging file /tmp/gdb_crash.log
set logging overwrite on
set logging enabled on

handle SIGSEGV stop print nopass
handle SIGABRT stop print nopass
handle SIGILL stop print nopass
handle SIGBUS stop print nopass
handle SIGFPE stop print nopass
# SIGUSR1/SIGUSR2 是 .NET 运行时的常规信号（GC、线程挂起），
# gdb 若停在它们上面会卡在噪音上、永远等不到真正的崩溃。
handle SIGUSR1 nostop noprint pass
handle SIGUSR2 nostop noprint pass
handle SIG32 nostop noprint pass
handle SIG33 nostop noprint pass
handle SIGPIPE nostop noprint pass
echo \n=== attached, waiting for crash ===\n
continue
echo \n=== STOPPED ===\n
info registers
echo \n=== backtrace ===\n
bt
echo \n=== faulting instruction ===\n
x/4i $pc
echo \n=== stack ===\n
x/16gx $rsp
echo \n=== rax/rcx/rdx targets if readable ===\n
x/4gx $rcx
x/4gx $rdx
set logging enabled off
quit
GDB

timeout 600 gdb -p "$PID" -batch -x "$GDB_SCRIPT" 2>&1 | grep -v "Shared library architecture" | tail -80
rc=$?
rm -f "$GDB_SCRIPT"
echo "=== gdb exit: $rc ==="
