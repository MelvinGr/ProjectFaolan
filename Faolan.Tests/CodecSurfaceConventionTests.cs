using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs;
using Faolan.Core.Network.Constants;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// The UNIFORM CODEC SURFACE gates (Gitea #530). CodecConventionTests next door pins the opcode/literal rules;
	// this file pins the SHAPE: every codec is `byte[] Build(...)` + `T Parse(in ReadOnlySpan<byte>)` and nothing
	// else public, reading sequentially through SpanReader to the end of the body.
	//
	// ⚠️ Most tests here are still SKIPPED, deliberately, and each Skip names the wave that un-skips it. The conversion
	// runs folder by folder; a gate that went live in wave 0 would be red for the whole sweep and would just get muted.
	// The bodies are written so that un-skipping is a one-line change and so the rule has an executable definition
	// rather than only prose. Removing a Skip is part of landing that wave - not a separate cleanup.
	//
	// The LIVE gates assert over `MigratedFolders` only, which is what lets them go live a wave at a time: the gate
	// tightens as the sweep advances instead of waiting for the end, when nothing would be left to catch. Wave 9 drops
	// the filter.
	//
	// Wave order (see #530): 1 Inventory/Feats/Team/Guild · 2 Text/Dialogue/Economy · 3 World · 4 Combat ·
	// 5 Spawn/Quest · 6 FullCharSnapshot (+#447) · 7 Faolan/ moves in · 8 Faolan.Importer/ stops hand-decoding ·
	// 9 delete ICodec, un-skip everything.
	public class CodecSurfaceConventionTests
	{
		private const string CodecsNamespace = "Faolan.Core.Network.Codecs";

		// The folders converted to the uniform surface so far. ADD A FOLDER AS ITS WAVE LANDS - that is what makes the
		// gates below binding on it, and leaving it out is how a converted folder silently stops being checked.
		private static readonly string[] MigratedFolders =
			[RootFolder, "Inventory", "Feats", "Team", "Guild", "Text", "Dialogue", "World", "Combat", "Quest", "Spawn", "FullCharSnapshot", "PlayerAgent", "Economy"];

		// The ROOT of Codecs/ itself - the codecs that sit in `Faolan.Core.Network.Codecs` with no domain folder.
		// Spelled as a sentinel rather than "" so the two places that expand it read as deliberate.
		private const string RootFolder = ".";

		private static readonly Regex BlockComment = new(@"/\*.*?\*/", RegexOptions.Singleline);
		private static readonly Regex ConstOffset = new(@"\bconst\s+int\s+\w*(Offset|Pos)\b");

		// One `case GameServer0X2000Opcodes.X:` of the listener's routing switch - §6's dispatch exemption, keyed on
		// the label rather than on a file name (see IirOpcodesAreOnlyNamedInsideCodecs).
		private static readonly Regex DispatchCaseLabel = new(@"^\s*case\s+GameServer0X2000Opcodes\.\w+\s*:");
		private static readonly Regex LineComment = new(@"//.*$", RegexOptions.Multiline);
		private static readonly Regex StringLiteral = new("\"(?:[^\"\\\\]|\\\\.)*\"");

		// A `new FaolanStream()` that is not introduced by `using` - the leak rule 6 exists for. The lookbehind is
		// VARIABLE-LENGTH (a .NET regex extension) because the declaration between `using` and the `new` is:
		// `using var s = `, `using (var s = `, `using FaolanStream s = ` - a fixed-width lookbehind matched none of
		// them and flagged every correct call site.
		private static readonly Regex UndisposedStream = new(@"(?<!\busing\b[^;]{0,60})new FaolanStream\(");

		// Anchored on Endpoints (Faolan.Core.Network/Constants) rather than on a codec: codec names are being
		// rewritten by this very refactor, and ListFrame - the obvious anchor - lives in Faolan.Core now.
		private static IEnumerable<Type> CodecTypes => typeof(Endpoints).Assembly.GetTypes()
			.Where(t => t.Namespace != null && MigratedFolders.Any(f => t.Namespace == NamespaceOf(f)));

		private static string NamespaceOf(string folder)
		{
			return folder == RootFolder ? CodecsNamespace : $"{CodecsNamespace}.{folder}";
		}

		// A codec MODULE: the static classes that carry Build/Parse. Value records are not modules.
		private static IEnumerable<Type> CodecModules => CodecTypes
			.Where(t => t is { IsAbstract: true, IsSealed: true }) // C# static class
			.Where(t => Methods(t).Any(m => m.Name is "Build" or "Parse"));

		// ---- rule 1: the public surface -------------------------------------------------------------------

		[Fact]
		public void PublicSurfaceIsBuildAndParseOnly()
		{
			var offenders = (from t in CodecModules
				from m in Methods(t)
				where m.Name is not ("Build" or "Parse") && !m.IsSpecialName
				select $"{t.Name}.{m.Name}").ToList();

			Assert.True(offenders.Count == 0,
				"a codec exposes only Build and Parse (composition goes through `internal Read(ref SpanReader)` / `internal Write(FaolanStream, T)`):\n  " +
				string.Join("\n  ", offenders));
		}

		// `string` is legal alongside `byte[]` because of rule 1a: a handful of codecs carry the remoteformat TEXT
		// that sits INSIDE a byte codec, and forcing byte[] on them would be a worse shape, not a better one. Widened,
		// not exempted - everything else in rules 1-7 still applies to them.
		[Fact]
		public void BuildReturnsBytes()
		{
			var offenders = (from t in CodecModules
				from m in Methods(t).Where(m => m.Name == "Build")
				where m.ReturnType != typeof(byte[]) && m.ReturnType != typeof(string)
				select $"{t.Name}.Build -> {m.ReturnType.Name}").ToList();

			Assert.True(offenders.Count == 0, "Build must return byte[] (or string, for a rule-1a text codec):\n  " + string.Join("\n  ", offenders));
		}

		[Fact]
		public void ParseTakesASpanAndReturnsAValue()
		{
			var offenders = new List<string>();
			foreach (var t in CodecModules)
			{
				var parse = Methods(t).SingleOrDefault(m => m.Name == "Parse");
				if (parse == null)
					continue;

				// Rule 1a: a TEXT codec takes the string instead. One shape or the other, never a mix - that is
				// ParseIsNotOverloaded's job, so all this has to decide is whether the single Parse is legal.
				var ps = parse.GetParameters();
				var span = ps.Length == 1 && ps[0].ParameterType == typeof(ReadOnlySpan<byte>).MakeByRefType() && ps[0].IsIn;
				var text = ps.Length == 1 && ps[0].ParameterType == typeof(string);
				if (!span && !text)
					offenders.Add($"{t.Name}.Parse must take a single `in ReadOnlySpan<byte>` (or a `string`, for a rule-1a text codec)");
				if (parse.ReturnType == typeof(void) || parse.ReturnType == typeof(bool))
					offenders.Add($"{t.Name}.Parse must return the decoded value, not {parse.ReturnType.Name}");
				if (ps.Any(p => p.IsOut))
					offenders.Add($"{t.Name}.Parse must not have out parameters");
			}

			Assert.True(offenders.Count == 0, "Parse signature:\n  " + string.Join("\n  ", offenders));
		}

		[Fact]
		public void ParseIsNotOverloaded()
		{
			var offenders = CodecModules
				.Where(t => Methods(t).Count(m => m.Name == "Parse") > 1)
				.Select(t => t.Name)
				.ToList();

			Assert.True(offenders.Count == 0,
				"exactly one Parse per codec - overloaded readers are how `which one decodes this body?` ambiguity gets in:\n  " + string.Join("\n  ", offenders));
		}

		[Fact]
		public void EveryCodecHasABuildTakingItsParseResult()
		{
			var offenders = new List<string>();
			foreach (var t in CodecModules)
			{
				var parse = Methods(t).SingleOrDefault(m => m.Name == "Parse");
				if (parse == null)
					continue;

				var builds = Methods(t).Where(m => m.Name == "Build").ToList();
				if (builds.Count == 0)
					continue; // parse-only codecs (inbound messages we never emit) are legitimate

				if (!builds.Any(b => b.GetParameters() is [{ } only] && only.ParameterType == parse.ReturnType))
					offenders.Add($"{t.Name}: Parse returns {parse.ReturnType.Name} but no Build overload takes it");
			}

			Assert.True(offenders.Count == 0,
				"Build(T) is canonical - without it Build/Parse are two unrelated methods sharing a file, and there is no round-trip:\n  " +
				string.Join("\n  ", offenders));
		}

		// The delegation constraint: overloads are free BECAUSE only one of them encodes. A non-canonical Build must
		// delegate, not be a second encoder that can drift from the first.
		//
		// Which overload is canonical comes from REFLECTION, not from the parameter's spelling: it is the one whose
		// single parameter is exactly what Parse returns. An earlier draft matched the type name against `*IIR`, which
		// silently exempted every codec whose value is an RPC message or a sub-record rather than an IIR - i.e. more
		// than half of them.
		[Fact]
		public void OnlyTheCanonicalBuildWritesBytes()
		{
			var canonical = CodecModules
				.Select(t => (t.Name, Parse: Methods(t).SingleOrDefault(m => m.Name == "Parse")))
				.Where(x => x.Parse != null)
				.ToDictionary(x => x.Name, x => x.Parse!.ReturnType.Name, StringComparer.Ordinal);

			var offenders = new List<string>();
			foreach (var file in CodecSourceFiles())
			{
				var src = Strip(File.ReadAllText(file));
				var valueType = canonical.GetValueOrDefault(Path.GetFileNameWithoutExtension(file));

				foreach (Match m in Regex.Matches(src, @"public static byte\[\] Build\s*\(([^)]*)\)\s*(=>|\{)", RegexOptions.Singleline))
				{
					var parameters = m.Groups[1].Value;
					if (valueType != null && parameters.Split(',').Length == 1 && Regex.IsMatch(parameters, $@"\b{Regex.Escape(valueType)}\b"))
						continue; // the canonical overload - the one method allowed to write bytes

					var body = m.Groups[2].Value == "=>" ? ExpressionBodyOf(src, m.Index + m.Length) : BodyOf(src, m.Index + m.Length - 1);
					if (body.Contains("new FaolanStream", StringComparison.Ordinal) || Regex.IsMatch(body, @"\.Write[A-Z]\w*\("))
						offenders.Add($"{Path.GetFileName(file)}: Build({parameters.Trim()}) encodes instead of delegating to Build({valueType ?? "T"})");
				}
			}

			Assert.True(offenders.Count == 0, "only Build(T) may write bytes; other overloads delegate:\n  " + string.Join("\n  ", offenders));
		}

		// ---- rule 3: sequential reading -------------------------------------------------------------------

		// ⚠️ Scoped to PARSE paths, which is what rule 3 is actually about (#530 §7): a const offset means the fields
		// between 0 and it were never DECODED, so the codec cannot tell a correct body from one that merely happens to
		// have the right byte there. A file with no `Parse` reads nothing - its offsets are PATCH offsets into a
		// record whose head is fixed-width (SpawnTransformPatch stamps the live position/token at send time) or into a
		// captured template (SpawnPacketBuilder's splice engine), and both disappear with the thing they patch rather
		// than with a rewrite of the reader. Scoping is the agreed answer here, not an allowlist: an allowlist would
		// exempt a FILE, and the moment one of these grew a Parse it would keep its exemption.
		[Fact]
		public void NoConstOffsetsInCodecs()
		{
			var offenders = (from file in CodecSourceFiles()
				let source = File.ReadAllText(file)
				where source.Contains("Parse(in ReadOnlySpan<byte>", StringComparison.Ordinal)
				let lines = File.ReadAllLines(file)
				from i in Enumerable.Range(0, lines.Length)
				let code = Strip(lines[i])
				where ConstOffset.IsMatch(code)
				select $"{Path.GetFileName(file)}:{i + 1}  {code.Trim()}").ToList();

			Assert.True(offenders.Count == 0,
				"read sequentially through SpanReader - a const offset means the bytes before it were never decoded:\n  " + string.Join("\n  ", offenders));
		}

		// ---- rule 6: stream disposal ----------------------------------------------------------------------

		// Scoped to the migrated codec folders for now. It WIDENS to all of Faolan.Core.Network + Faolan/ +
		// Faolan.Importer/ in wave 8, once those assemblies stop hand-rolling wire code - a resource rule has to cover
		// everywhere the stream is constructed, but it cannot go live over code the sweep has not reached.
		[Fact]
		public void FaolanStreamsAreDisposed()
		{
			var offenders = (from file in CodecSourceFiles()
				let lines = File.ReadAllLines(file)
				from i in Enumerable.Range(0, lines.Length)
				let code = Strip(lines[i])
				where UndisposedStream.IsMatch(code)
				select $"{Path.GetFileName(file)}:{i + 1}  {code.Trim()}").ToList();

			Assert.True(offenders.Count == 0,
				"FaolanStream : MemoryStream is IDisposable - `using var s = new FaolanStream();`, never `return new FaolanStream()...ToArray();`:\n  " +
				string.Join("\n  ", offenders));
		}

		// ---- rule 5: identity and naming -------------------------------------------------------------------

		[Fact]
		public void SingleOpcodeCodecIsNamedForItsIir()
		{
			var offenders = new List<string>();
			foreach (var (type, iir) in SingleIirCodecs())
			{
				var expected = Regex.Replace(iir.ToString(), "(IIR|Iir)$", "") + "Codec";
				if (type.Name != expected)
					offenders.Add($"{type.Name} emits {iir} so it must be named {expected}");
			}

			Assert.True(offenders.Count == 0, "a codec is named for the IIR it emits:\n  " + string.Join("\n  ", offenders));
		}

		// The MERGE gate. Scoped, so it only sees duplicates INSIDE the migrated set until wave 9 drops the filter -
		// the 11 cross-folder duplicate emitters (#530 §3) collapse in waves 3-4.
		[Fact]
		public void EachIirHasExactlyOneCodec()
		{
			var byOpcode = new Dictionary<GameServer0X2000Opcodes, List<string>>();
			foreach (var t in CodecTypes)
			foreach (var op in DeclaredOpcodes(t))
				(byOpcode.TryGetValue(op, out var l) ? l : byOpcode[op] = []).Add(t.Name);

			var dupes = byOpcode.Where(kv => kv.Value.Count > 1).Select(kv => $"{kv.Key}: {string.Join(", ", kv.Value)}").ToList();

			Assert.True(dupes.Count == 0, "one IIR, one codec - two codecs declaring the same opcode ARE the same codec and must be merged:\n  " + string.Join("\n  ", dupes));
		}

		[Fact]
		public void ParseResultIsNamedForItsIir()
		{
			var offenders = new List<string>();
			foreach (var (type, iir) in SingleIirCodecs())
			{
				var parse = Methods(type).SingleOrDefault(m => m.Name == "Parse");
				if (parse == null)
					continue;

				if (parse.ReturnType.Name != iir.ToString())
					offenders.Add($"{type.Name}.Parse returns {parse.ReturnType.Name}, expected {iir} (the client's name, verbatim)");
			}

			Assert.True(offenders.Count == 0, "the wire value is named for the client IIR:\n  " + string.Join("\n  ", offenders));
		}

		// #539: a codec that produces a PAYLOAD its parent should compose reads as standalone whenever the SERVICE does
		// the composing instead. Five failed when this was first un-skipped; four are fixed - the three FEEDBACK
		// payloads (`FormatFeedbackCodec.Build(instance, FeedbackPayload)` takes the value and the payload owns its own
		// typeFlags/footer) and `TradepostMailBodyCodec` (the AH-buy body is `MailColumns.ItemBoughtBody`, because the
		// listener has no business knowing the mail template exists). `NpcChatterCodec` cleared with the ChatServer
		// bodies: the world model caches the bark VALUE and `CharacterChatCodec` renders it.
		//
		// ONE is left, and it is a buff-PIPELINE change rather than a codec one: `MagicBuffDataCodec` is composed by
		// `Faolan/` because `NpcSpellCast` stores its payload as BYTES on the world model, so the broadcaster builds
		// the 54 bytes at spawn time. Giving that record a typed `MagicBuffData` is the same fix the chatter barks just
		// got, and it reaches `ActiveBuff` / `WorldSimulation` / `MountCatalog` / the `NpcSpell` columns. Wave 4 also
		// keeps `MagicBuffIIR.Data` opaque deliberately, so the parent needs a typed Build OVERLOAD rather than a
		// retyped value.
		//
		// Two blind spots WERE fixed while proving the list: the field scan missed `HandledOpcodes` (so the two Team
		// codecs that do declare their identity were flagged), and "standalone" ignored §6's own definition, which is
		// "nothing else composes it" - that alone cleared `RemoteFormatScreenCodec`.
		[Fact(Skip = "un-skip when #539's last offender lands - MagicBuffDataCodec needs NpcSpellCast to carry the VALUE instead of pre-built bytes")]
		public void RpcMessageCodecsDeclareTheirMethodOpcode()
		{
			// A codec with a standalone body but no IIR opcode is an RPC-message codec: it must declare its method
			// off its own interface enum, so the identity is recorded somewhere rather than nowhere.
			var offenders = CodecModules
				.Where(t => !DeclaredOpcodes(t).Any())
				.Where(t => Fields(t).All(f => !DeclaresAnOpcode(f)))
				.Where(IsStandaloneMessage)
				.Select(t => t.Name)
				.ToList();

			Assert.True(offenders.Count == 0,
				"an RPC-message codec declares `public const ushort Opcode = (ushort)<Interface>Opcodes.<Method>` and is named for that method:\n  " +
				string.Join("\n  ", offenders));
		}

		// Keeps the (a)/(b)/(c) taxonomy from rotting: a codec that declares no opcode must be COMPOSED by another
		// codec. A "sub-record" nobody composes is really a standalone message with its identity recorded nowhere.
		[Fact(Skip = "un-skip in wave 9")]
		public void EveryCodecIsClassified()
		{
			var sources = CodecSourceFiles().ToDictionary(f => f, File.ReadAllText);
			var offenders = new List<string>();

			foreach (var t in CodecModules.Where(t => !DeclaredOpcodes(t).Any() && Fields(t).All(f => !f.Name.EndsWith("Opcode", StringComparison.Ordinal))))
			{
				var composed = sources.Any(kv => !kv.Key.EndsWith($"{t.Name}.cs", StringComparison.Ordinal) && kv.Value.Contains($"{t.Name}.", StringComparison.Ordinal));
				if (!composed)
					offenders.Add($"{t.Name} declares no opcode but nothing composes it - it is a standalone message with no declared identity");
			}

			Assert.True(offenders.Count == 0, "every codec is (a) IIR, (b) RPC-message, or (c) a sub-record something composes:\n  " + string.Join("\n  ", offenders));
		}

		// ---- rule 7: encode/decode lives in Codecs/ --------------------------------------------------------

		// LIVE since #542, which was the last blocker: `CreateFlowService`'s burst helper took `(opcode, tail)` rather
		// than a whole record, so every one of its 34 steps spelled an IIR. It sends records now, and the helper whose
		// only job was slicing the sub-op back off for it (`CreateEditorRecordBuilder`) is gone.
		//
		// §6 exempts the listener's opcode DISPATCH switch. The exemption keys on the `case <opcode>:` LABEL, not on
		// the file name: nothing in `Faolan/` is NAMED "Dispatch" - the switch lives in `GameServerListener.Inbound.cs`
		// - so the old filename filter exempted nothing and would have counted its 22 routing labels as decode (#542).
		// A `case` label routes a body to a codec without reading a byte of it, wherever it lives.
		[Fact]
		public void IirOpcodesAreOnlyNamedInsideCodecs()
		{
			string[] roots = [TestPaths.Repo("Faolan"), TestPaths.Repo("Faolan.Importer")];
			var offenders = (from file in SourceFiles(roots)
				let lines = File.ReadAllLines(file)
				from i in Enumerable.Range(0, lines.Length)
				let code = Strip(lines[i])
				where code.Contains("GameServer0X2000Opcodes.", StringComparison.Ordinal) && !DispatchCaseLabel.IsMatch(code)
				select $"{Path.GetFileName(file)}:{i + 1}  {code.Trim()}").ToList();

			Assert.True(offenders.Count == 0,
				"naming an IIR outside Codecs/ means this file is encoding or decoding - that belongs in the codec:\n  " + string.Join("\n  ", offenders));
		}

		// ---- the payoff -----------------------------------------------------------------------------------

		// Reflection over every codec with a standalone body, against real captured bytes: Parse -> Build must
		// reproduce the input EXACTLY, and Parse(Build(v)) must equal v. Subsumes rule 3 - a codec that skips a
		// region cannot reproduce it on the way out, so a partial decode fails here even if EnsureAtEnd() passed.
		// Sub-record codecs are covered transitively through the parent that composes them.
		[Fact(Skip = "un-skip per wave; live from wave 9")]
		public void BuildParseRoundTripsOverTheCorpus()
		{
			var failures = new List<string>();
			foreach (var t in CodecModules)
			{
				var parse = Methods(t).SingleOrDefault(m => m.Name == "Parse");
				var build = Methods(t).FirstOrDefault(m => m.Name == "Build" && m.GetParameters() is [{ } p] && p.ParameterType == parse?.ReturnType);
				if (parse == null || build == null)
					continue;

				foreach (var op in DeclaredOpcodes(t))
				foreach (var entry in CorpusExtract.PacketsOfType((uint)op).Take(50))
				{
					object? value;
					try
					{
						value = parse.Invoke(null, [entry.Full]);
					}
					catch (TargetInvocationException e)
					{
						failures.Add($"{t.Name} failed to parse a captured {op} from {entry.Source}: {e.InnerException?.Message}");
						continue;
					}

					var rebuilt = (byte[])build.Invoke(null, [value])!;
					if (!rebuilt.AsSpan().SequenceEqual(entry.Full))
						failures.Add($"{t.Name} did not reproduce a captured {op} from {entry.Source} ({entry.Full.Length} -> {rebuilt.Length} bytes)");
				}
			}

			Assert.True(failures.Count == 0, "Parse -> Build must reproduce the captured bytes exactly:\n  " + string.Join("\n  ", failures.Take(25)));
		}

		// LIVE from wave 7b: Economy held the last two implementations (`TradepostListingCodec.ParsedResults` and
		// `ShopContentCodec.ShopContent`) and `PlayfieldProxy` the third, deferred by wave 3 because Economy consumed
		// its stream `Read`. The interface FILE goes in wave 9; nothing implements it from here.
		[Fact]
		public void NoIcodecImplementations()
		{
			var offenders = CodecTypes
				.Where(t => t.GetInterfaces().Any(i => i.IsGenericType && i.Name.StartsWith("ICodec", StringComparison.Ordinal)))
				.Select(t => t.FullName!)
				.ToList();

			Assert.True(offenders.Count == 0,
				"ICodec<T> is retired - a wire value is a plain record; the codec module owns Build/Parse:\n  " + string.Join("\n  ", offenders));
		}

		// ---- helpers ---------------------------------------------------------------------------------------

		private static string BodyOf(string src, int braceIndex)
		{
			var depth = 0;
			for (var i = braceIndex; i < src.Length; i++)
			{
				if (src[i] == '{')
					depth++;
				else if (src[i] == '}' && --depth == 0)
					return src[braceIndex..i];
			}

			return src[braceIndex..];
		}

		// An expression-bodied member's body: from `=>` to its terminating `;`. Taking a fixed window instead reads
		// into the NEXT member, which is how a `=> Build(...)` delegation gets flagged for the `s.WriteUInt32` in the
		// method below it.
		private static string ExpressionBodyOf(string src, int arrowEnd)
		{
			var end = src.IndexOf(';', arrowEnd);
			return end < 0 ? src[arrowEnd..] : src[arrowEnd..end];
		}

		// The root's files are its TOP LEVEL only - recursing would pull in every folder, migrated or not.
		private static IEnumerable<string> CodecSourceFiles()
		{
			var root = TestPaths.Repo("Faolan.Core.Network", "Codecs");
			var folders = SourceFiles(MigratedFolders.Where(f => f != RootFolder).Select(f => Path.Combine(root, f)));

			return MigratedFolders.Contains(RootFolder)
				? folders.Concat(Directory.EnumerateFiles(root, "*.cs", SearchOption.TopDirectoryOnly))
				: folders;
		}

		private static IEnumerable<GameServer0X2000Opcodes> DeclaredOpcodes(Type t)
		{
			foreach (var f in Fields(t))
			{
				if (f.IsLiteral && f.FieldType == typeof(uint) && f.Name == "Opcode")
					yield return (GameServer0X2000Opcodes)(uint)f.GetRawConstantValue()!;
				else if (f.Name == "HandledTypes" && f.FieldType == typeof(GameServer0X2000Opcodes[]))
					foreach (var v in (GameServer0X2000Opcodes[])f.GetValue(null)!)
						yield return v;
			}
		}

		private static IEnumerable<FieldInfo> Fields(Type t)
		{
			return t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
		}

		// A field that records the codec's message identity. `Opcode` is the single-method form and `HandledOpcodes`
		// the RPC twin of `HandledTypes` (one layout, the verb in the opcode) - the suffix check used to test only for
		// "Opcode", so a codec declaring the PLURAL was read as declaring nothing at all.
		private static bool DeclaresAnOpcode(FieldInfo f)
		{
			return f.Name.EndsWith("Opcode", StringComparison.Ordinal) || f.Name.EndsWith("Opcodes", StringComparison.Ordinal);
		}

		// A codec whose Build produces a whole message rather than a fragment: the server layer sends it AND no other
		// codec composes it. The second half is §6's own definition of a sub-record ("a codec that declares no opcode
		// must be composed by another codec in the tree") and it is what `EveryCodecIsClassified` asks; without it a
		// sub-record whose parent lives in `Codecs/` still read as standalone the moment a service touched it too.
		private static bool IsStandaloneMessage(Type t)
		{
			var server = SourceFiles([TestPaths.Repo("Faolan")]).Any(f => File.ReadAllText(f).Contains($"{t.Name}.Build", StringComparison.Ordinal));

			return server && !IsComposedByAnotherCodec(t);
		}

		// True when some OTHER codec source file mentions this codec - i.e. composes it into a larger body.
		private static bool IsComposedByAnotherCodec(Type t)
		{
			return CodecSourceFiles()
				.Where(f => !f.EndsWith($"{t.Name}.cs", StringComparison.Ordinal))
				.Any(f => File.ReadAllText(f).Contains($"{t.Name}.", StringComparison.Ordinal));
		}

		private static IEnumerable<MethodInfo> Methods(Type t)
		{
			return t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
		}

		private static IEnumerable<(Type Type, GameServer0X2000Opcodes Iir)> SingleIirCodecs()
		{
			foreach (var t in CodecTypes)
			{
				var declared = DeclaredOpcodes(t).ToList();
				if (declared.Count == 1)
					yield return (t, declared[0]);
			}
		}

		private static IEnumerable<string> SourceFiles(IEnumerable<string> roots)
		{
			return roots.Where(Directory.Exists)
				.SelectMany(r => Directory.EnumerateFiles(r, "*.cs", SearchOption.AllDirectories))
				.Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
				            !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
				            !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"));
		}

		private static string Strip(string code)
		{
			return StringLiteral.Replace(LineComment.Replace(BlockComment.Replace(code, ""), ""), "");
		}
	}
}
