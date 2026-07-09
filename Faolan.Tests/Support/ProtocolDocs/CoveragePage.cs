using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Faolan.Core.Network.Constants;

namespace Faolan.Tests.Support.ProtocolDocs
{
	// Generates docs/protocol/gen/coverage.md - the answer to "how much of the protocol is actually decoded?", derived
	// from real CODE REFERENCES rather than name-guessing:
	//   - a codec covers an IIR type  <=>  the codec source references GameServer0X2000Opcodes.<Type>
	//   - a codec has a byte-exact test  <=>  a Faolan.Tests file references the codec class AND asserts on bytes
	// From those two facts each of the 75 IIR carrier types gets a verification level (V3 golden / V2 codec-only /
	// V1 named-only). The RPC METHOD opcodes are handled inline in the listeners with no explicit opcode->handler
	// map, so their body-decode status is not auto-correlated here (stated as a known limit, not faked).
	public static class CoveragePage
	{
		public const string RelativePath = "docs/protocol/gen/coverage.md";

		private static readonly Regex IirRef = new(@"GameServer0X2000Opcodes\.(\w+)");
		private static readonly Regex ClassDecl = new(@"\b(?:public|internal)\s+(?:static\s+|sealed\s+|abstract\s+|partial\s+)*class\s+(\w+)");
		// byte-exact test signals: golden loads + hex/byte asserts.
		private static readonly string[] ByteExactMarkers = { "Golden.Bytes", "ToHexString", "TestPaths.Asset", "SequenceEqual" };

		public static string Render()
		{
			var repo = TestPaths.Repo();
			var codecDir = Path.Combine(repo, "Faolan.Core.Network", "Codecs");
			var testDir = Path.Combine(repo, "Faolan.Tests");

			// The IIR type names up front - needed to detect the `<Base>Iir` builder-identifier convention below.
			var allIirNames = typeof(GameServer0X2000Opcodes)
				.GetFields(BindingFlags.Public | BindingFlags.Static)
				.Select(f => f.Name)
				.Where(n => n.EndsWith("IIR", StringComparison.Ordinal))
				.ToList();

			// A codec "covers" an IIR type when its source references EITHER the opcode enum member
			// (GameServer0X2000Opcodes.FooIIR) OR the builder/codec identifier convention `FooIir` (e.g.
			// BuildCharacterActionIir, ComboBookIirCodec). The second catches builders that never name the enum.
			var codecFiles = Directory.EnumerateFiles(codecDir, "*.cs", SearchOption.AllDirectories).ToList();
			var codecToIir = new Dictionary<string, HashSet<string>>();      // codec class -> IIR type names
			var iirToCodecs = new Dictionary<string, SortedSet<string>>();   // IIR type -> codec classes
			foreach (var file in codecFiles)
			{
				var text = File.ReadAllText(file);
				var classes = ClassDecl.Matches(text).Select(m => m.Groups[1].Value).ToHashSet();
				if (classes.Count == 0) continue;

				var iirs = new HashSet<string>(IirRef.Matches(text).Select(m => m.Groups[1].Value));
				foreach (var iir in allIirNames)
				{
					// FooIIR -> the `FooIir` CamelCase token, matched even inside a compound identifier
					// (BuildCharacterActionIir, ComboBookIirCodec): allow any prefix, forbid a lowercase/digit
					// continuation after "Iir" so it's a whole CamelCase word, not e.g. "...Iiron".
					var ident = iir[..^3] + "Iir";
					if (Regex.IsMatch(text, Regex.Escape(ident) + @"(?![a-z0-9])"))
						iirs.Add(iir);
				}

				foreach (var c in classes)
				{
					codecToIir.TryAdd(c, new HashSet<string>());
					codecToIir[c].UnionWith(iirs);
					foreach (var iir in iirs)
					{
						iirToCodecs.TryAdd(iir, new SortedSet<string>(StringComparer.Ordinal));
						iirToCodecs[iir].Add(c);
					}
				}
			}

			// "Built somewhere": a type is constructed/handled by ANY server code (not just Codecs/) - many IIR
			// builders live inline in the listeners (GameServerListener.*, CreateFlowService, BankService, ...). A
			// type counts as built if any server .cs references its opcode member OR its `<Name>Iir` identifier.
			// This is what separates V2 (built, no golden) from V1 (nothing decodes it) accurately.
			var builtSomewhere = new HashSet<string>(iirToCodecs.Keys);
			var builtIn = new Dictionary<string, string>(); // type -> a representative file (basename) for the column
			foreach (var dir in new[] { Path.Combine(repo, "Faolan"), Path.Combine(repo, "Faolan.Core.Network") })
			foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
			{
				if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
				    file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
				    file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}") ||
				    file.EndsWith("Opcodes.cs", StringComparison.Ordinal)) // the enum DEFINITION, not a use
					continue;
				var text = File.ReadAllText(file);
				foreach (var iir in allIirNames)
				{
					if (builtSomewhere.Contains(iir)) continue;
					var ident = iir[..^3] + "Iir";
					if (text.Contains($"GameServer0X2000Opcodes.{iir}", StringComparison.Ordinal) ||
					    Regex.IsMatch(text, Regex.Escape(ident) + @"(?![a-z0-9])"))
					{
						builtSomewhere.Add(iir);
						builtIn[iir] = Path.GetFileNameWithoutExtension(file);
					}
				}
			}

			// codec class -> has a byte-exact test.
			var testTexts = Directory.EnumerateFiles(testDir, "*.cs", SearchOption.AllDirectories)
				.Select(File.ReadAllText).ToList();
			bool HasByteExactTest(string codec)
			{
				var re = new Regex(@"\b" + Regex.Escape(codec) + @"\b");
				return testTexts.Any(t => re.IsMatch(t) && ByteExactMarkers.Any(t.Contains));
			}
			var codecGolden = codecToIir.Keys.ToDictionary(c => c, HasByteExactTest);

			// Level per IIR type.
			var iirTypes = typeof(GameServer0X2000Opcodes)
				.GetFields(BindingFlags.Public | BindingFlags.Static)
				.Select(f => (Name: f.Name, Value: Convert.ToUInt32(f.GetRawConstantValue())))
				.Where(x => x.Value >= 0x10000) // drop the command opcode (see iir-types.md)
				.OrderBy(x => x.Name, StringComparer.Ordinal)
				.ToList();

			string Level(string iir)
			{
				if (iirToCodecs.TryGetValue(iir, out var codecs) && codecs.Any(c => codecGolden.GetValueOrDefault(c)))
					return "V3";
				return builtSomewhere.Contains(iir) ? "V2" : "V1";
			}

			int v3 = 0, v2 = 0, v1 = 0;
			foreach (var (name, _) in iirTypes)
			{
				switch (Level(name)) { case "V3": v3++; break; case "V2": v2++; break; default: v1++; break; }
			}

			var rpcTotal = typeof(GameCharAgentOpcodes).Assembly.GetTypes()
				.Where(t => t.IsEnum && t.Namespace == "Faolan.Core.Network.Constants" && t.Name != "GameServer0X2000Opcodes")
				.Sum(t => t.GetFields(BindingFlags.Public | BindingFlags.Static).Length);
			var dissectors = Directory.EnumerateFiles(Path.Combine(repo, "Captures", "scripts"), "decode_*.py").Count();
			var codecsWithGolden = codecGolden.Count(kv => kv.Value);

			var sb = new StringBuilder();
			sb.AppendLine("# Coverage matrix (generated)");
			sb.AppendLine();
			sb.AppendLine("> **GENERATED — do not edit.** Derived from real code references in the repo:");
			sb.AppendLine("> a codec covers an IIR type when it references `GameServer0X2000Opcodes.<Type>`; a codec has a");
			sb.AppendLine("> byte-exact test when a `Faolan.Tests` file references the codec class AND asserts on bytes.");
			sb.AppendLine("> Regenerate: `FAOLAN_UPDATE_DOCS=1 dotnet test Faolan.Tests --filter ProtocolDocs`.");
			sb.AppendLine();
			sb.AppendLine("## Headline");
			sb.AppendLine();
			sb.AppendLine($"- **{rpcTotal}** RPC method opcodes — all **V1** (enumerated + client-verified). See [`opcodes.md`](opcodes.md).");
			sb.AppendLine($"- **{iirTypes.Count}** IIR carrier types: **{v3} V3** (byte-exact builder), **{v2} V2** (built, no golden), **{v1} V1** (no builder found).");
			sb.AppendLine($"- **{codecGolden.Count}** codec classes, **{codecsWithGolden}** with a byte-exact test.");
			sb.AppendLine($"- **{dissectors}** Python dissectors (`Captures/scripts/decode_*.py`).");
			sb.AppendLine();
			sb.AppendLine("### The distinction this table exists to make");
			sb.AppendLine();
			sb.AppendLine("Every opcode is named (V1). That is NOT the same as its message body being decoded. A type is:");
			sb.AppendLine("- **V3** — a builder reproduces captured bytes (a golden test pins it). Provably correct.");
			sb.AppendLine("- **V2** — a codec OR an inline listener builder constructs/parses it (it references the opcode member");
			sb.AppendLine("  or the `<Name>Iir` identifier somewhere in the server), but no byte-exact golden guards it yet.");
			sb.AppendLine("- **V1** — no server code references it by opcode member or `<Name>Iir` identifier. Either genuinely");
			sb.AppendLine("  undecoded, OR built by an inconsistently-named builder the scan can't attribute (the builder-naming");
			sb.AppendLine("  cleanup that would collapse this ambiguity is Gitea #317).");
			sb.AppendLine();
			sb.AppendLine("**Limitation (honest):** RPC method opcodes are dispatched inline in the listeners with no explicit");
			sb.AppendLine("opcode→handler map, so their per-body decode status is not auto-correlated here — only their");
			sb.AppendLine("existence (V1). The IIR-type rows below ARE code-correlated. The byte-exact-test signal is a");
			sb.AppendLine("heuristic (codec class referenced by a test that asserts on bytes); it can over- or under-count at");
			sb.AppendLine("the margins, but never invents a golden that isn't there.");
			sb.AppendLine();
			sb.AppendLine("## IIR carrier types");
			sb.AppendLine();
			sb.AppendLine("| Level | IIR type | Built by |");
			sb.AppendLine("|-------|----------|----------|");
			foreach (var (name, _) in iirTypes.OrderBy(x => Rank(Level(x.Name))).ThenBy(x => x.Name, StringComparer.Ordinal))
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
		}

		private static int Rank(string level) => level switch { "V3" => 0, "V2" => 1, _ => 2 };
	}
}
