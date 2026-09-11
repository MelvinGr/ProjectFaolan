using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Faolan.Core.Enums;
using Faolan.Core.Network.Protocol;

namespace Faolan.Tests.Support.ProtocolDocs
{
	// The shared analysis behind gen/coverage.md and gen/messages.md: which codec declares which IIR type, whether a
	// byte-exact golden guards it, and (for messages.md) the name of that golden test method. Both pages read from one
	// index so their attribution can never disagree. Everything here is derived from the codecs themselves - their
	// Opcode/HandledTypes declarations, enforced by CodecConventionTests - not from name-guessing.
	public sealed class CodecIndex
	{
		private static readonly Regex TestClass = new Regex(@"\bpublic\s+(?:sealed\s+)?(?:partial\s+)?class\s+(\w+)", RegexOptions.Compiled);

		// A [Fact]/[Theory] method: the attribute(s), then a public (async) returntype Name(.
		private static readonly Regex TestMethod = new Regex(@"\[(?:Fact|Theory)\b[^\]]*\]\s*(?:\[[^\]]*\]\s*)*public\s+(?:async\s+)?[\w<>?\[\]]+\s+(\w+)\s*\(", RegexOptions.Compiled);

		// byte-exact test signals: golden loads + hex/byte asserts. "ToHex" covers both Convert.ToHexString and the
		// local ToHex() helpers several test classes define (CharacterStatBlockTests, CombatTests, ...).
		private static readonly string[] ByteExactMarkers = ["Golden.Bytes", "ToHex", "TestPaths.Asset", "SequenceEqual"];

		public Dictionary<string, HashSet<string>> CodecToIir { get; } = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal); // codec class -> IIR type names
		public Dictionary<string, SortedSet<string>> IirToCodecs { get; } = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal); // IIR type -> codec classes
		public Dictionary<string, bool> CodecGolden { get; } = new Dictionary<string, bool>(StringComparer.Ordinal); // codec -> has a byte-exact test
		public Dictionary<string, string> BuiltIn { get; } = new Dictionary<string, string>(StringComparer.Ordinal); // IIR type -> representative file (inline builders)
		public Dictionary<string, string> CodecGoldenMethod { get; } = new Dictionary<string, string>(StringComparer.Ordinal); // codec -> "TestClass.Method" (byte-exact), when found
		public Dictionary<uint, string> NameByValue { get; } = [];
		public HashSet<string> BuiltSomewhere { get; } = new HashSet<string>(StringComparer.Ordinal); // IIR types built anywhere
		public List<(string Name, uint Value)> IirTypes { get; } = []; // the 32-bit carrier types (value >= 0x10000)
		public int CodecsWithGolden => CodecGolden.Count(kv => kv.Value);
		public int Dissectors { get; private set; }

		public int RpcMethodTotal { get; private set; }

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

			var enumFields = typeof(IirTypes).GetFields(BindingFlags.Public | BindingFlags.Static);
			var allIirNames = enumFields.Select(f => f.Name).Where(n => n.EndsWith("IIR", StringComparison.Ordinal)).ToList();
			foreach (var f in enumFields)
				idx.NameByValue[Convert.ToUInt32(f.GetRawConstantValue())] = f.Name;

			// Codec -> the IIR types it declares. HandledTypes wins (the whole set); else every `const uint *Opcode`.
			// Anchor the scan on a Faolan.Core.Network type (Rpc), not IirTypes: the opcode enums moved to
			// Faolan.Core, so typeof(IirTypes).Assembly is Faolan.Core.dll while the codecs live in
			// Faolan.Core.Network.dll - scanning the wrong assembly finds zero codecs (every IIR falls back to "inline").
			foreach (var t in typeof(Rpc).Assembly.GetTypes().Where(t => t.Namespace != null && t.Namespace.StartsWith("Faolan.Core.Network.Codecs", StringComparison.Ordinal)))
			{
				var fields = t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
				var iirs = new HashSet<string>(StringComparer.Ordinal);

				var handled = fields.FirstOrDefault(f => f.Name == "HandledTypes" && f.FieldType == typeof(IirTypes[]));
				if (handled != null)
				{
					foreach (var v in (IirTypes[])handled.GetValue(null)!)
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
			foreach (var file in SourceFiles(dir, "*.cs"))
			{
				if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
				    file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}") || file.EndsWith("Opcodes.cs", StringComparison.Ordinal))
					continue;

				var text = File.ReadAllText(file);
				foreach (var iir in allIirNames)
				{
					if (idx.BuiltSomewhere.Contains(iir))
						continue;

					if (text.Contains($"IirTypes.{iir}", StringComparison.Ordinal))
					{
						idx.BuiltSomewhere.Add(iir);
						idx.BuiltIn[iir] = Path.GetFileNameWithoutExtension(file);
					}
				}
			}

			// codec -> has a byte-exact test, and the name of that test method (for messages.md citations).
			var testFiles = SourceFiles(Path.Combine(repo, "Faolan.Tests"), "*.cs").Select(f => (Text: File.ReadAllText(f), Path: f)).ToList();
			foreach (var codec in idx.CodecToIir.Keys)
			{
				var re = new Regex(@"\b" + Regex.Escape(codec) + @"\b");
				string? method = null;

				// Most specific proof first - the test class named after the codec beats an unrelated suite that merely
				// touches it - then the sorted order as a stable tie-break (OrderBy is stable), so the citation is
				// picked by name, never by which file the filesystem happened to hand back first.
				foreach (var (text, _) in testFiles.OrderBy(f => Affinity(Path.GetFileNameWithoutExtension(f.Path), codec)))
				{
					// File-level IsByteExact is only a cheap pre-filter; the authoritative signal is a single method
					// that both references the codec and is byte-exact (FindGoldenMethod). Requiring the method-level
					// match stops a file from crediting a codec when its FromHexString and its .ToArray()/marker live in
					// different, unrelated methods (e.g. InboundParserTests builds test input with .ToArray() in one
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

			// The RPC method catalogs: every `*Methods` enum, plus the two `ChatServer*Opcodes` (chat carries no
			// RpcHeader, so its numbers really are opcodes). IirTypes is counted above. Counting `*Opcodes` alone would
			// quietly drop the total to 18.
			idx.RpcMethodTotal = typeof(GameCharAgentMethods)
				.Assembly.GetTypes()
				.Where(t => t is { IsEnum: true, Namespace: "Faolan.Core.Enums" } &&
				            (t.Name.EndsWith("Methods", StringComparison.Ordinal) || t.Name.StartsWith("ChatServer", StringComparison.Ordinal)))
				.Sum(t => t.GetFields(BindingFlags.Public | BindingFlags.Static).Length);
			idx.Dissectors = SourceFiles(Path.Combine(repo, "tools", "capture"), "decode_*.py", SearchOption.TopDirectoryOnly).Count;

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

		// How closely a test file's name matches the codec it might cite: 0 = named after it exactly
		// (FooCodec -> FooCodecTests), 1 = named after its subject (FooCodec -> FooTests), 2 = mentions the subject,
		// 3 = unrelated.
		private static int Affinity(string testFile, string codec)
		{
			var subject = codec;
			foreach (var suffix in new[] { "Codec", "Builder", "Decoder", "Body" })
			{
				if (subject.EndsWith(suffix, StringComparison.Ordinal))
					subject = subject[..^suffix.Length];
			}

			if (testFile == codec + "Tests")
				return 0;

			if (testFile == subject + "Tests")
				return 1;

			return testFile.Contains(subject, StringComparison.Ordinal)
				? 2
				: 3;
		}

		// Finds a `TestClass.Method` whose body references `codec` and asserts on bytes - a real [V3: ...] citation.
		private static string? FindGoldenMethod(string text, string codec)
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
					? StartOfLeadingTrivia(text, methods[i + 1].Index)
					: text.Length;
				var body = text[start..end];
				if (body.Contains(codec, StringComparison.Ordinal) && IsByteExact(body, codec))
					return $"{cls}.{methods[i].Groups[1].Value}";
			}

			return null;
		}

		// Walks back from a method declaration over the blank lines, `//` comments and `[Attribute]` lines directly
		// above it - that trivia belongs to the method below, not to the one before it.
		//
		// Without this, a method's slice ran to the next method's declaration, so the next method's doc comment was
		// attributed to the previous method's body. A comment is enough to satisfy `body.Contains(codec)`, so merely
		// naming a codec in prose above a test could credit it with the byte-exact golden of the test above - which is
		// exactly what happened: a comment about `DeleteItemCodec`/`MoveInventoryItemCodec` promoted both from V2 to V3
		// on the strength of `BuildParseRoundTripsOverTheCorpus`. Both are parse-only c2s codecs with no serialize path
		// at all, which `IsByteExact` goes out of its way to protect against, so the claim was doubly wrong. A
		// generated page states what is proven; a mention is not a proof.
		private static int StartOfLeadingTrivia(string text, int declaration)
		{
			var at = declaration;
			while (at > 0)
			{
				var lineStart = text.LastIndexOf('\n', Math.Max(0, at - 2)) + 1;
				var line = text[lineStart..at].Trim();
				if (line.Length != 0 && !line.StartsWith("//", StringComparison.Ordinal) && !(line.StartsWith('[') && line.EndsWith(']')))
					break;

				at = lineStart;
			}

			return at;
		}

		// A scope (whole test file, or one method body) is byte-exact for `codec` if it carries one of the explicit
		// markers above, or it loads a captured hex oracle (Convert.FromHexString) and compares it against a serialize
		// output - either the round-trip `.ToArray()` (V1IirCodecTests: `Assert.Equal(body, codec.Parse(body).ToArray())`)
		// or a direct call to this codec's Build* (ShopOpenReplyTests/N3ToClientQuitCodecTests: `Assert.Equal(captured,
		// codec.BuildX(...))`). Requiring the codec's own Build keeps parse-only codecs (MoveInventoryItemCodec, which has
		// no serialize path) from being falsely credited by an input-hex parser test. These idioms are real byte-exact
		// goldens the explicit markers miss, and without them a batch of genuinely-tested IIR types reads as V2.
		private static bool IsByteExact(string scope, string codec)
		{
			return ByteExactMarkers.Any(scope.Contains) || (scope.Contains("FromHexString") && (scope.Contains(".ToArray(") || scope.Contains($"{codec}.Build")));
		}

		// Every source file under `dir`, in a deterministic order. Directory.EnumerateFiles returns filesystem order,
		// which changes when a file is rewritten (an editor or `sed -i` replaces the inode) - and several of the picks
		// below are "first match wins", so an unordered scan silently rewrites generated pages: a codec with two equally
		// valid golden tests would flip citation between machines and between commits. Sorted, the page is a function of
		// the sources alone.
		private static List<string> SourceFiles(string dir, string pattern, SearchOption option = SearchOption.AllDirectories)
		{
			return Directory.EnumerateFiles(dir, pattern, option).OrderBy(f => f, StringComparer.Ordinal).ToList();
		}
	}
}
