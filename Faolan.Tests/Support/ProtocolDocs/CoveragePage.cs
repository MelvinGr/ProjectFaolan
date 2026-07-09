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
	// Generates docs/protocol/gen/coverage.md - the answer to "how much of the protocol is actually decoded?".
	// Attribution is EXACT, read from the codecs themselves rather than guessed from names:
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

		// byte-exact test signals: golden loads + hex/byte asserts. "ToHex" covers both Convert.ToHexString and the
		// local ToHex() helpers several test classes define (CharacterStatBlockTests, WorldDespawnTests, ...).
		private static readonly string[] ByteExactMarkers = { "Golden.Bytes", "ToHex", "TestPaths.Asset", "SequenceEqual" };

		public static string Render()
		{
			var repo = TestPaths.Repo();
			var testDir = Path.Combine(repo, "Faolan.Tests");

			var allIirNames = typeof(GameServer0X2000Opcodes).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => f.Name).Where(n => n.EndsWith("IIR", StringComparison.Ordinal)).ToList();
			var nameByValue = typeof(GameServer0X2000Opcodes).GetFields(BindingFlags.Public | BindingFlags.Static).ToDictionary(f => Convert.ToUInt32(f.GetRawConstantValue()), f => f.Name);

			// Codec -> the IIR types it DECLARES. `HandledTypes` is authoritative when present (it lists the whole
			// set); otherwise every `const uint *Opcode` on the type. No regex, no name-guessing.
			var codecToIir = new Dictionary<string, HashSet<string>>(); // codec class -> IIR type names
			var iirToCodecs = new Dictionary<string, SortedSet<string>>(); // IIR type -> codec classes
			foreach (var t in typeof(GameServer0X2000Opcodes).Assembly.GetTypes().Where(t => t.Namespace != null && t.Namespace.StartsWith("Faolan.Core.Network.Codecs", StringComparison.Ordinal)))
			{
				var fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
				var iirs = new HashSet<string>(StringComparer.Ordinal);

				var handled = fields.FirstOrDefault(f => f.Name == "HandledTypes" && f.FieldType == typeof(GameServer0X2000Opcodes[]));
				if (handled != null)
				{
					foreach (var v in (GameServer0X2000Opcodes[])handled.GetValue(null)!)
						iirs.Add(v.ToString());
				}
				else
				{
					foreach (var f in fields.Where(f => f.IsLiteral && f.FieldType == typeof(uint) && f.Name.EndsWith("Opcode", StringComparison.Ordinal)))
					{
						if (nameByValue.TryGetValue((uint)f.GetRawConstantValue()!, out var n))
							iirs.Add(n);
					}
				}

				if (iirs.Count == 0)
					continue;

				codecToIir.TryAdd(t.Name, new HashSet<string>());
				codecToIir[t.Name].UnionWith(iirs);
				foreach (var iir in iirs)
				{
					iirToCodecs.TryAdd(iir, new SortedSet<string>(StringComparer.Ordinal));
					iirToCodecs[iir].Add(t.Name);
				}
			}

			// "Built somewhere": a type constructed by server code that has no codec class - the IIR builders that live
			// inline in the listeners (GameServerListener.*, BankService, ...). Those name the opcode member directly,
			// so a plain reference scan finds them. This is what separates V2 (built, no golden) from V1 (nothing builds it).
			var builtSomewhere = new HashSet<string>(iirToCodecs.Keys);
			var builtIn = new Dictionary<string, string>(); // type -> a representative file (basename) for the column
			foreach (var dir in new[] { Path.Combine(repo, "Faolan"), Path.Combine(repo, "Faolan.Core.Network") })
			foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
			{
				if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
				    file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}") || file.EndsWith("Opcodes.cs", StringComparison.Ordinal)) // the enum DEFINITION, not a use
					continue;

				var text = File.ReadAllText(file);
				foreach (var iir in allIirNames)
				{
					if (builtSomewhere.Contains(iir))
						continue;

					if (text.Contains($"GameServer0X2000Opcodes.{iir}", StringComparison.Ordinal))
					{
						builtSomewhere.Add(iir);
						builtIn[iir] = Path.GetFileNameWithoutExtension(file);
					}
				}
			}

			// codec class -> has a byte-exact test.
			var testTexts = Directory.EnumerateFiles(testDir, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText).ToList();

			bool HasByteExactTest(string codec)
			{
				var re = new Regex(@"\b" + Regex.Escape(codec) + @"\b");
				return testTexts.Any(t => re.IsMatch(t) && ByteExactMarkers.Any(t.Contains));
			}

			var codecGolden = codecToIir.Keys.ToDictionary(c => c, HasByteExactTest);

			// Level per IIR type.
			var iirTypes = typeof(GameServer0X2000Opcodes)
				.GetFields(BindingFlags.Public | BindingFlags.Static)
				.Select(f => (f.Name, Value: Convert.ToUInt32(f.GetRawConstantValue())))
				.Where(x => x.Value >= 0x10000) // drop the command opcode (see iir-types.md)
				.OrderBy(x => x.Name, StringComparer.Ordinal)
				.ToList();

			string Level(string iir)
			{
				if (iirToCodecs.TryGetValue(iir, out var codecs) && codecs.Any(c => codecGolden.GetValueOrDefault(c)))
					return "V3";

				return builtSomewhere.Contains(iir)
					? "V2"
					: "V1";
			}

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

			var rpcTotal = typeof(GameCharAgentOpcodes)
				.Assembly.GetTypes()
				.Where(t => t.IsEnum && t.Namespace == "Faolan.Core.Network.Constants" && t.Name != "GameServer0X2000Opcodes")
				.Sum(t => t.GetFields(BindingFlags.Public | BindingFlags.Static).Length);
			var dissectors = Directory.EnumerateFiles(Path.Combine(repo, "Captures", "scripts"), "decode_*.py").Count();
			var codecsWithGolden = codecGolden.Count(kv => kv.Value);

			var sb = new StringBuilder();
			sb.AppendLine("# Coverage matrix (generated)");
			sb.AppendLine();
			sb.AppendLine("> **GENERATED — do not edit.** Derived from the codecs themselves: a codec covers an IIR type when it");
			sb.AppendLine("> **declares** it (`HandledTypes`, or a `const uint *Opcode` = `(uint)GameServer0X2000Opcodes.<Type>`);");
			sb.AppendLine("> a codec has a byte-exact test when a `Faolan.Tests` file references the codec class AND asserts on bytes.");
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
			sb.AppendLine("- **V2** — a codec declares it, or an inline listener builder names its opcode member, but no");
			sb.AppendLine("  byte-exact golden guards it yet.");
			sb.AppendLine("- **V1** — nothing builds or parses it. Genuinely undecoded.");
			sb.AppendLine();
			sb.AppendLine("V1 now means what it says. A codec cannot emit an IIR without declaring it: `CodecConventionTests`");
			sb.AppendLine("forbids a bare opcode hex literal anywhere in `Codecs/`, so every emitter names its");
			sb.AppendLine("`GameServer0X2000Opcodes.<Type>` member and is attributed here. (Before #317 the scan fell back on a");
			sb.AppendLine("`<Name>Iir` identifier regex, and ~9 built types showed a false V1.)");
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
