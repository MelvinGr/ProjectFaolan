using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Faolan.Core.Enums;
using Faolan.Core.Network.Constants;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Enforces the codec/builder opcode convention. A static class cannot implement an interface, and two thirds of
	// the codecs are static classes, so the convention cannot be expressed as an interface - it is expressed here
	// instead, and this test is what makes it stick:
	//
	//   1. A codec declares the IIR type(s) it emits in one uniform place:
	//        - `public const uint Opcode = (uint)IirTypes.X`   (exactly one type), or
	//        - `public static readonly IirTypes[] HandledTypes` (more than one).
	//   2. `SubOpcode` on a static const never comes back.
	//   3. No IIR opcode is written as a bare hex literal anywhere in Codecs/ - it always comes from the enum,
	//      so `grep IirTypes.<Type>` finds every emitter.
	//
	// Codecs with no opcode (sub-record helpers like CharacterStatBlockCodec, StatListCodec, PfAccessWindowEntryCodec)
	// are fine and are deliberately not required to declare one - reflection cannot tell "emits an IIR" from "is a
	// sub-record".
	public class CodecConventionTests
	{
		private const string CodecsNamespace = "Faolan.Core.Network.Codecs";

		// Files where the word is deliberately still a literal, with the reason. Not a place to silence the test.
		// The client never treats this word as a constant: of the 136 functions in AgeOfConan.exe that touch
		// 0x3F1/0x7E2, 77 divide by 0x3F1 to read a count and 59 multiply to write one - zero compare it to a
		// constant (tools/ghidra/DumpListFraming.java). Every "0x7E2 constant" ever written down was a 1-element
		// list. If you are about to add an entry here, that is the prior.
		private static readonly Dictionary<string, string> ListWordLiteralAllowlist = new Dictionary<string, string>(StringComparer.Ordinal)
		{
			// The one genuine collision rather than a mistaken constant: CharacterStatId is a dense catalog enum and
			// stat 1009 - CombatRatingHoly - is written 0x03F1. It is a stat id that happens to equal the framing unit,
			// not a framing word: nothing here frames a list, and the value comes from the client's stat table, not from
			// a count. This is the opposite of the failure the rule exists for (a count mistaken for a constant), which
			// is why it is an allowlist entry and not a reason to narrow the scan.
			["CharacterStatId.cs"] = "stat id 1009 (CombatRatingHoly) numerically collides with 0x3F1; it is not a list frame"
		};
		private static readonly Regex BlockComment = new Regex(@"/\*.*?\*/", RegexOptions.Singleline);

		// The tail of a `WriteArray(` whose arguments are only hex byte literals - a verbatim capture blob.
		private static readonly Regex ByteLiteralsOnly = new Regex(@"^\s*0x[0-9A-Fa-f]{2}\s*(,\s*0x[0-9A-Fa-f]{2}\s*)*\)");

		// 8-hex-digit literals; IIR opcodes are 32-bit BKDR-31 hashes so they always appear in this form.
		private static readonly Regex HexLiteral = new Regex(@"0x[0-9A-Fa-f]{8}\b");
		private static readonly Regex LineComment = new Regex(@"//.*$", RegexOptions.Multiline);

		// The count-framing word in any spelling it has been written in: 0x3F1 (the unit), 0x7E2 (a 1-element list -
		// the value that keeps passing for a constant), and decimal 1009, which two snapshot codecs used.
		private static readonly Regex ListWordLiteral = new Regex(@"\b(0x0*3F1|0x0*7E2|1009)\b", RegexOptions.IgnoreCase);

		// String literals are prose (exception messages naming the word) - stripped like comments before scanning.
		private static readonly Regex StringLiteral = new Regex("\"(?:[^\"\\\\]|\\\\.)*\"");

		// The entity object-class ids, in the 4- and 8-digit spellings both have been written in.
		private static readonly Regex EntityClassLiteral = new Regex(@"\b0x0*(C350|C748|C75B|C878|C879|C87A|C87D|DEB7)\b", RegexOptions.IgnoreCase);

		// The RPC endpoint class ids - BKDR-31 hashes of the client class names, all 32-bit, all in Endpoints.cs.
		private static readonly Regex EndpointClassLiteral = new Regex(
			@"\b0x(0C384054|1D359116|28377648|3D744981|5B9756FC|604DDBA0|6C67C147|6DD55614|7D155738|82F20484|8B87F791|A9ECB95D|B171CE13|BBD560C8|F2C2D5B5)\b", RegexOptions.IgnoreCase);

		// Anchored on Endpoints (Faolan.Core.Network/Constants).
		private static IEnumerable<Type> CodecTypes => typeof(Endpoints).Assembly.GetTypes().Where(t => t.Namespace != null && t.Namespace.StartsWith(CodecsNamespace, StringComparison.Ordinal));

		// (7) The RPC endpoint class ids come from the registry, never a hand-rolled hash. Same rule as EntityClass
		// below, for the other id namespace: these are BKDR-31 hashes of the client's class names
		// (0xA9ECB95D = hash("GameCharAgent")).
		//
		// The registry is `Faolan.Core.Constants.RpcInterfaces` (it also carries each class's name and its `*Methods`
		// enum) and is the only file allowed to spell an id; `Endpoints` builds the wire ProtoEndpoint from it.
		// ProtocolDocsTests recomputes every id from its class name through Bkdr31, which is what proves the table
		// right.
		[Fact]
		public void EndpointClassIdsComeFromEndpoints_NotAHandRolledHash()
		{
			string[] roots = [TestPaths.Repo("Faolan.Core.Network"), TestPaths.Repo("Faolan"), TestPaths.Repo("Faolan.Importer"), TestPaths.Repo("Faolan.Core")];
			var offenders = new List<string>();

			foreach (var file in roots
				         .Where(Directory.Exists)
				         .SelectMany(r => Directory.EnumerateFiles(r, "*.cs", SearchOption.AllDirectories))
				         .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
			{
				if (Path.GetFileName(file) is "RpcInterfaces.cs") // the registry itself
					continue;

				var lines = File.ReadAllLines(file);
				for (var i = 0; i < lines.Length; i++)
				{
					var code = StringLiteral.Replace(LineComment.Replace(BlockComment.Replace(lines[i], ""), ""), "");
					if (code.Contains("RpcInterfaces.", StringComparison.Ordinal))
						continue; // it comes from the registry on this line - that is the convention

					if (EndpointClassLiteral.IsMatch(code))
						offenders.Add($"{Path.GetFileName(file)}:{i + 1}  {code.Trim()}");
				}
			}

			Assert.True(offenders.Count == 0, "RPC endpoint class ids must come from Endpoints (GameCharAgentType = 0xA9ECB95D, ...):\n  " + string.Join("\n  ", offenders));
		}

		// (5) The entity object-class word comes from EntityClass, never a hand-rolled 0xC350. Same disease as the
		// ListFrame rule below, and further along: docs/entity-class-ids.md records that EntityClass replaced ~a dozen
		// ad-hoc local constants, and nine of them grew back while nothing was watching - `SpellTargetTypeWord`,
		// `CharacterClass` (twice), `ReceiverClass`, `Marker`, `TypeTag`, `LootTypeTag`, `VisualChangeTypeTag` and a
		// bare `C350`, all holding a value the client itself checks by name (MoveTaskIIR_c::PollStatus:
		// `id.type != 50000` -> "id is not of a character!").
		//
		// The cost is not tidiness. With no single registry nobody can see that these words are one id namespace, so
		// each new codec re-derives "some constant word here" from its own capture - TeamMemberRecord carrying 0xC79C
		// as `Field8`, ShopContentCodec's shop tag living on a database entity as `Shop.WireType`. One name, one place,
		// one test.
		[Fact]
		public void EntityClassWordComesFromEntityClass_NotAHandRolledLiteral()
		{
			// Faolan.Core.Database is in scope because that is where two of them hide: the guild and shop class words
			// are easily written as `Guild.WireType` / `Shop.WireType` on the EF entities, a layer that has no business
			// owning a wire constant.
			string[] roots =
			[
				TestPaths.Repo("Faolan.Core.Network"), TestPaths.Repo("Faolan"), TestPaths.Repo("Faolan.Importer"), TestPaths.Repo("Faolan.Core.Database"), TestPaths.Repo("Faolan.Core")
			];
			var offenders = new List<string>();

			foreach (var file in roots
				         .Where(Directory.Exists)
				         .SelectMany(r => Directory.EnumerateFiles(r, "*.cs", SearchOption.AllDirectories))
				         .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
				                     !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}")))
			{
				// EntityClass.cs is the registry.
				var name = Path.GetFileName(file);
				if (name is "EntityClass.cs")
					continue;

				var lines = File.ReadAllLines(file);
				for (var i = 0; i < lines.Length; i++)
				{
					var code = StringLiteral.Replace(LineComment.Replace(BlockComment.Replace(lines[i], ""), ""), "");
					if (code.Contains("EntityClass", StringComparison.Ordinal))
						continue; // it comes from EntityClass on this line - that is the convention

					if (EntityClassLiteral.IsMatch(code))
						offenders.Add($"{name}:{i + 1}  {code.Trim()}");
				}
			}

			Assert.True(offenders.Count == 0, "entity object-class ids must come from EntityClass (Character = 0xC350, LootBag = 0xC87A, ...):\n  " + string.Join("\n  ", offenders));
		}

		// (4) The count-framed list word comes from ListFrame, never a hand-rolled literal. Scattered local names are
		// exactly why this word keeps getting re-mistaken for a magic constant: 0x7E2 is just ListFrame.ForCount(1),
		// and single-element lists are the common case in captures. Three subsystems shipped it as a "constant" before
		// anyone noticed (resurrection spots, the AH search head, and SendMail). One name, one place, one test.
		//
		// Allowed: ListFrame.cs itself.
		[Fact]
		public void CountFramedListWordComesFromListFrame_NotAHandRolled0x3F1()
		{
			// Codecs/ is not enough: the character-list framing lives in the PlayerAgent listener and the guild lists
			// in CSPlayerAgent, both of which can open-code it. A wire-shape rule has to cover everywhere the wire is
			// written.
			//
			// Faolan.Core is a root because ListFrame lives there: the registry itself is allowlisted by filename, so
			// without this root the one assembly that owns the rule would be the one place nothing checked it - and
			// SpanReader/FaolanStream, which sit beside it, are exactly where a stray framing word would be written.
			var roots = new[] { TestPaths.Repo("Faolan.Core.Network", "Codecs"), TestPaths.Repo("Faolan"), TestPaths.Repo("Faolan.Core") };
			var offenders = new List<string>();

			foreach (var file in roots.SelectMany(root => Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)))
			{
				// EF migrations are generated + frozen; they carry no wire framing.
				if (file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
					continue;

				var name = Path.GetFileName(file);
				if (name is "ListFrame.cs" || ListWordLiteralAllowlist.ContainsKey(name))
					continue;

				var lines = File.ReadAllLines(file);
				for (var i = 0; i < lines.Length; i++)
				{
					var code = StringLiteral.Replace(LineComment.Replace(BlockComment.Replace(lines[i], ""), ""), "");
					if (code.Contains("ListFrame", StringComparison.Ordinal))
						continue; // it comes from ListFrame on this line - that is the convention

					if (ListWordLiteral.IsMatch(code))
						offenders.Add($"{name}:{i + 1}  {code.Trim()}");
				}
			}

			Assert.True(offenders.Count == 0, "the count-framed list word must come from ListFrame (0x3F1 = ListFrame.Unit, 0x7E2 = ListFrame.ForCount(1)):\n  " + string.Join("\n  ", offenders));
		}

		// (1b) HandledTypes is the multi-type form: a non-empty, duplicate-free array of enum members.
		[Fact]
		public void HandledTypes_AreNonEmptyAndDistinct()
		{
			var declaring = new List<string>();
			foreach (var t in CodecTypes)
			{
				var f = Fields(t).FirstOrDefault(f => f.Name == "HandledTypes");
				if (f == null)
					continue;

				declaring.Add(t.Name);
				Assert.True(f.FieldType == typeof(IirTypes[]), $"{t.Name}.HandledTypes must be IirTypes[], was {f.FieldType.Name}");

				var values = (IirTypes[])f.GetValue(null)!;
				Assert.True(values.Length > 1, $"{t.Name}.HandledTypes has {values.Length} entries - use `const uint Opcode` for a single type");
				Assert.True(values.Distinct().Count() == values.Length, $"{t.Name}.HandledTypes contains duplicates");
				Assert.All(values, v => Assert.True(Enum.IsDefined(v), $"{t.Name}.HandledTypes has an undefined member"));
			}

			// Guards the test itself: if nothing declares HandledTypes the multi-type case silently stopped being covered.
			Assert.True(declaring.Count >= 3, $"only {declaring.Count} codecs declare HandledTypes - the reflection filter is probably broken");
		}

		// (3) The one that actually matters: an IIR opcode never appears as a bare hex literal in codec code. If it
		// did, `grep IirTypes.<Type>` would miss that emitter and the coverage matrix could not attribute it. Comments
		// may still spell the hex for readability - they are stripped before scanning.
		[Fact]
		public void NoCodecSourceContainsAMagicIirOpcodeLiteral()
		{
			// Scans the codec layer and its consumers. Codecs/ alone is not enough: the snapshot opcode 0x737A6DF9 has an
			// enum member (FullPlayerCharacterIIR) and Codecs/ is clean, while bare literals accumulate in the server, the
			// importer and the tests - exactly the emitters `grep IirTypes.<Type>` needs to find. Mirrors the ListFrame
			// rule below, which spans both trees.
			string[] roots = [TestPaths.Repo("Faolan.Core.Network", "Codecs"), TestPaths.Repo("Faolan"), TestPaths.Repo("Faolan.Importer")];
			var defined = Enum.GetValues<IirTypes>().ToHashSet();
			var offenders = new List<string>();

			foreach (var file in roots
				         .Where(Directory.Exists)
				         .SelectMany(r => Directory.EnumerateFiles(r, "*.cs", SearchOption.AllDirectories))
				         .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
				                     !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}")))
			{
				var lines = File.ReadAllLines(file);
				for (var i = 0; i < lines.Length; i++)
				{
					var code = LineComment.Replace(BlockComment.Replace(lines[i], ""), "");
					if (code.Contains("IirTypes", StringComparison.Ordinal))
						continue; // the value comes from the enum on this line - that is the convention

					foreach (Match m in HexLiteral.Matches(code))
					{
						var value = Convert.ToUInt32(m.Value, 16);
						if (defined.Contains((IirTypes)value))
							offenders.Add($"{Path.GetFileName(file)}:{i + 1}  {m.Value} == IirTypes.{(IirTypes)value}");
					}
				}
			}

			Assert.True(offenders.Count == 0, "IIR opcodes must come from IirTypes, not a hex literal:\n  " + string.Join("\n  ", offenders));
		}

		// (8) Every DispatchPassBlob body is length-framed by its sender. A codec emits a bare record
		// `[u32 iirType][record]`; the carrier's `[u32 len]` is written at the send site, so a body argument that is
		// just a `Build(...)` result puts the IIR type where the client reads a length.
		//
		// That is a kill, not a glitch. An unframed AddToGroupIIR has the client read 0x0DF44B73 as a 234 MB payload
		// and drop the connection for both members of a team (see WorldIir's header); an unframed WorldObjectCodec body
		// has 0xF096251E read as a ~4 GB length, so the client leaves the instant an NPC dies. Nothing in the type
		// system connects the two halves: both sides are `byte[]`, so moving the length compiles clean on the day it
		// breaks the wire.
		//
		// Detection is deliberately blunt: the body expression must either frame (`WriteArrayUInt32Length`) or be a
		// verbatim byte-literal blob whose framing is baked into the captured bytes. Anything else must earn an
		// allowlist entry naming why - which is one line to write and the exact moment to ask whether the callee still
		// prefixes its own length.
		[Fact]
		public void EveryDispatchPassBlobBodyIsLengthFramedBySender()
		{
			// Send sites whose body is already `[u32 len][record]` when it arrives, with the reason. Not a place to
			// silence the test: if a codec stopped prefixing its own length, the fix is at the call site, not here.
			var allowlist = new Dictionary<string, string>(StringComparer.Ordinal)
			{
				["GameWire.cs"] = "FrameWorldEntryRecord takes a body that already carries its [u32 bodyLen] - its parameter is named bodyWithLenPrefix, and its one caller writes that prefix itself",
				["CreateFlowService.cs"] = "the create-editor 12-byte [entityClass][handle][0] body on the GameCharInterfaceProject receiver is not an IIR record at all, so it carries no record length",
				["CreateEditorBursts.cs"] = "the editor's avatar snapshot writes its own [u32 len] prefix (WriteUInt32(length).WriteArray) ahead of a record it holds as bytes"
			};

			var offenders = new List<string>();
			var checkedCount = 0;
			foreach (var file in Directory.EnumerateFiles(TestPaths.Repo("Faolan"), "*.cs", SearchOption.AllDirectories))
			{
				var source = File.ReadAllText(file);
				var name = Path.GetFileName(file);
				foreach (var body in DispatchPassBlobBodies(source))
				{
					checkedCount++;
					if (body.Contains("WriteArrayUInt32Length", StringComparison.Ordinal) || IsVerbatimByteBlob(body) || allowlist.ContainsKey(name))
						continue;

					offenders.Add($"{name}: DispatchPassBlob body is not length-framed: {Collapse(body)}");
				}
			}

			Assert.True(offenders.Count == 0,
				"A DispatchPassBlob body must be framed by the sender (WorldIir.Send / WriteArrayUInt32Length) - a bare record makes the client read the IIR type as a length and drop the connection:\n  "
				+ string.Join("\n  ", offenders));
			Assert.True(checkedCount >= 40, $"only {checkedCount} DispatchPassBlob sends found - the scan is probably broken");
		}

		// The final argument of each `new Rpc(..., DispatchPassBlob, <body>)`: everything from the opcode to the paren
		// that closes the constructor, found by depth-counting so a nested call's own parens do not end it early.
		private static IEnumerable<string> DispatchPassBlobBodies(string source)
		{
			const string marker = "GameInterfaceMethods.DispatchPassBlob,";
			for (var at = source.IndexOf(marker, StringComparison.Ordinal); at >= 0; at = source.IndexOf(marker, at + 1, StringComparison.Ordinal))
			{
				var start = at + marker.Length;
				var depth = 0;
				var end = start;
				for (; end < source.Length; end++)
				{
					if (source[end] == '(')
						depth++;
					else if (source[end] == ')' && depth-- == 0)
						break;
				}

				yield return source[start..end];
			}
		}

		// A hand-carried capture blob - `[0x00, 0x00, 0x00, 0x1F, 0xA3, ...]` - where the leading bytes are the length
		// and there is no codec on the other side to move it. Recognised as "byte literals and nothing else".
		private static bool IsVerbatimByteBlob(string body)
		{
			var writes = body.IndexOf("WriteArray(", StringComparison.Ordinal);
			return writes >= 0 && ByteLiteralsOnly.IsMatch(body[(writes + "WriteArray(".Length)..]);
		}

		private static string Collapse(string body)
		{
			var one = string.Join(' ', body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
			return one.Length <= 100 ? one : one[..100] + "…";
		}

		// (2) `SubOpcode` must not appear on a static const. (Instance members named SubOpcode - the WorldObject
		// column, WorldObjectIIR.SubOpcode - are a different thing and are allowed.)
		[Fact]
		public void NoStaticConstantIsNamedSubOpcode()
		{
			var bad = (from t in CodecTypes from f in Fields(t) where f.Name.EndsWith("SubOpcode", StringComparison.Ordinal) select $"{t.Name}.{f.Name}").ToList();

			Assert.True(bad.Count == 0, "Static opcode constants are named `Opcode`, not `SubOpcode`:\n  " + string.Join("\n  ", bad));
		}

		// (1) Every const named *Opcode carries a real IirTypes value - never a hand-typed hash.
		[Fact]
		public void OpcodeConstants_AreDefinedEnumMembers()
		{
			var bad = new List<string>();
			var checkedCount = 0;
			foreach (var t in CodecTypes)
			foreach (var f in Fields(t).Where(f => f.IsLiteral && f.FieldType == typeof(uint) && f.Name.EndsWith("Opcode", StringComparison.Ordinal)))
			{
				checkedCount++;
				var value = (uint)f.GetRawConstantValue()!;
				if (!Enum.IsDefined((IirTypes)value))
					bad.Add($"{t.Name}.{f.Name} = 0x{value:X8} is not a defined IirTypes member");
			}

			Assert.True(bad.Count == 0, "Opcode constants must be (uint)IirTypes.<Type>:\n  " + string.Join("\n  ", bad));
			Assert.True(checkedCount >= 15, $"only {checkedCount} Opcode constants found - the reflection filter is probably broken");
		}

		private static IEnumerable<FieldInfo> Fields(Type t)
		{
			return t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
		}
	}
}
