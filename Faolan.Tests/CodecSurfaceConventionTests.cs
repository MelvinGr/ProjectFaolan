using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// The uniform codec surface gates. CodecConventionTests next door pins the opcode/literal rules; this file pins
	// the shape: every codec is `byte[] Build(...)` + `T Parse(in ReadOnlySpan<byte>)` and nothing else public,
	// reading sequentially through SpanReader to the end of the body.
	//
	// Every gate here is live over every folder under `Codecs/`, and `MigratedFolders` is read off disk, so a new
	// folder is covered the moment it exists.
	//
	// Both allowlists are empty, and neither is a place to silence a gate: an entry has to name the decode issue
	// that removes it again.
	public class CodecSurfaceConventionTests
	{
		private const string CodecsNamespace = "Faolan.Core.Network.Codecs";

		// Every folder under Codecs/, enumerated from disk rather than hand-maintained, so a new folder is covered the
		// moment it exists.
		private static readonly string[] MigratedFolders =
		[
			RootFolder,
			.. Directory.EnumerateDirectories(TestPaths.Repo("Faolan.Core.Network", "Codecs")).Select(d => Path.GetFileName(d)!).Order(StringComparer.Ordinal)
		];

		// A genuine sub-record whose parent does not compose it yet - not a place to silence the gate. Each entry would
		// name the decode issue that removes it; the list is empty.
		private static readonly string[] UncomposedSubRecords = [];

		// Codecs the corpus round-trip cannot yet cover, each with the decode issue that removes it. The list is empty:
		// adding an entry back means shipping a codec that cannot read retail, so do it only with a filed decode issue,
		// never to silence a failure. Entries are per codec, not per body, so an entry hides the codec's passing bodies
		// too.
		private static readonly string[] UnreadableInTheCorpus = [];

		// The root of Codecs/ itself - the codecs that sit in `Faolan.Core.Network.Codecs` with no domain folder.
		// Spelled as a sentinel rather than "" so the two places that expand it read as deliberate.
		private const string RootFolder = ".";

		private static readonly ConcurrentDictionary<MethodInfo, Func<byte[], object?>> Parsers = new ConcurrentDictionary<MethodInfo, Func<byte[], object?>>();

		private static readonly Regex BlockComment = new Regex(@"/\*.*?\*/", RegexOptions.Singleline);
		private static readonly Regex ConstOffset = new Regex(@"\bconst\s+int\s+\w*(Offset|Pos)\b");

		// One `case IirTypes.X:` of the listener's routing switch - the dispatch exemption, keyed on the label rather
		// than on a file name (see IirOpcodesAreOnlyNamedInsideCodecs).
		private static readonly Regex DispatchCaseLabel = new Regex(@"^\s*case\s+IirTypes\.\w+\s*:");
		private static readonly Regex LineComment = new Regex(@"//.*$", RegexOptions.Multiline);
		private static readonly Regex StringLiteral = new Regex("\"(?:[^\"\\\\]|\\\\.)*\"");

		// A `new FaolanStream()` that is not introduced by `using` - the leak the disposal rule exists for. The
		// lookbehind is variable-length (a .Net regex extension) because the declaration between `using` and the `new`
		// varies: `using var s = `, `using (var s = `, `using FaolanStream s = `. A fixed-width lookbehind matches none
		// of them and flags every correct call site.
		private static readonly Regex UndisposedStream = new Regex(@"(?<!\busing\b[^;]{0,60})new FaolanStream\(");

		// Anchored on Endpoints (Faolan.Core.Network/Constants) rather than on a codec: ListFrame - the obvious anchor
		// - lives in Faolan.Core.
		private static IEnumerable<Type> CodecTypes => typeof(Endpoints).Assembly.GetTypes()
			.Where(t => t.Namespace != null && MigratedFolders.Any(f => t.Namespace == NamespaceOf(f)));

		private static string NamespaceOf(string folder)
		{
			return folder == RootFolder ? CodecsNamespace : $"{CodecsNamespace}.{folder}";
		}

		// A codec module: the static classes that carry Build/Parse. Value records are not modules.
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

		// `string` is legal alongside `byte[]`: a handful of codecs carry the remoteformat text that sits inside a byte
		// codec, and forcing byte[] on them would be a worse shape, not a better one. Widened, not exempted - every
		// other rule still applies to them.
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

				// A text codec takes the string instead. One shape or the other, never a mix - that is
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

		// The delegation constraint: overloads are free because only one of them encodes. A non-canonical Build must
		// delegate, not be a second encoder that can drift from the first.
		//
		// Which overload is canonical comes from reflection, not from the parameter's spelling: it is the one whose
		// single parameter is exactly what Parse returns. Matching the type name against `*IIR` instead would silently
		// exempt every codec whose value is an RPC message or a sub-record rather than an IIR - more than half of them.
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

		// Scoped to Parse paths, which is what the sequential-reading rule is about: a const offset means the fields
		// between 0 and it were never decoded, so the codec cannot tell a correct body from one that merely happens
		// to have the right byte there. A file with no `Parse` reads nothing - its offsets are patch offsets into a
		// record whose head is fixed-width (SpawnTransformPatch stamps the live position/token at send time) or into a
		// captured template (SpawnPacketBuilder's splice engine), and both disappear with the thing they patch rather
		// than with a rewrite of the reader. Scoping is the answer here rather than an allowlist: an allowlist would
		// exempt a file, and the moment one of these grew a Parse it would keep its exemption.
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

		// A resource rule has to cover everywhere the stream is constructed, so this reads every source file under
		// Codecs/.
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

		// The merge gate: two codecs declaring the same opcode are the same codec and must be merged.
		[Fact]
		public void EachIirHasExactlyOneCodec()
		{
			var byOpcode = new Dictionary<IirTypes, List<string>>();
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

		// A codec that produces a payload its parent should compose reads as standalone whenever the service does the
		// composing instead, so the codec that owns the message has to compose the payload - not a listener or a
		// service. The feedback payloads are the pattern: `FormatFeedbackCodec.Build(instance, FeedbackPayload)` takes
		// the value and the payload owns its own typeFlags/footer, and the AH-buy body is `MailColumns.ItemBoughtBody`
		// because the listener has no business knowing the mail template exists.
		//
		// Two things the scan must get right: it has to include `HandledOpcodes` as well as `Opcode`, or a codec
		// declaring its identity in the plural is flagged; and "standalone" means "nothing else composes it".
		[Fact]
		public void RpcMessageCodecsDeclareTheirMethodOpcode()
		{
			// A codec with a standalone body but no IIR opcode is an RPC-message codec: it must declare its method
			// off its own interface enum, so the identity is recorded somewhere rather than nowhere.
			var offenders = CodecModules
				.Where(t => !DeclaredOpcodes(t).Any())
				.Where(t => Fields(t).All(f => !DeclaresItsMessageId(f)))
				.Where(IsStandaloneMessage)
				.Select(t => t.Name)
				.ToList();

			Assert.True(offenders.Count == 0,
				"an RPC-message codec declares `public const ushort Opcode = (ushort)<Interface>Opcodes.<Method>` - or, on the hash-registered PlayerAgent/PlayerInterface pair, `public const uint Method = (uint)<Interface>Methods.<Method>` - and is named for that method:\n  " +
				string.Join("\n  ", offenders));
		}

		// Keeps the (a)/(b)/(c) taxonomy from rotting: a codec that declares no opcode must be composed by another
		// codec. A "sub-record" nobody composes is really a standalone message with its identity recorded nowhere.
		[Fact]
		public void EveryCodecIsClassified()
		{
			var sources = CodecSourceFiles().ToDictionary(f => f, File.ReadAllText);
			var offenders = new List<string>();

			// Via DeclaresAnOpcode, so `HandledOpcodes` (the RPC twin of `HandledTypes`) counts: a codec declaring the
			// plural must not read as declaring nothing at all.
			foreach (var t in CodecModules.Where(t => !DeclaredOpcodes(t).Any() && Fields(t).All(f => !DeclaresItsMessageId(f))))
			{
				var composed = sources.Any(kv => !kv.Key.EndsWith($"{t.Name}.cs", StringComparison.Ordinal) && kv.Value.Contains($"{t.Name}.", StringComparison.Ordinal));
				if (!composed && !UncomposedSubRecords.Contains(t.Name))
					offenders.Add($"{t.Name} declares no opcode but nothing composes it - it is a standalone message with no declared identity");
			}

			Assert.True(offenders.Count == 0, "every codec is (a) IIR, (b) RPC-message, or (c) a sub-record something composes:\n  " + string.Join("\n  ", offenders));
		}

		// ---- rule 7: encode/decode lives in Codecs/ --------------------------------------------------------

		// The listener's opcode dispatch switch is exempt, and the exemption keys on the `case <opcode>:` label rather
		// than on a file name: nothing in `Faolan/` is named "Dispatch" - the switch lives in
		// `GameServerListener.Inbound.cs` - so a filename filter would exempt nothing and count its routing labels as
		// decode. A `case` label routes a body to a codec without reading a byte of it, wherever it lives.
		[Fact]
		public void IirOpcodesAreOnlyNamedInsideCodecs()
		{
			string[] roots = [TestPaths.Repo("Faolan"), TestPaths.Repo("Faolan.Importer")];
			var offenders = (from file in SourceFiles(roots)
				let lines = File.ReadAllLines(file)
				from i in Enumerable.Range(0, lines.Length)
				let code = Strip(lines[i])
				where code.Contains("IirTypes.", StringComparison.Ordinal) && !DispatchCaseLabel.IsMatch(code)
				select $"{Path.GetFileName(file)}:{i + 1}  {code.Trim()}").ToList();

			Assert.True(offenders.Count == 0,
				"naming an IIR outside Codecs/ means this file is encoding or decoding - that belongs in the codec:\n  " + string.Join("\n  ", offenders));
		}

		// ---- the payoff -----------------------------------------------------------------------------------

		// Reflection over every codec with a standalone body, against real captured bytes: Parse -> Build must
		// reproduce the input exactly, and Parse(Build(v)) must equal v. Subsumes the sequential-reading rule - a codec
		// that skips a region cannot reproduce it on the way out, so a partial decode fails here even if EnsureAtEnd()
		// passed. Sub-record codecs are covered transitively through the parent that composes them.
		//
		// It parses the record, `entry.Body[4..]`, not `entry.Full`. `Body` is `[u32 carrier length][record]` and the
		// length belongs to the framing sender rather than to any `Build`, so handing the gate the framed form would
		// fail every codec for the framing rather than for its grammar.
		//
		// Codecs with their own dedicated corpus gate (`SimpleCharFullUpdateCorpusTests`, `MoveTaskCodecCorpusTests`,
		// `SpawnPreIirCorpusTests`) are not excluded: those assert over all captured bodies and report a census, this
		// samples 50 per type and asserts nothing else. Overlap is cheap; a codec silently losing its only coverage is
		// not.
		[Fact]
		public void BuildParseRoundTripsOverTheCorpus()
		{
			var failures = new List<string>();
			var covered = 0;
			foreach (var t in CodecModules)
			{
				var parse = Methods(t).SingleOrDefault(m => m.Name == "Parse");
				var build = Methods(t).FirstOrDefault(m => m.Name == "Build" && m.GetParameters() is [{ } p] && p.ParameterType == parse?.ReturnType);
				if (parse == null || build == null || UnreadableInTheCorpus.Contains(t.Name))
					continue;

				foreach (var op in DeclaredOpcodes(t))
				foreach (var entry in CorpusExtract.PacketsOfType((uint)op).Take(50))
				{
					var record = entry.Body[4..]; // the u32 carrier length is the sender's
					object? value;
					try
					{
						value = ParserFor(parse)(record);
					}
					// The emitted call is a direct call, so the codec's own exception arrives unwrapped - there is no
					// TargetInvocationException to unpack, unlike a MethodInfo.Invoke.
					catch (CodecParseException e)
					{
						failures.Add($"{t.Name} failed to parse a captured {op} from {entry.Source}: {e.Message}");
						continue;
					}

					covered++;
					var rebuilt = (byte[])build.Invoke(null, [value])!;
					if (!rebuilt.AsSpan().SequenceEqual(record))
						failures.Add($"{t.Name} did not reproduce a captured {op} from {entry.Source} ({record.Length} -> {rebuilt.Length} bytes)");
				}
			}

			Assert.True(failures.Count == 0,
				$"Parse -> Build must reproduce the captured bytes exactly ({failures.Count} failures; " +
				string.Join(", ", failures.GroupBy(f => f.Split(' ')[0]).Select(g => $"{g.Key} x{g.Count()}")) + "):\n  " + string.Join("\n  ", failures.Take(25)));

			// A gate that reflects its way to zero work passes silently, which is the one failure mode it cannot
			// report itself. The extract carries 9 IIR types; this is the floor, not a count to keep in step.
			Assert.True(covered > 200, $"the corpus round-trip only exercised {covered} bodies - it is reflecting its way to nothing");
		}

		// ---- rule 17: where a body starts ----------------------------------------------------------------
		//
		// Two codecs can both take `Parse(in ReadOnlySpan<byte>)` and disagree about which byte is byte 0. Handing
		// one the other's slice does not fail cleanly: it reads four bytes out of step and either runs off the end
		// or returns plausible values - a `StatDamageIIR` reporting `Flags +0.02` where the capture said
		// `HealthCurrent -37.25`, and silently undecodable messages in `--report timeline`.
		//
		// No codec consumes a leading length: a census of all 141 codecs with a standalone `Parse` finds none, and this
		// gate is what keeps it that way.
		//
		// It is behavioural rather than textual on purpose. A source-shape rule ("the first statement must be an
		// ExpectU32Be") would pass for a codec that reads the word and ignores it, which is precisely the failure
		// being guarded against. So: take a body the codec is known to accept, corrupt its first word, and require a
		// throw. A codec whose body starts at the sub-opcode cannot survive that; one that starts four bytes earlier
		// or later never looked at it.
		//
		// There is a legitimate second convention and it is declared, not excused. A c2s IIR (`DeleteItemCodec`,
		// `MoveInventoryItemCodec`) carries its sub-opcode in the RPC frame, so its body genuinely starts at the
		// entity class - `[u32 0xC350][u32 charInstance]...`. Those codecs say so with `OpcodeIsInTheFrame`, putting the
		// answer in the type where the next caller will see it. That is a declaration a codec makes about itself, not an
		// allowlist this test keeps - adding a name here is not possible, and setting the flag on a codec whose body
		// really does start at its opcode makes the gate fail rather than pass, because the corrupted-word body would
		// then have to parse and does not.
		[Fact]
		public void IirBodiesStartAtTheirSubOpcode()
		{
			var failures = new List<string>();
			var checkedCodecs = 0;
			foreach (var t in CodecModules)
			{
				var parse = Methods(t).SingleOrDefault(m => m.Name == "Parse");
				if (parse == null || DeclaredOpcodes(t).Count() != 1)
					continue;

				var opcode = DeclaredOpcodes(t).Single();
				var body = CorpusExtract.PacketsOfType((uint)opcode).Select(e => e.Body[4..]).FirstOrDefault(b => b.Length >= 4);
				if (body == null)
					continue;

				var parser = ParserFor(parse);
				try
				{
					parser(body);
				}
				catch (CodecParseException)
				{
					continue; // not a body this codec accepts - BuildParseRoundTripsOverTheCorpus owns that failure
				}

				checkedCodecs++;
				var corrupted = body.ToArray();
				corrupted[0] ^= 0xFF;

				var declaresFramedOpcode = Fields(t).Any(f => f.Name == "OpcodeIsInTheFrame");
				bool rejected;
				try
				{
					parser(corrupted);
					rejected = false;
				}
				catch (CodecParseException)
				{
					rejected = true;
				}

				if (rejected == declaresFramedOpcode)
				{
					failures.Add(declaresFramedOpcode
						? $"{t.Name} declares OpcodeIsInTheFrame, but its Parse rejects a body whose FIRST WORD was corrupted - so the body does start at the sub-opcode and the declaration is wrong"
						: $"{t.Name} accepted a body whose first word is not {opcode} - its Parse does not start at the sub-opcode, and nothing says where it does start");
				}
			}

			Assert.True(failures.Count == 0, "a codec must start at its sub-opcode, or declare that the frame carries it:\n  " + string.Join("\n  ", failures));

			// The corpus extract carries 9 IIR types, so the behavioural half reaches 8 codecs of the ~90 that declare
			// an opcode. That is worth having and is nowhere near enough on its own - the 80-odd it cannot reach would
			// pass in silence, which is the failure mode this gate exists to prevent in the first place.
			Assert.True(checkedCodecs > 5, $"the body-start gate only exercised {checkedCodecs} codecs - it is reflecting its way to nothing");
		}

		// The source half of the body-start rule, covering every IIR codec rather than the 8 the corpus can reach. It is
		// the weaker statement - it checks that the codec names the word it opens with, not that it validates it - but
		// naming is the whole point: nothing in `Parse(in ReadOnlySpan<byte>)` otherwise says which byte is byte 0, so
		// both readings look equally plausible at a call site. A first read of `var len = r.ReadU32Be()` and one of
		// `r.ExpectU32Be(Opcode, ...)` are the same shape and only the second says what it is.
		[Fact]
		public void IirCodecsNameTheWordTheyOpenWith()
		{
			var failures = new List<string>();
			var checkedCodecs = 0;
			foreach (var t in CodecModules)
			{
				if (Methods(t).All(m => m.Name != "Parse") || !DeclaredOpcodes(t).Any())
					continue;

				var file = CodecSourceFiles().FirstOrDefault(f => Path.GetFileNameWithoutExtension(f) == t.Name);
				if (file == null)
					continue;

				var first = FirstConsumingStatement(File.ReadAllText(file));
				if (first == null)
					continue;

				checkedCodecs++;
				if (Fields(t).Any(f => f.Name == "OpcodeIsInTheFrame"))
					continue;

				// Three spellings, all of which name the opening word: assert it is the opcode (the single-opcode case),
				// read it as an `IirTypes` to dispatch on (a genuine family), or - having declared the frame carries the
				// opcode - open at the entity class instead.
				if (!first.Contains("ExpectU32Be(Opcode", StringComparison.Ordinal)
					&& !first.Contains("IirTypes)r.ReadU32Be()", StringComparison.Ordinal)
					&& !first.Contains("ExpectU32Be(EntityClass", StringComparison.Ordinal))
					failures.Add($"{t.Name}: opens with `{first.Trim()}`");
			}

			Assert.True(failures.Count == 0,
				"an IIR codec must open by NAMING the word it consumes - `ExpectU32Be(Opcode, ...)`, a cast to IirTypes for a "
				+ "family, or `OpcodeIsInTheFrame` plus the entity class. An unnamed leading u32 is indistinguishable from a "
				+ "length prefix, which is Gitea #531:\n  " + string.Join("\n  ", failures));
			Assert.True(checkedCodecs > 50, $"the naming gate only exercised {checkedCodecs} codecs - it is reflecting its way to nothing");
		}

		// The first statement of a codec's decode path that consumes bytes: the body of `internal Read(ref SpanReader
		// r)` where there is one, else of the public `Parse`. Comments and the SpanReader construction are skipped -
		// they consume nothing.
		private static string? FirstConsumingStatement(string source)
		{
			var entry = Regex.Match(source, @"(?:internal|private) static [\w<>?\[\], ]+ Read\(ref SpanReader r\)\s*\{(.*?)\n\t\t\}", RegexOptions.Singleline);
			if (!entry.Success)
				entry = Regex.Match(source, @"public static [\w<>?\[\], ]+ Parse\(in ReadOnlySpan<byte>[^)]*\)\s*\{(.*?)\n\t\t\}", RegexOptions.Singleline);

			if (!entry.Success)
				return null;

			foreach (var line in entry.Groups[1].Value.Split('\n'))
			{
				var code = line.Trim();
				if (code.Length == 0 || code.StartsWith("//", StringComparison.Ordinal) || code.StartsWith("var r = new SpanReader", StringComparison.Ordinal))
					continue;
				if (code.Contains("r.Expect", StringComparison.Ordinal) || code.Contains("r.Read", StringComparison.Ordinal) || code.Contains("r.Peek", StringComparison.Ordinal))
					return code;
			}

			return null;
		}

		// `ICodec<T>` is retired: a wire value is a plain record and the codec module owns Build/Parse, so nothing in
		// the tree may implement it.
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
		// into the next member, which is how a `=> Build(...)` delegation gets flagged for the `s.WriteUInt32` in the
		// method below it.
		private static string ExpressionBodyOf(string src, int arrowEnd)
		{
			var end = src.IndexOf(';', arrowEnd);
			return end < 0 ? src[arrowEnd..] : src[arrowEnd..end];
		}

		// The root's files are its top level only - recursing would pull in every folder.
		private static IEnumerable<string> CodecSourceFiles()
		{
			var root = TestPaths.Repo("Faolan.Core.Network", "Codecs");
			var folders = SourceFiles(MigratedFolders.Where(f => f != RootFolder).Select(f => Path.Combine(root, f)));

			return MigratedFolders.Contains(RootFolder)
				? folders.Concat(Directory.EnumerateFiles(root, "*.cs", SearchOption.TopDirectoryOnly))
				: folders;
		}

		private static IEnumerable<IirTypes> DeclaredOpcodes(Type t)
		{
			foreach (var f in Fields(t))
			{
				if (f.IsLiteral && f.FieldType == typeof(uint) && f.Name == "Opcode")
					yield return (IirTypes)(uint)f.GetRawConstantValue()!;
				else if (f.Name == "HandledTypes" && f.FieldType == typeof(IirTypes[]))
					foreach (var v in (IirTypes[])f.GetValue(null)!)
						yield return v;
			}
		}

		private static IEnumerable<FieldInfo> Fields(Type t)
		{
			return t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
		}

		// A codec's declared message identity: `Opcode`/`HandledOpcodes` on the 15 index-registered classes, and
		// `Method`/`HandledMethods` on PlayerAgent/PlayerInterface, whose methods are registered by 32-bit hash and so
		// have no opcode to declare against at all. The plural forms count as much as the singular ones.
		private static bool DeclaresItsMessageId(FieldInfo f)
		{
			return f.Name.EndsWith("Opcode", StringComparison.Ordinal) || f.Name.EndsWith("Opcodes", StringComparison.Ordinal) ||
			       f.Name.EndsWith("Method", StringComparison.Ordinal) || f.Name.EndsWith("Methods", StringComparison.Ordinal);
		}

		// A codec whose Build produces a whole message rather than a fragment: the server layer sends it and no other
		// codec composes it. The second half is the definition of a sub-record ("a codec that declares no opcode must be
		// composed by another codec in the tree") and it is what `EveryCodecIsClassified` asks; without it a sub-record
		// whose parent lives in `Codecs/` reads as standalone the moment a service touches it too.
		private static bool IsStandaloneMessage(Type t)
		{
			var server = SourceFiles([TestPaths.Repo("Faolan")]).Any(f => File.ReadAllText(f).Contains($"{t.Name}.Build", StringComparison.Ordinal));

			return server && !IsComposedByAnotherCodec(t);
		}

		// True when some other codec source file mentions this codec - i.e. composes it into a larger body.
		private static bool IsComposedByAnotherCodec(Type t)
		{
			return CodecSourceFiles()
				.Where(f => !f.EndsWith($"{t.Name}.cs", StringComparison.Ordinal))
				.Any(f => File.ReadAllText(f).Contains($"{t.Name}.", StringComparison.Ordinal));
		}

		// `Parse(in ReadOnlySpan<byte>)` cannot be reached through `MethodInfo.Invoke`: a `ReadOnlySpan<byte>` is a ref
		// struct, so it cannot be boxed into the `object[]` an Invoke takes, and the parameter is additionally by-ref
		// ("Object of type 'System.Byte[]' cannot be converted to type 'System.ReadOnlySpan`1[System.Byte]&'"). Nor can
		// an expression tree build one - they support neither ref-struct locals nor by-ref parameters.
		//
		// So the call is emitted: wrap the array, take its address, call, box a value type. It is confined to this one
		// helper - if `Parse`'s signature ever changes, this fails loudly at emit time rather than silently covering
		// nothing.
		private static Func<byte[], object?> ParserFor(MethodInfo parse)
		{
			return Parsers.GetOrAdd(parse, static m =>
			{
				var dm = new DynamicMethod($"parse_{m.DeclaringType!.Name}", typeof(object), [typeof(byte[])], typeof(CodecSurfaceConventionTests).Module, true);
				var il = dm.GetILGenerator();
				il.DeclareLocal(typeof(ReadOnlySpan<byte>));
				il.Emit(OpCodes.Ldarg_0);
				il.Emit(OpCodes.Call, typeof(ReadOnlySpan<byte>).GetMethod("op_Implicit", [typeof(byte[])])!);
				il.Emit(OpCodes.Stloc_0);
				il.Emit(OpCodes.Ldloca_S, (byte)0);
				il.Emit(OpCodes.Call, m);
				if (m.ReturnType.IsValueType)
					il.Emit(OpCodes.Box, m.ReturnType);

				il.Emit(OpCodes.Ret);

				return dm.CreateDelegate<Func<byte[], object?>>();
			});
		}

		private static IEnumerable<MethodInfo> Methods(Type t)
		{
			return t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
		}

		private static IEnumerable<(Type Type, IirTypes Iir)> SingleIirCodecs()
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
