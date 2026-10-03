#!/usr/bin/env python3
"""
Unwind an IL2CPP crash from a Windows minidump, without gdb.

Why this exists
---------------
gdb's backtrace is useless on this game. IL2CPP-compiled x64 code does not keep a
frame pointer, so `bt` reads garbage out of rbp and prints frames like
"#1 0x2 in ?? ()". Worse, at a crash the faulting pc is often not in any mapped
region, so even `x/4i $pc` fails.

Windows x64 does not use frame pointers for unwinding: it uses the .pdata table
(RUNTIME_FUNCTION entries) plus UNWIND_INFO records that describe, per function,
how to restore the stack. That table is present in this GameAssembly.dll
(104,663 entries) and survives into the running process, so a table-driven
unwind works where gdb's heuristic does not.

Output is in STATIC virtual addresses (imagebase 0x180000000), which is exactly
what Ghidra holds after ImportSymbols.java, so frames map straight onto known
symbol names.

Usage
-----
    tools/il2cpp_unwind.py <minidump.dmp> [--gameassembly PATH] [--script-json PATH]
                           [--threads N] [--all-threads]

    # typical
    tools/il2cpp_unwind.py /run/media/.../coredump/crash_1791042906.dmp

Dump files come from the DOTNET_Dbg* environment variables:
    DOTNET_DbgEnableMiniDump=1
    DOTNET_DbgMiniDumpType=1
    DOTNET_DbgMiniDumpName=S:\\coredump\\crash_%t.dmp
    DOTNET_EnableCrashReport=1
"""

import argparse
import bisect
import os
import struct
import sys

DEFAULT_GAMEASSEMBLY = os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
    "gamedir", "GameAssembly.dll")
DEFAULT_SCRIPT_JSON = os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
    "output", "dumper_out", "script.json")

# GameAssembly's preferred image base. Static VAs in Ghidra/objdump are relative
# to this, and the module is mapped elsewhere at runtime under Proton.
STATIC_IMAGE_BASE = 0x180000000

# --- minidump constants -----------------------------------------------------
MDMP_SIGNATURE = 0x504D444D
STREAM_THREAD_LIST = 3
STREAM_MODULE_LIST = 4
STREAM_EXCEPTION = 6
STREAM_MEMORY_LIST = 5
STREAM_MEMORY64_LIST = 9
STREAM_SYSTEM_INFO = 7

# CONTEXT_AMD64 offsets (subset we need), per winnt.h.
CTX_RSP = 0x98
CTX_RBP = 0xA0
CTX_RIP = 0xF8

# EXCEPTION_CODEs worth naming in the report.
KNOWN_EXCEPTIONS = {
    0xC0000005: "EXCEPTION_ACCESS_VIOLATION",
    0xC000001D: "EXCEPTION_ILLEGAL_INSTRUCTION",
    0xC0000094: "EXCEPTION_INT_DIVIDE_BY_ZERO",
    0xC0000096: "EXCEPTION_PRIV_INSTRUCTION",
    0xC00000FD: "EXCEPTION_STACK_OVERFLOW",
    0xC0000409: "EXCEPTION_STACK_BUFFER_OVERRUN (failfast)",
    0xE0434352: "CLR exception",
    0xE0434F4D: "CLR failfast",
    0x80000003: "EXCEPTION_BREAKPOINT",
}


# ---------------------------------------------------------------------------
# minidump parsing
# ---------------------------------------------------------------------------

class MiniDump:
    """Minimal minidump reader: threads, modules, and memory for stack walking."""

    def __init__(self, path):
        self.path = path
        with open(path, "rb") as fh:
            self.data = fh.read()
        self._parse_header()

    def _parse_header(self):
        d = self.data
        (sig, version, nstreams, dir_rva, checksum, timestamp, flags) = \
            struct.unpack_from("<IIIIIII", d, 0)
        if sig != MDMP_SIGNATURE:
            raise ValueError(
                f"not a minidump (signature 0x{sig:08X}, expected 0x{MDMP_SIGNATURE:08X})")
        self.version = version
        self.timestamp = timestamp
        self.flags = flags
        self.streams = {}
        for i in range(nstreams):
            stype, size, rva = struct.unpack_from("<III", d, dir_rva + i * 12)
            # Keep the largest if a type repeats; some writers emit a zero-size one.
            if stype not in self.streams or size > self.streams[stype][0]:
                self.streams[stype] = (size, rva)

    # -- generic helpers ----------------------------------------------------

    def _md_string(self, rva):
        """MINIDUMP_STRING: uint32 byte-length followed by UTF-16LE."""
        n, = struct.unpack_from("<I", self.data, rva)
        raw = self.data[rva + 4:rva + 4 + n]
        return raw.decode("utf-16-le", errors="replace")

    # -- threads ------------------------------------------------------------

    class Thread:
        __slots__ = ("tid", "context_rva", "context_size", "stack_start",
                     "stack_size", "stack_rva", "regs")

        def __init__(self):
            self.regs = {}

    def threads(self):
        if STREAM_THREAD_LIST not in self.streams:
            return []
        _, rva = self.streams[STREAM_THREAD_LIST]
        count, = struct.unpack_from("<I", self.data, rva)
        out = []
        base = rva + 4
        for i in range(count):
            off = base + i * 48
            (tid, suspend, prio_class, prio, teb,
             stack_start, stack_size, stack_rva) = \
                struct.unpack_from("<IIIIQQII", self.data, off)
            # MINIDUMP_THREAD is followed by a LOCATION_DESCRIPTOR for the context.
            ctx_size, ctx_rva = struct.unpack_from("<II", self.data, off + 40)

            t = MiniDump.Thread()
            t.tid = tid
            t.context_rva = ctx_rva
            t.context_size = ctx_size
            t.stack_start = stack_start
            t.stack_size = stack_size
            t.stack_rva = stack_rva

            if ctx_size >= CTX_RIP + 8:
                t.regs["rip"], = struct.unpack_from("<Q", self.data, ctx_rva + CTX_RIP)
                t.regs["rsp"], = struct.unpack_from("<Q", self.data, ctx_rva + CTX_RSP)
                t.regs["rbp"], = struct.unpack_from("<Q", self.data, ctx_rva + CTX_RBP)
            out.append(t)
        return out

    # -- exception ----------------------------------------------------------

    def exception(self):
        """(code, address) or None. Absent for failfast, which has no hardware fault."""
        if STREAM_EXCEPTION not in self.streams:
            return None
        _, rva = self.streams[STREAM_EXCEPTION]
        # MINIDUMP_EXCEPTION_STREAM:
        #   I ThreadId, I __align, MINIDUMP_EXCEPTION, MINIDUMP_LOCATION_DESCRIPTOR
        thread_id, = struct.unpack_from("<I", self.data, rva)
        excl = rva + 8
        code, flags, rec, addr = struct.unpack_from("<IIQQ", self.data, excl)
        return {"thread_id": thread_id, "code": code, "address": addr,
                "name": KNOWN_EXCEPTIONS.get(code, f"0x{code:08X}")}

    # -- modules ------------------------------------------------------------

    def modules(self):
        """list of (base, size, name) sorted by base."""
        if STREAM_MODULE_LIST not in self.streams:
            return []
        _, rva = self.streams[STREAM_MODULE_LIST]
        count, = struct.unpack_from("<I", self.data, rva)
        out = []
        base = rva + 4
        # MINIDUMP_MODULE is 108 bytes.
        for i in range(count):
            off = base + i * 108
            image_base, image_size, _, _, name_rva = \
                struct.unpack_from("<QIIII", self.data, off)
            try:
                name = self._md_string(name_rva)
            except Exception:
                name = "<unreadable>"
            out.append((image_base, image_size, name))
        out.sort(key=lambda m: m[0])
        return out

    # -- memory -------------------------------------------------------------

    def _build_memory_index(self):
        """
        Map virtual address ranges to file offsets, from MemoryList /
        Memory64List. Stack bytes live here, which is what lets us read return
        addresses off the stack.
        """
        if getattr(self, "_mem_index", None) is not None:
            return self._mem_index

        index = []

        if STREAM_MEMORY64_LIST in self.streams:
            _, rva = self.streams[STREAM_MEMORY64_LIST]
            count, base_rva = struct.unpack_from("<QQ", self.data, rva)
            off = rva + 16
            file_off = base_rva
            for _ in range(count):
                start, size = struct.unpack_from("<QQ", self.data, off)
                off += 16
                index.append((start, start + size, file_off))
                file_off += size

        if STREAM_MEMORY_LIST in self.streams:
            _, rva = self.streams[STREAM_MEMORY_LIST]
            count, = struct.unpack_from("<I", self.data, rva)
            for i in range(count):
                start, size, frva = struct.unpack_from("<QII", self.data, rva + 4 + i * 16)
                index.append((start, start + size, frva))

        index.sort(key=lambda e: e[0])
        self._mem_index = index
        return index

    def read_mem(self, addr, length):
        """Read process memory captured in the dump; None when not present."""
        index = self._build_memory_index()
        starts = [e[0] for e in index]
        i = bisect.bisect_right(starts, addr) - 1
        if i < 0:
            return None
        lo, hi, foff = index[i]
        if addr < lo or addr + length > hi:
            return None
        delta = addr - lo
        return self.data[foff + delta: foff + delta + length]

    def read_u64(self, addr):
        raw = self.read_mem(addr, 8)
        return None if raw is None else struct.unpack_from("<Q", raw)[0]

    def covers(self, addr):
        """True when `addr` falls inside some captured memory region.

        Used to distinguish "this dump cannot answer that question" from "the
        answer is empty" - a distinction that costs a lot of time to rediscover.
        """
        index = self._build_memory_index()
        starts = [e[0] for e in index]
        i = bisect.bisect_right(starts, addr) - 1
        if i < 0:
            return False
        lo, hi, _ = index[i]
        return lo <= addr < hi


# ---------------------------------------------------------------------------
# .pdata driven unwinding
# ---------------------------------------------------------------------------

class Unwinder:
    """
    Table-driven x64 unwinder for one module (GameAssembly.dll).

    Reads the module's PE headers from disk, locates .pdata and the exception
    directory, and replays UNWIND_INFO records to walk frames.
    """

    def __init__(self, path):
        self.path = path
        with open(path, "rb") as fh:
            self.pe = fh.read()
        self._parse_pe()

    def _parse_pe(self):
        pe = self.pe
        e_lfanew, = struct.unpack_from("<I", pe, 0x3C)
        if pe[e_lfanew:e_lfanew + 4] != b"PE\0\0":
            raise ValueError("not a PE file")
        coff = e_lfanew + 4
        nsec, = struct.unpack_from("<H", pe, coff + 2)
        opt_size, = struct.unpack_from("<H", pe, coff + 16)
        opt = coff + 20
        magic, = struct.unpack_from("<H", pe, opt)
        if magic != 0x20B:
            raise ValueError(f"expected PE32+ (0x20B), got 0x{magic:X}")

        # Data directory 3 is the exception directory (.pdata).
        dd_count_off = opt + 108
        n_dd, = struct.unpack_from("<I", pe, dd_count_off)
        exc_rva = exc_size = 0
        if n_dd > 3:
            exc_rva, exc_size = struct.unpack_from("<II", pe, dd_count_off + 4 + 3 * 8)
        if exc_rva == 0:
            raise ValueError("no exception directory / .pdata in this PE")

        sec_off = opt + opt_size
        self.sections = []
        for i in range(nsec):
            o = sec_off + i * 40
            name = pe[o:o + 8].rstrip(b"\0").decode("ascii", "replace")
            vsize, vaddr, rawsize, rawptr = struct.unpack_from("<IIII", pe, o + 8)
            self.sections.append((name, vaddr, vsize, rawptr, rawsize))

        self.pdata_rva = exc_rva
        self.pdata_size = exc_size
        self.pdata = self._read_rva(exc_rva, exc_size)
        self.nfunc = len(self.pdata) // 12

        # Sorted start addresses for binary search.
        self.entries = []
        for i in range(self.nfunc):
            s, e, u = struct.unpack_from("<III", self.pdata, i * 12)
            self.entries.append((s, e, u))
        self.entries.sort(key=lambda t: t[0])
        self._starts = [t[0] for t in self.entries]

    def _read_rva(self, rva, size):
        for name, vaddr, vsize, rawptr, rawsize in self.sections:
            if vaddr <= rva < vaddr + max(vsize, rawsize):
                delta = rva - vaddr
                return self.pe[rawptr + delta: rawptr + delta + size]
        raise ValueError(f"rva 0x{rva:X} not in any section")

    def lookup(self, rva):
        """(start, end, unwind_info_rva) for the function containing rva, or None."""
        i = bisect.bisect_right(self._starts, rva) - 1
        if i < 0:
            return None
        s, e, u = self.entries[i]
        return (s, e, u) if s <= rva < e else None

    def unwind_codes(self, unwind_rva):
        """Decode UNWIND_INFO into (version, flags, frame_reg, frame_off, ops)."""
        off = 0
        for name, vaddr, vsize, rawptr, rawsize in self.sections:
            if vaddr <= unwind_rva < vaddr + max(vsize, rawsize):
                off = rawptr + (unwind_rva - vaddr)
                break
        else:
            return None
        hdr = self.pe[off:off + 4]
        if len(hdr) < 4:
            return None
        version = hdr[0] & 0x7
        flags = hdr[0] >> 3
        count = hdr[1]
        frame_reg = hdr[2] & 0xF
        frame_off = (hdr[2] >> 4) | (hdr[3] << 4)
        if version != 1:
            return None
        slots = []
        base = off + 4
        for i in range(count):
            b0 = self.pe[base + i * 2]
            b1 = self.pe[base + i * 2 + 1]
            slots.append((b0, b1))
        return {
            "version": version, "flags": flags, "count": count,
            "frame_reg": frame_reg, "frame_off": frame_off, "slots": slots,
        }

    def find_return_address(self, rsp, rip, dump, module_base, limit=0x8000):
        """
        Scan the stack upward for a plausible return address.

        Needed because at a crash the innermost frame is often outside any
        unwind table (JIT'd code, Wine thunks, a fault inside data), so .pdata
        alone cannot get past it. Accepting only candidates that land inside a
        .pdata-covered function keeps the false-positive rate low.

        Returns (runtime address, stack offset) or (None, scanned bytes).
        """
        off = 0
        while off < limit:
            raw = dump.read_u64(rsp + off)
            if raw is None:
                return None, off
            # raw is a RUNTIME address; convert to the module-relative RVA that
            # .pdata is indexed by.
            rva = raw - module_base
            if 0 <= rva < 0x100000000 and static_rva_in_pdata(self, rva):
                return raw, off
            off += 8
        return None, off


def static_rva_in_pdata(unw, rva):
    return unw.lookup(rva) is not None


# ---------------------------------------------------------------------------
# symbol resolution via Il2CppDumper's script.json
# ---------------------------------------------------------------------------

class Symbols:
    """
    Maps a static VA to "Type.Method" using Il2CppDumper's script.json.

    Note the Address field is an RVA, not a VA. Several methods legitimately
    share an address (overloads / thunks), so each address maps to a list.
    """

    def __init__(self, path):
        self.by_rva = {}
        self.loaded = False
        if not path or not os.path.exists(path):
            return
        import json
        with open(path, "r", encoding="utf-8") as fh:
            doc = json.load(fh)
        for e in doc.get("ScriptMethod", []):
            rva = e.get("Address")
            if rva is None:
                continue
            self.by_rva.setdefault(rva, []).append(e)
        self.sorted_rvas = sorted(self.by_rva)
        self.loaded = True

    def nearest(self, static_va, slack=None):
        """
        Symbol for the function containing static_va.

        script.json records method ENTRY points, not every internal address, so an
        interior address is attributed to the nearest preceding entry - but only
        within a plausible function size, or an arbitrary address gets blamed on a
        function megabytes away.

        `slack` defaults to the gap to the next known entry, capped at 0x2000.
        Bounding by the NEXT entry is what keeps this honest, because script.json's
        spacing varies widely across the binary (51,251 entries over ~24 MB).
        """
        if not self.loaded:
            return None
        rva = static_va - STATIC_IMAGE_BASE

        # Guard against wrap-around: bisecting a value below the first entry
        # returns index 0, whose predecessor is a nonsensical "match".
        if rva < self.sorted_rvas[0]:
            return None

        i = bisect.bisect_right(self.sorted_rvas, rva) - 1
        if i < 0:
            return None
        best = self.sorted_rvas[i]
        if best > rva:
            return None

        if slack is None:
            nxt = self.sorted_rvas[i + 1] if i + 1 < len(self.sorted_rvas) else None
            limit = (nxt - best) if nxt is not None else 0x2000
            slack = min(max(limit, 0x10), 0x2000)

        if rva - best > slack:
            return None
        entries = self.by_rva[best]
        e = entries[0]
        ts = (e.get("TypeSignature") or "").strip()
        sig = (e.get("Signature") or "").strip()
        name = e.get("Name") or "?"
        offset = rva - best
        label = f"{ts}::{sig}" if ts or sig else name
        return {"name": name, "label": label, "offset": offset,
                "overloads": len(entries)}


# ---------------------------------------------------------------------------
# unwind loop
# ---------------------------------------------------------------------------

def unwind_thread(dump, unw, thread, module_base, module_size, max_frames=48):
    """
    Walk one thread's stack. Returns a list of frame dicts.

    Strategy: try .pdata unwinding first (authoritative); when it cannot make
    progress - no .pdata entry for the current pc, or rsp fails to advance -
    fall back to scanning the stack for the next return address that lands in a
    .pdata-covered function. That hybrid is what makes this work at a crash,
    where the innermost frame is often outside any unwind table.
    """
    regs = thread.regs
    if "rip" not in regs:
        return []

    rip = regs["rip"]
    rsp = regs["rsp"]
    rbp = regs.get("rbp", 0)
    frames = []
    seen = set()

    for _ in range(max_frames):
        if rip == 0:
            break
        key = (rip, rsp)
        if key in seen:
            frames.append({"va": rip, "rsp": rsp, "note": "<unwind loop detected>"})
            break
        seen.add(key)

        # rip may be inside GameAssembly (then we can symbolise it) or elsewhere
        # (Wine thunks, JIT'd stubs, another DLL). The bound must be the module's
        # real mapped size: using a large arbitrary constant lets out-of-range
        # addresses masquerade as "in module" and produce bogus frames.
        rel = rip - module_base
        in_module = 0 <= rel < module_size
        rva = rel if in_module else None
        frame = {"va": rip, "rsp": rsp, "rva": rva}
        frames.append(frame)

        # --- attempt .pdata unwind ---
        next_rip = None
        next_rsp = None

        if in_module:
            info = unw.lookup(rva)
            if info is not None:
                codes = unw.unwind_codes(info[2])
                # A plain "push rbp" prologue with no other ops is the common
                # leaf case; frame_reg tells us if rbp is chained.
                if codes and codes["frame_reg"] == 0 and codes["frame_off"] == 0:
                    # No frame register: the return address sits at rsp for a
                    # leaf, otherwise we cannot derive it without full op replay.
                    cand = dump.read_u64(rsp)
                    if cand and static_rva_in_pdata(unw, cand - module_base):
                        next_rip, next_rsp = cand, rsp + 8

        if next_rip is None:
            # Fallback: scan for the next return address inside .pdata.
            addr, off = unw.find_return_address(rsp, rip, dump, module_base)
            if addr is not None:
                next_rip, next_rsp = addr, rsp + off + 8
                frame["via"] = "stack scan"

        if next_rip is None or next_rsp <= rsp:
            break
        rip, rsp = next_rip, next_rsp

    return frames


def main():
    ap = argparse.ArgumentParser(
        description="Unwind an IL2CPP crash from a Windows minidump using .pdata.")
    ap.add_argument("dump", help="minidump produced by DOTNET_DbgEnableMiniDump")
    ap.add_argument("--gameassembly", default=DEFAULT_GAMEASSEMBLY,
                    help=f"GameAssembly.dll path (default: {DEFAULT_GAMEASSEMBLY})")
    ap.add_argument("--script-json", default=DEFAULT_SCRIPT_JSON,
                    help="Il2CppDumper script.json for symbol names (optional)")
    ap.add_argument("--threads", type=int, default=3,
                    help="how many threads to unwind; the crashing one is first (default 3)")
    ap.add_argument("--all-threads", action="store_true", help="unwind every thread")
    ap.add_argument("--max-frames", type=int, default=48)
    args = ap.parse_args()

    if not os.path.exists(args.gameassembly):
        sys.exit(f"GameAssembly.dll not found: {args.gameassembly}")

    print(f"dump      : {args.dump}")
    dump = MiniDump(args.dump)

    exc = dump.exception()
    if exc:
        print(f"exception : {exc['name']}  address=0x{exc['address']:X}  tid={exc['thread_id']}")
    else:
        print("exception : none recorded (failfast / abort produce no hardware fault)")

    # The module is mapped somewhere else at runtime under Proton; every .pdata
    # lookup is relative to this base, so it must come from the dump itself
    # Module names in a Windows minidump use backslash paths (S:\...\GameAssembly.dll).
    # os.path.basename does NOT split those on Linux, so normalise manually.
    def _leaf(name):
        return name.replace("\\", "/").rsplit("/", 1)[-1].lower()

    module_base = module_size = None
    all_modules = list(dump.modules())
    for mbase, msize, mname in all_modules:
        if _leaf(mname) == "gameassembly.dll":
            module_base, module_size = mbase, msize
            break
    if module_base is None:
        sys.exit("GameAssembly.dll not present in the dump's module list")
    print(f"module    : GameAssembly.dll base=0x{module_base:X} size=0x{module_size:X}")

    # Attribute every frame to its owning module. "outside GameAssembly" is far
    # too coarse: a frame in ntdll.dll is a completely different diagnosis from
    # one in coreclr.dll, and telling them apart by hand is most of the job.
    module_ranges = sorted((b, b + s, _leaf(n)) for b, s, n in all_modules)

    def owning_module(va):
        for lo, hi, leaf in module_ranges:
            if lo <= va < hi:
                return leaf, va - lo
        return None, None

    # If no frame can ever be symbolised because the code pages never made it
    # into the dump, say so once up front - otherwise the reader chases empty
    # symbol tables. This is a capture-scope fact, not a parser failure.
    if not dump.covers(module_base):
        print("note      : GameAssembly code pages are NOT in this dump -> frames")
        print("            inside the game can only be located, not symbolised")

    unw = Unwinder(args.gameassembly)
    print(f"pdata     : {unw.nfunc} RUNTIME_FUNCTION entries")

    syms = Symbols(args.script_json)
    print(f"symbols   : {'script.json loaded' if syms.loaded else 'unavailable (addresses only)'}")

    if args.all_threads:
        threads = dump.threads()
    else:
        threads = []
        allt = dump.threads()
        if exc:
            first = [t for t in allt if t.tid == exc["thread_id"]]
            threads.extend(first)
        for t in allt:
            if len(threads) >= args.threads:
                break
            if t not in threads:
                threads.append(t)

    for t in threads:
        line = f"\n===== thread {t.tid}  rip=0x{t.regs.get('rip', 0):X} rsp=0x{t.regs.get('rsp', 0):X}"
        if exc and t.tid == exc["thread_id"]:
            line += "   <-- faulting thread"
        print(line)
        for i, fr in enumerate(unwind_thread(dump, unw, t, module_base, module_size, args.max_frames)):
            va = fr["va"]
            static_va = None
            if fr.get("rva") is not None:
                static_va = STATIC_IMAGE_BASE + fr["rva"]

            entry = syms.nearest(static_va) if static_va is not None else None
            if entry:
                off = f"+0x{entry['offset']:X}" if entry["offset"] else ""
                label = f"{entry['label']}{off}"
                if entry["overloads"] > 1:
                    label += f"   ({entry['overloads']} overloads at this address)"
            elif static_va is not None:
                label = f"<GameAssembly+0x{fr['rva']:X}>"
            else:
                # Name the owning module rather than just "outside". A frame in
                # ntdll.dll means Wine's dispatch path; one in coreclr.dll means
                # the managed runtime. That difference is usually the diagnosis.
                leaf, moff = owning_module(va)
                label = (f"<{leaf}+0x{moff:X}>" if leaf
                         else "<unmapped / not captured>")

            note = f"   [{fr['note']}]" if fr.get("note") else ""
            via = f"   ({fr['via']})" if fr.get("via") else ""
            # Static VA is printed alongside the runtime one because that is the
            # form Ghidra/objdump/script.json use and what a reader can paste
            # straight into them.
            sva = f" (static 0x{static_va:012X})" if static_va is not None else ""
            print(f"  #{i:<2} 0x{va:012X}{sva}  {label}{via}{note}")

    print("\nFrame addresses are RUNTIME addresses; the \"static\" value next to each is the\n"
          "Ghidra/objdump VA (imagebase 0x180000000) and is what maps onto symbol names.")
    sys.exit(0)


if __name__ == "__main__":
    main()
