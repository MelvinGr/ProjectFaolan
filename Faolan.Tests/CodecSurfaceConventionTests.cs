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
	// ⚠️ EVERY TEST HERE IS SKIPPED, deliberately, and each Skip names the wave that un-skips it. The conversion runs
	// folder by folder; a gate that went live in wave 0 would be red for the whole sweep and would just get muted.
	// The bodies are written NOW so that un-skipping is a one-line change and so the rule has an executable
	// definition rather than only prose. Removing a Skip is part of landing that wave - not a separate cleanup.
	//
	// Wave order (see #530): 1 Inventory/Feats/Team/Guild · 2 Text/Dialogue/Economy · 3 World · 4 Combat ·
	// 5 Spawn/Quest · 6 FullCharSnapshot (+#447) · 7 Faolan/ moves in · 8 Faolan.Importer/ stops hand-decoding ·
	// 9 delete ICodec, un-skip everything.
	public class CodecSurfaceConventionTests
	{
		private const string CodecsNamespace = "Faolan.Core.Network.Codecs";

		private static readonly Regex BlockComment = new(@"/\*.*?\*/", RegexOptions.Singleline);
		private static readonly Regex ConstOffset = new(@"\bconst\s+int\s+\w*(Offset|Pos)\b");
		private static readonly Regex LineComment = new(@"//.*$", RegexOptions.Multiline);
		private static readonly Regex StringLiteral = new("\"(?:[^\"\\\\]|\\\\.)*\"");

		// A `new FaolanStream()` that is not introduced by `using` - the leak rule 6 exists for.
		private static readonly Regex UndisposedStream = new(@"(?<!using\s)(?<!using\s\w{0,20}\s?=\s?)new FaolanStream\(");

		// Anchored on Endpoints (Faolan.Core.Network/Constants) rather than on a codec: codec names are being
		// rewritten by this very refactor, and ListFrame - the obvious anchor - lives in Faolan.Core now.
		private static IEnumerable<Type> CodecTypes => typeof(Endpoints).Assembly.GetTypes()
			.Where(t => t.Namespace != null && t.Namespace.StartsWith(CodecsNamespace, StringComparison.Ordinal));

		// A codec MODULE: the static classes that carry Build/Parse. Value records are not modules.
		private static IEnumerable<Type> CodecModules => CodecTypes
			.Where(t => t is { IsAbstract: true, IsSealed: true }) // C# static class
			.Where(t => Methods(t).Any(m => m.Name is "Build" or "Parse"));

		// ---- rule 1: the public surface -------------------------------------------------------------------

		[Fact(Skip = "un-skip per wave; live from wave 9")]
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

		[Fact(Skip = "un-skip per wave; live from wave 9")]
		public void BuildReturnsBytes()
		{
			var offenders = (from t in CodecModules
				from m in Methods(t).Where(m => m.Name == "Build")
				where m.ReturnType != typeof(byte[])
				select $"{t.Name}.Build -> {m.ReturnType.Name}").ToList();

			Assert.True(offenders.Count == 0, "Build must return byte[]:\n  " + string.Join("\n  ", offenders));
		}

		[Fact(Skip = "un-skip per wave; live from wave 9")]
		public void ParseTakesASpanAndReturnsAValue()
		{
			var offenders = new List<string>();
			foreach (var t in CodecModules)
			{
				var parse = Methods(t).SingleOrDefault(m => m.Name == "Parse");
				if (parse == null)
					continue;

				var ps = parse.GetParameters();
				if (ps.Length != 1 || ps[0].ParameterType != typeof(ReadOnlySpan<byte>).MakeByRefType() || !ps[0].IsIn)
					offenders.Add($"{t.Name}.Parse must take a single `in ReadOnlySpan<byte>`");
				if (parse.ReturnType == typeof(void) || parse.ReturnType == typeof(bool))
					offenders.Add($"{t.Name}.Parse must return the decoded value, not {parse.ReturnType.Name}");
				if (ps.Any(p => p.IsOut))
					offenders.Add($"{t.Name}.Parse must not have out parameters");
			}

			Assert.True(offenders.Count == 0, "Parse signature:\n  " + string.Join("\n  ", offenders));
		}

		[Fact(Skip = "un-skip per wave; live from wave 9")]
		public void ParseIsNotOverloaded()
		{
			var offenders = CodecModules
				.Where(t => Methods(t).Count(m => m.Name == "Parse") > 1)
				.Select(t => t.Name)
				.ToList();

			Assert.True(offenders.Count == 0,
				"exactly one Parse per codec - overloaded readers are how `which one decodes this body?` ambiguity gets in:\n  " + string.Join("\n  ", offenders));
		}

		[Fact(Skip = "un-skip per wave; live from wave 9")]
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
		// be a single `=> Build(...)` expression, not a second encoder that can drift from the first.
		[Fact(Skip = "un-skip per wave; live from wave 9")]
		public void OnlyTheCanonicalBuildWritesBytes()
		{
			var offenders = new List<string>();
			foreach (var file in CodecSourceFiles())
			{
				var src = Strip(File.ReadAllText(file));
				foreach (Match m in Regex.Matches(src, @"public static byte\[\] Build\s*\(([^)]*)\)\s*(=>|\{)", RegexOptions.Singleline))
				{
					// The canonical overload takes exactly one parameter whose type ends in IIR (or is the codec's
					// value record) - it is allowed to encode. Everything else must delegate.
					var parameters = m.Groups[1].Value;
					if (parameters.Split(',').Length == 1 && Regex.IsMatch(parameters, @"\b\w*(IIR|Iir)\b"))
						continue;

					var body = m.Groups[2].Value == "=>" ? src[m.Index..Math.Min(src.Length, m.Index + m.Length + 200)] : BodyOf(src, m.Index + m.Length - 1);
					if (body.Contains("new FaolanStream", StringComparison.Ordinal) || Regex.IsMatch(body, @"\.Write[A-Z]\w*\("))
						offenders.Add($"{Path.GetFileName(file)}: Build({parameters.Trim()}) encodes instead of delegating to Build(T)");
				}
			}

			Assert.True(offenders.Count == 0, "only Build(T) may write bytes; other overloads delegate:\n  " + string.Join("\n  ", offenders));
		}

		// ---- rule 3: sequential reading -------------------------------------------------------------------

		[Fact(Skip = "un-skip per wave; live from wave 9 (SpawnPacketBuilder's 12 patch offsets need the captured template retired first - see #530 §7)")]
		public void NoConstOffsetsInCodecs()
		{
			var offenders = (from file in CodecSourceFiles()
				let lines = File.ReadAllLines(file)
				from i in Enumerable.Range(0, lines.Length)
				let code = Strip(lines[i])
				where ConstOffset.IsMatch(code)
				select $"{Path.GetFileName(file)}:{i + 1}  {code.Trim()}").ToList();

			Assert.True(offenders.Count == 0,
				"read sequentially through SpanReader - a const offset means the bytes before it were never decoded:\n  " + string.Join("\n  ", offenders));
		}

		// ---- rule 6: stream disposal ----------------------------------------------------------------------

		[Fact(Skip = "un-skip per wave; live from wave 9")]
		public void FaolanStreamsAreDisposed()
		{
			string[] roots = [TestPaths.Repo("Faolan.Core.Network"), TestPaths.Repo("Faolan"), TestPaths.Repo("Faolan.Importer")];
			var offenders = (from file in SourceFiles(roots)
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

		[Fact(Skip = "un-skip in wave 6 - 39 renames land per folder (see the table in #530 §3)")]
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

		[Fact(Skip = "un-skip in wave 6 - today 11 IIRs have >1 emitter; this is the MERGE gate (see #530 §3)")]
		public void EachIirHasExactlyOneCodec()
		{
			var byOpcode = new Dictionary<GameServer0X2000Opcodes, List<string>>();
			foreach (var t in CodecTypes)
			foreach (var op in DeclaredOpcodes(t))
				(byOpcode.TryGetValue(op, out var l) ? l : byOpcode[op] = []).Add(t.Name);

			var dupes = byOpcode.Where(kv => kv.Value.Count > 1).Select(kv => $"{kv.Key}: {string.Join(", ", kv.Value)}").ToList();

			Assert.True(dupes.Count == 0, "one IIR, one codec - two codecs declaring the same opcode ARE the same codec and must be merged:\n  " + string.Join("\n  ", dupes));
		}

		[Fact(Skip = "un-skip per wave; live from wave 9")]
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

		[Fact(Skip = "un-skip in wave 2 - only 4 of the ~14 RPC-message codecs declare their method today")]
		public void RpcMessageCodecsDeclareTheirMethodOpcode()
		{
			// A codec with a standalone body but no IIR opcode is an RPC-message codec: it must declare its method
			// off its own interface enum, so the identity is recorded somewhere rather than nowhere.
			var offenders = CodecModules
				.Where(t => !DeclaredOpcodes(t).Any())
				.Where(t => Fields(t).All(f => !f.Name.EndsWith("Opcode", StringComparison.Ordinal)))
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

		[Fact(Skip = "un-skip in wave 8 - ConversationExtractor and QuestDefinitionExtractor fail today (see #530 §9 C)")]
		public void IirOpcodesAreOnlyNamedInsideCodecs()
		{
			string[] roots = [TestPaths.Repo("Faolan"), TestPaths.Repo("Faolan.Importer")];
			var offenders = (from file in SourceFiles(roots)
				where !Path.GetFileName(file).Contains("Dispatch", StringComparison.Ordinal) // the routing switch does not decode
				let lines = File.ReadAllLines(file)
				from i in Enumerable.Range(0, lines.Length)
				let code = Strip(lines[i])
				where code.Contains("GameServer0X2000Opcodes.", StringComparison.Ordinal)
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

		[Fact(Skip = "un-skip in wave 9, when ICodec.cs is deleted")]
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

		private static IEnumerable<string> CodecSourceFiles()
		{
			return SourceFiles([TestPaths.Repo("Faolan.Core.Network", "Codecs")]);
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

		// A codec whose Build produces a whole message rather than a fragment: it is called from the server layer
		// (a listener/service sends it) rather than only from another codec.
		private static bool IsStandaloneMessage(Type t)
		{
			var server = SourceFiles([TestPaths.Repo("Faolan")]).Any(f => File.ReadAllText(f).Contains($"{t.Name}.Build", StringComparison.Ordinal));
			return server;
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
