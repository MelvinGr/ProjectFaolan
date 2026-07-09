using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Faolan.Tests.Support.ProtocolDocs
{
	// Generates docs/protocol/gen/messages.md - the per-message index (PLAN.md §5). For every IIR carrier type it
	// lists the opcode, verification level, the codec that builds it, and - for the V3 ones - the golden test method
	// that pins it byte-exact, emitted as a live [V3: Class.Method] tag the citation linter resolves. So this page is
	// not just a list: each V3 row carries a machine-verified proof-of-layout, and the field-level layout itself lives
	// in the linked codec source (the authoritative spec). Shares CodecIndex with coverage.md so the two never disagree.
	public static class MessagesPage
	{
		public const string RelativePath = "docs/protocol/gen/messages.md";

		public static string Render()
		{
			var idx = CodecIndex.Build();

			// The golden test method for an IIR type: the one belonging to a golden-backed codec that declares it.
			string GoldenMethod(string iir)
			{
				if (!idx.IirToCodecs.TryGetValue(iir, out var codecs))
					return null;
				return codecs.Where(c => idx.CodecGolden.GetValueOrDefault(c))
					.Select(c => idx.CodecGoldenMethod.GetValueOrDefault(c))
					.FirstOrDefault(m => m != null);
			}

			var rows = idx.IirTypes
				.Select(t => (t.Name, t.Value, Level: idx.Level(t.Name), Golden: GoldenMethod(t.Name)))
				.OrderBy(x => CodecIndex.Rank(x.Level))
				.ThenBy(x => x.Name, StringComparer.Ordinal)
				.ToList();

			var v3WithCitation = rows.Count(r => r.Level == "V3" && r.Golden != null);
			var v3Total = rows.Count(r => r.Level == "V3");

			var sb = new StringBuilder();
			sb.AppendLine("# Message index (generated)");
			sb.AppendLine();
			sb.AppendLine("> **GENERATED — do not edit.** One row per IIR carrier type: opcode, verification level, the codec");
			sb.AppendLine("> that builds it, and (V3 only) the golden test that pins it byte-exact — emitted as a live `[V3: …]`");
			sb.AppendLine("> tag the citation linter resolves. The field-level LAYOUT lives in the linked codec source, which is");
			sb.AppendLine("> the authoritative spec; this page is the index into it. Shares `CodecIndex` with [`coverage.md`](coverage.md).");
			sb.AppendLine("> Regenerate: `FAOLAN_UPDATE_DOCS=1 dotnet test Faolan.Tests --filter ProtocolDocs`.");
			sb.AppendLine();
			sb.AppendLine("## How to read a message's layout");
			sb.AppendLine();
			sb.AppendLine("1. Find the type below → note its **codec**. 2. Open that codec in `Faolan.Core.Network/Codecs/` — its");
			sb.AppendLine("   header comment is the byte grammar, and its `Build`/`Parse`/`Read`/`Write` is the executable spec.");
			sb.AppendLine("   3. The **golden** column names the test that proves those bytes match a real capture.");
			sb.AppendLine();
			sb.AppendLine($"**{rows.Count}** IIR carrier types · **{v3Total} V3** ({v3WithCitation} with an auto-cited golden). The");
			sb.AppendLine("carrier framing that wraps every one of these is in [`03-iir-carrier.md`](../03-iir-carrier.md); the");
			sb.AppendLine("encoding rules for the fields inside are in [`04-encoding.md`](../04-encoding.md).");
			sb.AppendLine();
			sb.AppendLine("| Level | IIR type | Opcode | Codec | Golden (proof of layout) |");
			sb.AppendLine("|-------|----------|--------|-------|--------------------------|");
			foreach (var (name, value, level, golden) in rows)
			{
				var codec = idx.IirToCodecs.TryGetValue(name, out var cs) ? string.Join(", ", cs.Select(c => $"`{c}`"))
					: idx.BuiltIn.TryGetValue(name, out var file) ? $"`{file}` *(inline)*"
					: "—";
				var proof = golden != null ? $"[V3: {golden}]" : "";
				sb.AppendLine($"| {level} | `{name}` | `0x{value:X8}` | {codec} | {proof} |");
			}

			sb.AppendLine();
			sb.AppendLine("**V2** rows are built but have no byte-exact golden yet; **V1** rows are genuinely undecoded (see");
			sb.AppendLine("[`gaps.md`](../gaps.md)). A V3 row with an empty golden column is byte-exact via a codec whose test the");
			sb.AppendLine("scan could not name to a single method (rare); its codec still has a golden per `coverage.md`.");
			sb.AppendLine();
			return sb.ToString();
		}
	}
}
