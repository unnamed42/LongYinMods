using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MelonMCP.Server;
using Newtonsoft.Json.Linq;

namespace MelonMCP.Tools
{
    /// <summary>
    /// Disassembles memory as it exists RIGHT NOW.
    ///
    /// This is the whole point: on-disk bytes != in-memory bytes. IL2CPP + Il2CppInterop rewrite
    /// native method entries to `ff 25 &lt;disp32&gt;`, with the jump slot living outside the module
    /// image, so reading GameAssembly.dll off disk shows the pristine prologue and tells you nothing
    /// about whether a hook is live.
    /// </summary>
    public class DisasmToolDefinition : ToolDefinitionBase
    {
        public override string Name => "disasm";

        public override string Description => @"Disassemble the RUNNING process's memory (x86-64, Iced).

Accepts either a runtime address or a static VA (0x180xxxxxx, as printed by Ghidra/objdump); a static VA
is converted automatically and the report says so.

Use this instead of objdump/gdb when the question is about runtime bytes, e.g. 'is the entry already
hooked'. Pair with resolve_jump to follow an ff 25 / e9 at the entry.

Note: GameAssembly.dll only maps its header page as a named module. The ~24 MB of real code lives in an
anonymous r-xp mapping, so address-to-region guesses based on module name alone will be wrong; this
tool reports the region it actually read from.";

        protected override ToolInputSchema GetInputSchema()
        {
            return new ToolInputSchema
            {
                Properties = new Dictionary<string, ToolPropertySchema>
                {
                    ["address"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Start address. Hex string, with or without 0x. Runtime address or "
                                    + "static VA (0x180xxxxxx) are both accepted."
                    },
                    ["count"] = new ToolPropertySchema
                    {
                        Type = "integer",
                        Description = "How many instructions to decode (default 20, max 500).",
                        Default = 20,
                        Minimum = 1,
                        Maximum = 500
                    }
                },
                Required = new List<string> { "address" }
            };
        }

        public override CallToolResult Execute(Dictionary<string, JToken> arguments)
        {
            var addressText = GetStringArg(arguments, "address");
            var count = GetIntArg(arguments, "count", 20);

            if (!TryParseAddress(addressText, out var raw))
            {
                return ErrorResult($"Could not parse address '{addressText}'. Use hex, e.g. 0x7f1234567890 or 0x180a8aee0.");
            }

            var address = DisassemblyHelper.NormalizeAddress(raw, out var how);

            var instructions = DisassemblyHelper.Decode(address, count, out var error);
            if (instructions == null)
            {
                return ErrorResult($"Disassembly failed: {error}");
            }
            if (instructions.Count == 0)
            {
                return ErrorResult($"No instructions decoded at 0x{address:X} (unreadable or invalid opcode).");
            }

            return JsonResult(new
            {
                requestedAddress = $"0x{raw:X}",
                resolvedAddress = $"0x{address:X}",
                addressKind = how,
                region = DisassemblyHelper.DescribeAddress(address),
                count = instructions.Count,
                instructions = instructions.Select(i => new
                {
                    address = i.Address,
                    staticVa = i.StaticVa,
                    bytes = i.Bytes,
                    text = i.Text
                })
            });
        }

        internal static bool TryParseAddress(string text, out long value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;

            text = text.Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text.Substring(2);

            return long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }
    }

    /// <summary>
    /// Raw hex + ASCII dump of live memory. The companion to disasm for when the target is data
    /// (a vtable, a descriptor slot, a string) rather than code.
    /// </summary>
    public class ReadMemToolDefinition : ToolDefinitionBase
    {
        public override string Name => "read_mem";

        public override string Description => @"Read raw memory from the running process and show it as hex plus
printable ASCII, 16 bytes per row.

Accepts runtime addresses or static VAs (0x180xxxxxx). Useful for reading pointer tables, type
descriptor slots, and vtables - data rather than code. Use disasm for instructions.";

        protected override ToolInputSchema GetInputSchema()
        {
            return new ToolInputSchema
            {
                Properties = new Dictionary<string, ToolPropertySchema>
                {
                    ["address"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Start address (hex, 0x optional)."
                    },
                    ["length"] = new ToolPropertySchema
                    {
                        Type = "integer",
                        Description = "Bytes to read (default 128, max 4096).",
                        Default = 128,
                        Minimum = 1,
                        Maximum = 4096
                    },
                    ["asPointers"] = new ToolPropertySchema
                    {
                        Type = "boolean",
                        Description = "Also interpret the data as an array of 8-byte pointers, which is how "
                                    + "pointer tables and vtables are usually read."
                    }
                },
                Required = new List<string> { "address" }
            };
        }

        public override CallToolResult Execute(Dictionary<string, JToken> arguments)
        {
            var addressText = GetStringArg(arguments, "address");
            var length = GetIntArg(arguments, "length", 128);
            var asPointers = GetBoolArg(arguments, "asPointers", false);

            if (!DisasmToolDefinition.TryParseAddress(addressText, out var raw))
            {
                return ErrorResult($"Could not parse address '{addressText}'.");
            }

            var address = DisassemblyHelper.NormalizeAddress(raw, out var how);

            if (!NativeMemory.TryReadBytes(address, length, out var data))
            {
                return ErrorResult($"Could not read {length} bytes at 0x{address:X}. The page may be unmapped.");
            }

            var rows = new List<string>();
            for (int offset = 0; offset < data.Length; offset += 16)
            {
                var chunk = data.Skip(offset).Take(16).ToArray();
                var hex = string.Join(" ", chunk.Select(b => b.ToString("X2"))).PadRight(47);
                var ascii = new string(chunk.Select(b => b >= 32 && b < 127 ? (char)b : '.').ToArray());
                rows.Add($"0x{address + offset:X16}  {hex}  |{ascii}|");
            }

            object pointers = null;
            if (asPointers)
            {
                var ptrs = new List<object>();
                for (int offset = 0; offset + 8 <= data.Length; offset += 8)
                {
                    var p = BitConverter.ToUInt64(data, offset);
                    if (p == 0) continue;   // null entries are noise in a pointer table
                    ptrs.Add(new
                    {
                        slot = $"0x{address + offset:X}",
                        value = $"0x{p:X}",
                        region = DisassemblyHelper.DescribeAddress((long)p)
                    });
                }
                pointers = ptrs;
            }

            return JsonResult(new
            {
                requestedAddress = $"0x{raw:X}",
                resolvedAddress = $"0x{address:X}",
                addressKind = how,
                region = DisassemblyHelper.DescribeAddress(address),
                length = data.Length,
                hex = string.Join("\n", rows),
                pointers
            });
        }
    }

    /// <summary>
    /// Follows a jump to its real destination. Written because on IL2CPP the interesting question is
    /// usually "this entry is ff 25 - where does it actually go?", and answering it by hand means
    /// decoding a rip-relative displacement and dereferencing the slot.
    /// </summary>
    public class ResolveJumpToolDefinition : ToolDefinitionBase
    {
        public override string Name => "resolve_jump";

        public override string Description => @"Follow a jump at the given address and report its real target.

Handles the two encodings that matter for IL2CPP work:
- 'ff 25 <disp32>' (jmp qword ptr [rip+disp32]) - Il2CppInterop's detour form. Reads the pointer out of
  the slot and reports where it lands, whether the slot is outside the module image, and whether the
  target is in the anonymous code mapping.
- 'e9 <rel32>' / 'eb <rel8>' - direct relative jumps.

If the first byte is not a jump, it says so rather than guessing.";

        protected override ToolInputSchema GetInputSchema()
        {
            return new ToolInputSchema
            {
                Properties = new Dictionary<string, ToolPropertySchema>
                {
                    ["address"] = new ToolPropertySchema
                    {
                        Type = "string",
                        Description = "Address of the jump instruction (hex, 0x optional)."
                    }
                },
                Required = new List<string> { "address" }
            };
        }

        public override CallToolResult Execute(Dictionary<string, JToken> arguments)
        {
            var addressText = GetStringArg(arguments, "address");

            if (!DisasmToolDefinition.TryParseAddress(addressText, out var raw))
            {
                return ErrorResult($"Could not parse address '{addressText}'.");
            }

            var address = DisassemblyHelper.NormalizeAddress(raw, out var how);
            var result = DisassemblyHelper.ResolveJump(address, out var error);

            if (result == null)
            {
                return ErrorResult($"Could not resolve jump: {error}");
            }

            return JsonResult(new
            {
                addressKind = how,
                region = DisassemblyHelper.DescribeAddress(address),
                result
            });
        }
    }
}
