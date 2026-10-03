using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Iced.Intel;
using MelonMCP.Server;
using Newtonsoft.Json.Linq;

namespace MelonMCP.Tools
{
    /// <summary>
    /// Shared decoding helpers for the disasm / read_mem / resolve_jump tools.
    ///
    /// Backed by Iced, which ships with MelonLoader (net6/Iced.dll), so no extra dependency is
    /// needed and the mod stays single-file.
    /// </summary>
    internal static class DisassemblyHelper
    {
        internal sealed class DecodedInstruction
        {
            public string Address { get; set; }
            public string StaticVa { get; set; }
            public string Bytes { get; set; }
            public string Mnemonic { get; set; }
            public string Operands { get; set; }
            public string Text { get; set; }
            public int Length { get; set; }
            public ulong NextIp { get; set; }
        }

        /// <summary>
        /// Decodes up to <paramref name="count"/> instructions starting at a runtime address.
        /// Returns null with a reason when the address cannot be read.
        /// </summary>
        internal static List<DecodedInstruction> Decode(long runtimeAddress, int count, out string error)
        {
            error = null;
            var result = new List<DecodedInstruction>();

            if (runtimeAddress == 0)
            {
                error = "address resolved to 0";
                return null;
            }

            // Read a generous window: x86 instructions top out at 15 bytes, so this covers any
            // reasonable request without needing to know instruction boundaries in advance.
            var windowSize = Math.Max(64, Math.Min(count, 512) * 16);
            if (!NativeMemory.TryReadBytes(runtimeAddress, windowSize, out var bytes))
            {
                error = $"could not read {windowSize} bytes at 0x{runtimeAddress:X}";
                return null;
            }

            var decoder = Decoder.Create(64, bytes, (ulong)runtimeAddress);
            var formatter = new NasmFormatter();
            formatter.Options.DigitSeparator = "";
            formatter.Options.UppercaseHex = false;
            var output = new StringOutput();

            for (int i = 0; i < count; i++)
            {
                // Stop cleanly at the end of the readable window instead of emitting garbage.
                if (decoder.IP >= (ulong)(runtimeAddress + windowSize)) break;

                var instr = decoder.Decode();
                if (instr.Code == Code.INVALID) break;

                var length = instr.Length;
                var offset = (int)(instr.IP - (ulong)runtimeAddress);
                var instrBytes = new byte[length];
                if (offset + length <= bytes.Length)
                {
                    Array.Copy(bytes, offset, instrBytes, 0, length);
                }

                formatter.Format(instr, output);
                var text = output.ToStringAndReset();

                // Split "mnemonic operands" for callers that want them separately.
                var space = text.IndexOf(' ');
                var mnemonic = space < 0 ? text : text.Substring(0, space);
                var operands = space < 0 ? "" : text.Substring(space + 1).Trim();

                result.Add(new DecodedInstruction
                {
                    Address = $"0x{instr.IP:X}",
                    StaticVa = $"0x{NativeMemory.RuntimeToStaticVa((long)instr.IP):X}",
                    Bytes = BitConverter.ToString(instrBytes).Replace("-", " "),
                    Mnemonic = mnemonic,
                    Operands = operands,
                    Text = text,
                    Length = length,
                    NextIp = instr.NextIP
                });
            }

            return result;
        }

        /// <summary>
        /// Follows an unconditional jump at the given address to its real destination, the way a
        /// human would read `ff 25` / `e9` by hand.
        ///
        /// Handles the two encodings that actually matter here:
        ///   e9 &lt;rel32&gt;            - direct relative jump
        ///   ff 25 &lt;disp32&gt;         - jmp qword ptr [rip+disp32]: read the pointer out of the slot
        /// plus the short form eb &lt;rel8&gt;, and indirect jmp rax / call rax for completeness.
        /// </summary>
        internal static object ResolveJump(long runtimeAddress, out string error)
        {
            error = null;
            if (!NativeMemory.TryReadBytes(runtimeAddress, 16, out var b))
            {
                error = $"could not read bytes at 0x{runtimeAddress:X}";
                return null;
            }

            var info = new Dictionary<string, object>
            {
                ["address"] = $"0x{runtimeAddress:X}",
                ["bytes"] = BitConverter.ToString(b, 0, Math.Min(8, b.Length)).Replace("-", " ")
            };

            switch (b[0])
            {
                case 0xE9:
                {
                    var rel = BitConverter.ToInt32(b, 1);
                    var target = runtimeAddress + 5 + rel;
                    info["kind"] = "jmp rel32";
                    info["target"] = $"0x{target:X}";
                    info["targetStaticVa"] = $"0x{NativeMemory.RuntimeToStaticVa(target):X}";
                    info["targetModule"] = DescribeAddress(target);
                    return info;
                }

                case 0xEB:
                {
                    var rel = (sbyte)b[1];
                    var target = runtimeAddress + 2 + rel;
                    info["kind"] = "jmp rel8";
                    info["target"] = $"0x{target:X}";
                    info["targetStaticVa"] = $"0x{NativeMemory.RuntimeToStaticVa(target):X}";
                    return info;
                }

                case 0xFF when b[1] == 0x25:
                {
                    // jmp qword ptr [rip+disp32] - the Il2CppInterop detour form.
                    var disp = BitConverter.ToInt32(b, 2);
                    var slot = runtimeAddress + 6 + disp;
                    info["kind"] = "jmp qword ptr [rip+disp32]  (ff 25)";
                    info["slot"] = $"0x{slot:X}";
                    info["slotOutsideModuleImage"] = IsOutsideModule(slot);

                    if (NativeMemory.TryReadUInt64(slot, out var ptr))
                    {
                        info["target"] = $"0x{ptr:X}";
                        var target = (long)ptr;
                        info["targetStaticVa"] = $"0x{NativeMemory.RuntimeToStaticVa(target):X}";
                        info["targetModule"] = DescribeAddress(target);
                        info["targetIsInAnonymousCode"] = IsAnonymousExecutable(target);
                    }
                    else
                    {
                        info["target"] = null;
                        info["note"] = "the jump slot itself could not be read";
                    }
                    return info;
                }

                case 0xFF when (b[1] & 0xF8) == 0xE0:
                {
                    info["kind"] = $"jmp r{(b[1] & 7) switch { 0 => "ax", 1 => "cx", 2 => "dx", 3 => "bx", 4 => "sp", 5 => "bp", 6 => "si", _ => "di" }}";
                    info["note"] = "register-indirect jump: the target depends on runtime register state "
                                 + "and cannot be resolved from bytes alone";
                    return info;
                }

                default:
                    info["kind"] = "not a jump";
                    info["note"] = "the first byte is not a jump opcode; call this on an address whose "
                                 + "entry actually begins with e9 / eb / ff 25";
                    return info;
            }
        }

        private static bool IsOutsideModule(long address)
        {
            if (!NativeMemory.TryResolveModule(out var moduleBase, out var moduleSize)) return false;
            return address < moduleBase || address >= moduleBase + moduleSize;
        }

        private static bool IsAnonymousExecutable(long address)
        {
            foreach (var r in NativeMemory.GetExecutableRegions())
            {
                if (address >= r.Start && address < r.End)
                {
                    return r.Path == null || r.Path.Length == 0;
                }
            }
            return false;
        }

        /// <summary>
        /// Names the memory region containing an address, so the caller can tell "in the module
        /// image" from "in the anonymous code mapping" without reimplementing the maps parsing.
        /// </summary>
        internal static string DescribeAddress(long address)
        {
            if (address == 0) return null;

            if (NativeMemory.TryResolveModule(out var moduleBase, out var moduleSize))
            {
                if (address >= moduleBase && address < moduleBase + moduleSize)
                    return "GameAssembly.dll";
            }

            foreach (var r in NativeMemory.GetExecutableRegions())
            {
                if (address >= r.Start && address < r.End)
                {
                    return (r.Path == null || r.Path.Length == 0)
                        ? $"<anonymous r-xp {r.Size / 1024 / 1024} MB>"
                        : r.Path;
                }
            }

            return "<unmapped>";
        }

        /// <summary>
        /// Accepts either a runtime address or a static VA, so callers can paste addresses straight
        /// out of Ghidra/objdump. Static VAs are recognised by being far below the runtime module
        /// base and in the 0x18xxxxxxx range.
        /// </summary>
        internal static long NormalizeAddress(long address, out string how)
        {
            how = "runtime";
            if (address == 0) return 0;

            // Static VAs are imagebase-relative and sit in the 0x180000000 range; runtime addresses
            // under Proton are much higher. Compare against the actual module base rather than
            // guessing from magnitude alone.
            if (NativeMemory.TryResolveModule(out var moduleBase, out _))
            {
                if (address >= 0x180000000L && address < moduleBase && address < 0x1_0000_0000L)
                {
                    var converted = NativeMemory.StaticVaToRuntime(address);
                    if (converted != 0)
                    {
                        how = "static VA -> runtime";
                        return converted;
                    }
                }
            }

            return address;
        }
    }
}
