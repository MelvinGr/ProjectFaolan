using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Faolan.Core.Network.Constants;

namespace Faolan.Tests.Support.ProtocolDocs
{
	// The shared analysis behind gen/coverage.md AND gen/messages.md: which codec declares which IIR type, whether a
	// byte-exact golden guards it, and (for messages.md) the NAME of that golden test method. Both pages read from ONE
	// index so their attribution can never disagree. Everything here is derived from the codecs themselves - the #317
	// Opcode/HandledTypes declarations, enforced by CodecConventionTests - not from name-guessing.
	public sealed class CodecIndex
	{
		private static readonly Regex TestClass = new(@"\bpublic\s+(?:sealed\s+)?(?:partial\s+)?class\s+(\w+)", RegexOptions.Compiled);

		// A [Fact]/[Theory] method: the attribute(s), then a public (async) returntype Name(.
		private static readonly Regex TestMethod = new(@"\[(?:Fact|Theory)\b[^\]]*\]\s*(?:\[[^\]]*\]\s*)*public\s+(?:async\s+)?[\w<>?\[\]]+\s+(\w+)\s*\(", RegexOptions.Compiled);

		// byte-exact test signals: golden loads + hex/byte asserts. "ToHex" covers both Convert.ToHexString and the
		// local ToHex() helpers several test classes define (CharacterStatBlockTests, CombatTests, ...).
		private static readonly string[] ByteExactMarkers = ["Golden.Bytes", "ToHex", "TestPaths.Asset", "SequenceEqual"];
		public Dictionary<string, bool> CodecGolden { get; } = new(StringComparer.Ordinal); // codec -> has a byte-exact test

		public Dictionary<string, HashSet<string>> CodecToIir { get; } = new(StringComparer.Ordinal); // codec class -> IIR type names
		public Dictionary<string, SortedSet<string>> IirToCodecs { get; } = new(StringComparer.Ordinal); // IIR type -> codec classes
		public Dictionary<string, string> BuiltIn { get; } = new(StringComparer.Ordinal); // IIR type -> representative file (inline builders)
		public Dictionary<string, string> CodecGoldenMethod { get; } = new(StringComparer.Ordinal); // codec -> "TestClass.Method" (byte-exact), when found
		public Dictionary<uint, string> NameByValue { get; } = [];
		public HashSet<string> BuiltSomewhere { get; } = new(StringComparer.Ordinal); // IIR types built anywhere
		public int CodecsWithGolden => CodecGolden.Count(kv => kv.Value);
		public int Dissectors { get; private set; }

		public int RpcOpcodeTotal { get; private set; }
		public List<(string Name, uint Value)> IirTypes { get; } = []; // the 32-bit carrier types (value >= 0x10000)

		public string Level(string iir)
		{
			if (IirToCodecs.TryGetValue(iir, out var codecs) && codecs.Any(c => CodecGolden.GetValueOrDefault(c)))
				return "V3";

			return BuiltSomewhere.Contains(iir)
				? "V2"
				: "V1";
		}

		public static CodecIndex Build()
		{
			var repo = TestPaths.Repo();
			var idx = new CodecIndex();

			var enumFields = typeof(GameServer0X2000Opcodes).GetFields(BindingFlags.Public | BindingFlags.Static);
			var allIirNames = enumFields.Select(f => f.Name).Where(n => n.EndsWith("IIR", StringComparison.Ordinal)).ToList();
			foreach (var f in enumFields)
				idx.NameByValue[Convert.ToUInt32(f.GetRawConstantValue())] = f.Name;

			// Codec -> the IIR types it DECLARES. HandledTypes wins (the whole set); else every `const uint *Opcode`.
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
						if (idx.NameByValue.TryGetValue((uint)f.GetRawConstantValue()!, out var n))
							iirs.Add(n);
					}
				}

				if (iirs.Count == 0)
					continue;

				idx.CodecToIir.TryAdd(t.Name, []);
				idx.CodecToIir[t.Name].UnionWith(iirs);
				foreach (var iir in iirs)
				{
					idx.IirToCodecs.TryAdd(iir, new SortedSet<string>(StringComparer.Ordinal));
					idx.IirToCodecs[iir].Add(t.Name);
				}
			}

			// "Built somewhere": types built by server code that has no codec class - the IIR builders inline in the
			// listeners. They name the opcode member directly, so a plain reference scan finds them (V2 vs V1).
			idx.BuiltSomewhere.UnionWith(idx.IirToCodecs.Keys);
			foreach (var dir in new[] { Path.Combine(repo, "Faolan"), Path.Combine(repo, "Faolan.Core.Network") })
			foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
			{
				if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
				    file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}") || file.EndsWith("Opcodes.cs", StringComparison.Ordinal))
					continue;

				var text = File.ReadAllText(file);
				foreach (var iir in allIirNames)
				{
					if (idx.BuiltSomewhere.Contains(iir))
						continue;

					if (text.Contains($"GameServer0X2000Opcodes.{iir}", StringComparison.Ordinal))
					{
						idx.BuiltSomewhere.Add(iir);
						idx.BuiltIn[iir] = Path.GetFileNameWithoutExtension(file);
					}
				}
			}

			// codec -> has a byte-exact test, and the NAME of that test method (for messages.md citations).
			var testFiles = Directory.EnumerateFiles(Path.Combine(repo, "Faolan.Tests"), "*.cs", SearchOption.AllDirectories).Select(f => (Text: File.ReadAllText(f), Path: f)).ToList();
			foreach (var codec in idx.CodecToIir.Keys)
			{
				var re = new Regex(@"\b" + Regex.Escape(codec) + @"\b");
				string method = null;
				foreach (var (text, _) in testFiles)
				{
					// File-level IsByteExact is only a cheap pre-filter; the authoritative signal is a SINGLE method
					// that both references the codec and is byte-exact (FindGoldenMethod). Requiring the method-level
					// match stops a file from crediting a codec when its FromHexString and its .ToArray()/marker live in
					// DIFFERENT, unrelated methods (e.g. InboundParserTests builds test input with .ToArray() in one
					// method and loads a capture with FromHexString in another - neither a byte-exact InventoryMove golden).
					if (!re.IsMatch(text) || !IsByteExact(text, codec))
						continue;

					method = FindGoldenMethod(text, codec);
					if (method != null)
						break;
				}

				idx.CodecGolden[codec] = method != null;
				if (method != null)
					idx.CodecGoldenMethod[codec] = method;
			}

			idx.IirTypes.AddRange(enumFields
				.Select(f => (f.Name, Value: Convert.ToUInt32(f.GetRawConstantValue())))
				.Where(x => x.Value >= 0x10000) // drop the command opcode (see iir-types.md)
				.OrderBy(x => x.Name, StringComparer.Ordinal));

			idx.RpcOpcodeTotal = typeof(GameCharAgentOpcodes)
				.Assembly.GetTypes()
				.Where(t => t.IsEnum && t.Namespace == "Faolan.Core.Network.Constants" && t.Name != "GameServer0X2000Opcodes")
				.Sum(t => t.GetFields(BindingFlags.Public | BindingFlags.Static).Length);
			idx.Dissectors = Directory.EnumerateFiles(Path.Combine(repo, "tools", "capture"), "decode_*.py").Count();

			return idx;
		}

		public static int Rank(string level)
		{
			return level switch
			{
				"V3" => 0,
				"V2" => 1,
				_    => 2
			};
		}

		// Finds a `TestClass.Method` whose body references `codec` AND asserts on bytes - a real [V3: ...] citation.
		private static string FindGoldenMethod(string text, string codec)
		{
			var cls = TestClass.Match(text) is { Success: true } m
				? m.Groups[1].Value
				: null;
			if (cls == null)
				return null;

			var methods = TestMethod.Matches(text).ToList();
			for (var i = 0; i < methods.Count; i++)
			{
				var start = methods[i].Index;
				var end = i + 1 < methods.Count
					? methods[i + 1].Index
					: text.Length;
				var body = text[start..end];
				if (body.Contains(codec, StringComparison.Ordinal) && IsByteExact(body, codec))
					return $"{cls}.{methods[i].Groups[1].Value}";
			}

			return null;
		}

		// A scope (whole test file, or one method body) is byte-exact for `codec` if it carries one of the explicit
		// markers above, OR it loads a captured hex oracle (Convert.FromHexString) and compares it against a serialize
		// output - either the round-trip `.ToArray()` (V1IirCodecTests: `Assert.Equal(body, codec.Parse(body).ToArray())`)
		// or a direct call to THIS codec's Build* (ShopOpenBuilderTests/WorldDespawnTests: `Assert.Equal(captured,
		// codec.BuildX(...))`). Requiring the codec's own Build keeps parse-only codecs (InventoryMoveCodec, which has
		// no serialize path) from being falsely credited by an input-hex parser test. These idioms are real byte-exact
		// goldens the explicit markers missed - why a batch of genuinely-tested IIR types read as V2.
		private static bool IsByteExact(string scope, string codec)
		{
			return ByteExactMarkers.Any(scope.Contains) || (scope.Contains("FromHexString") && (scope.Contains(".ToArray(") || scope.Contains($"{codec}.Build")));
		}
	}
}
