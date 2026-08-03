using System;
using System.Linq;
using System.Text;

namespace Faolan.Tests.Support.ProtocolDocs
{
	// Generates docs/protocol/gen/coverage.md - the answer to "how much of the protocol is actually decoded?".
	// Attribution is EXACT (from CodecIndex, shared with messages.md), read from the codecs themselves rather than
	// guessed from names:
	//   - a codec covers an IIR type  <=>  the codec TYPE declares it via `HandledTypes` (multi-type) or a
	//     `const uint *Opcode` (single-type). That is the #317 convention, enforced by CodecConventionTests, which
	//     also forbids a bare opcode hex literal anywhere in Codecs/ - so nothing can emit an IIR unattributed.
	//   - a codec has a byte-exact test  <=>  a Faolan.Tests file references the codec class AND asserts on bytes
	// Each IIR carrier type then gets a verification level (V3 golden / V2 built-only / V1 no builder). Types built
	// INLINE in the listeners (no codec class) are found by scanning the server tree for the opcode member.
	// The RPC METHOD opcodes are dispatched inline with no explicit opcode->handler map, so their body-decode status
	// is not auto-correlated here (stated as a known limit, not faked).
	public static class CoveragePage
	{
		public const string RelativePath = "docs/protocol/gen/coverage.md";

		public static string Render()
		{
			var idx = CodecIndex.Build();
			var iirToCodecs = idx.IirToCodecs;
			var builtIn = idx.BuiltIn;
			var iirTypes = idx.IirTypes;

			int v3 = 0, v2 = 0, v1 = 0;
			foreach (var (name, _) in iirTypes)
			{
				switch (Level(name))
				{
					case "V3":
						v3++;
						break;
					case "V2":
						v2++;
						break;
					default:
						v1++;
						break;
				}
			}

			var rpcTotal = idx.RpcMethodTotal;
			var dissectors = idx.Dissectors;
			var codecsWithGolden = idx.CodecsWithGolden;
			var codecGoldenCount = idx.CodecGolden.Count;

			var sb = new StringBuilder();
			sb.AppendLine("# Coverage matrix (generated)");
			sb.AppendLine();
			sb.AppendLine("> **GENERATED — do not edit.** Derived from the codecs themselves: a codec covers an IIR type when it");
			sb.AppendLine("> **declares** it (`HandledTypes`, or a `const uint *Opcode` = `(uint)IirTypes.<Type>`);");
			sb.AppendLine("> a codec has a byte-exact test when a `Faolan.Tests` file references the codec class AND asserts on bytes.");
			sb.AppendLine("> Regenerate: `FAOLAN_UPDATE_DOCS=1 dotnet test Faolan.Tests --filter ProtocolDocs`.");
			sb.AppendLine();
			sb.AppendLine("## Headline");
			sb.AppendLine();
			sb.AppendLine($"- **{rpcTotal}** RPC methods — all **V1** (enumerated + client-verified). See [`opcodes.md`](opcodes.md).");
			sb.AppendLine($"- **{iirTypes.Count}** IIR carrier types: **{v3} V3** (byte-exact builder), **{v2} V2** (built, no golden), **{v1} V1** (no builder found).");
			sb.AppendLine($"- **{codecGoldenCount}** codec classes, **{codecsWithGolden}** with a byte-exact test.");
			sb.AppendLine($"- **{dissectors}** Python dissectors (`tools/capture/decode_*.py`).");
			sb.AppendLine();
			sb.AppendLine("### The distinction this table exists to make");
			sb.AppendLine();
			sb.AppendLine("Every opcode is named (V1). That is NOT the same as its message body being decoded. A type is:");
			sb.AppendLine("- **V3** — a builder reproduces captured bytes (a golden test pins it). Provably correct.");
			sb.AppendLine("- **V2** — a codec declares it, or an inline listener builder names its opcode member, but no");
			sb.AppendLine("  byte-exact golden guards it yet.");
			sb.AppendLine("- **V1** — nothing builds or parses it. Genuinely undecoded.");
			sb.AppendLine();
			sb.AppendLine("⚠️ **A V2 is not automatically a to-do.** V3 is defined as *a BUILDER reproducing captured bytes*, so a");
			sb.AppendLine("message we only ever RECEIVE can never reach it — there is nothing to build. The parse-only c2s types");
			sb.AppendLine("sit at V2 permanently and correctly; read the remaining V2 count as \"types with no golden\", not as work");
			sb.AppendLine("outstanding, and check the direction before treating one as a gap.");
			sb.AppendLine();
			sb.AppendLine("V1 now means what it says. A codec cannot emit an IIR without declaring it: `CodecConventionTests`");
			sb.AppendLine("forbids a bare opcode hex literal anywhere in `Codecs/`, so every emitter names its");
			sb.AppendLine("`IirTypes.<Type>` member and is attributed here. (Before #317 the scan fell back on a");
			sb.AppendLine("`<Name>Iir` identifier regex, and ~9 built types showed a false V1.)");
			sb.AppendLine();
			sb.AppendLine("**Limitation (honest):** RPC methods are dispatched inline in the listeners with no explicit");
			sb.AppendLine("opcode→handler map, so their per-body decode status is not auto-correlated here — only their");
			sb.AppendLine("existence (V1). The IIR-type rows below ARE code-correlated. The byte-exact-test signal is a");
			sb.AppendLine("heuristic (codec class referenced by a test that asserts on bytes); it can over- or under-count at");
			sb.AppendLine("the margins, but never invents a golden that isn't there.");
			sb.AppendLine();
			sb.AppendLine("## IIR carrier types");
			sb.AppendLine();
			sb.AppendLine("| Level | IIR type | Built by |");
			sb.AppendLine("|-------|----------|----------|");
			foreach (var (name, _) in iirTypes.OrderBy(x => CodecIndex.Rank(Level(x.Name))).ThenBy(x => x.Name, StringComparer.Ordinal))
			{
				string builtBy;
				if (iirToCodecs.TryGetValue(name, out var cs))
					builtBy = string.Join(", ", cs.Select(c => $"`{c}`"));
				else if (builtIn.TryGetValue(name, out var file))
					builtBy = $"`{file}` *(inline)*";
				else
					builtBy = "—";
				sb.AppendLine($"| {Level(name)} | `{name}` | {builtBy} |");
			}

			sb.AppendLine();
			return sb.ToString();

			string Level(string iir)
			{
				return idx.Level(iir);
			}
		}

		private static int Rank(string level)
		{
			return level switch
			{
				"V3" => 0,
				"V2" => 1,
				_    => 2
			};
		}
	}
}
