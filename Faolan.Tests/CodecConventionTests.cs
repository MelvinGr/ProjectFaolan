using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs;
using Faolan.Core.Network.Constants;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Enforces the codec/builder opcode convention (#317). A static class cannot implement an interface, and two
	// thirds of the codecs ARE static classes, so the convention cannot be expressed as an interface - it is
	// expressed here instead, and this test is what makes it stick:
	//
	//   1. A codec declares the IIR type(s) it emits in ONE uniform place:
	//        - `public const uint Opcode = (uint)GameServer0X2000Opcodes.X`   (exactly one type), or
	//        - `public static readonly GameServer0X2000Opcodes[] HandledTypes` (more than one).
	//   2. The old inconsistent name (`SubOpcode` on a static const) never comes back.
	//   3. No IIR opcode is written as a bare hex literal anywhere in Codecs/ - it always comes from the enum,
	//      so `grep GameServer0X2000Opcodes.<Type>` finds every emitter.
	//
	// Codecs with NO opcode (sub-record helpers like CharacterStatBlockCodec, StatListCodec, PfAccessWindowEntryCodec) are fine and
	// are deliberately not required to declare one - reflection cannot tell "emits an IIR" from "is a sub-record".
	public class CodecConventionTests
	{
		private const string CodecsNamespace = "Faolan.Core.Network.Codecs";

		// Files where the word is deliberately still a literal, with the reason. NOT a place to silence the test.
		// Currently EMPTY, and that is the finding: the client never treats this word as a constant. Of the 136
		// functions in AgeOfConan.exe that touch 0x3F1/0x7E2, 77 divide by 0x3F1 to read a count and 59 multiply to
		// write one - ZERO compare it to a constant (tools/ghidra/DumpListFraming.java). Every "0x7E2 constant"
		// we ever wrote down was a 1-element list. If you are about to add an entry here, that is the prior.
		private static readonly Dictionary<string, string> ListWordLiteralAllowlist = new(StringComparer.Ordinal)
		{
			// The one genuine COLLISION rather than a mistaken constant, surfaced when Faolan.Core joined the scan
			// roots (#530): CharacterStatId is a dense catalog enum and stat 1009 - CombatRatingHoly - is written
			// 0x03F1. It is a stat id that happens to equal the framing unit, not a framing word: nothing here
			// frames a list, and the value comes from the client's stat table, not from a count. Note this is the
			// OPPOSITE of the failure the rule exists for (a count mistaken for a constant), which is why it is an
			// allowlist entry and not a reason to narrow the scan.
			["CharacterStatId.cs"] = "stat id 1009 (CombatRatingHoly) numerically collides with 0x3F1; it is not a list frame"
		};
		private static readonly Regex BlockComment = new(@"/\*.*?\*/", RegexOptions.Singleline);

		// 8-hex-digit literals; IIR opcodes are 32-bit BKDR-31 hashes so they always appear in this form.
		private static readonly Regex HexLiteral = new(@"0x[0-9A-Fa-f]{8}\b");
		private static readonly Regex LineComment = new(@"//.*$", RegexOptions.Multiline);

		// The count-framing word in any spelling it has been written in: 0x3F1 (the unit), 0x7E2 (a 1-element list -
		// the value that keeps passing for a constant), and decimal 1009, which two snapshot codecs used.
		private static readonly Regex ListWordLiteral = new(@"\b(0x0*3F1|0x0*7E2|1009)\b", RegexOptions.IgnoreCase);

		// String literals are prose (exception messages naming the word) - stripped like comments before scanning.
		private static readonly Regex StringLiteral = new("\"(?:[^\"\\\\]|\\\\.)*\"");

		// The entity object-class ids, in the 4- and 8-digit spellings both have been written in.
		private static readonly Regex EntityClassLiteral = new(@"\b0x0*(C350|C748|C75B|C878|C879|C87A|C87D|DEB7)\b", RegexOptions.IgnoreCase);

		// The RPC endpoint class ids - BKDR-31 hashes of the client class names, all 32-bit, all in Endpoints.cs.
		private static readonly Regex EndpointClassLiteral = new(
			@"\b0x(0C384054|1D359116|28377648|3D744981|5B9756FC|604DDBA0|6C67C147|6DD55614|7D155738|82F20484|8B87F791|A9ECB95D|B171CE13|BBD560C8|F2C2D5B5)\b", RegexOptions.IgnoreCase);

		// Anchored on Endpoints (Faolan.Core.Network/Constants): the old anchor was `ICodec<>`, deleted in #530 wave 9.
		private static IEnumerable<Type> CodecTypes => typeof(Endpoints).Assembly.GetTypes().Where(t => t.Namespace != null && t.Namespace.StartsWith(CodecsNamespace, StringComparison.Ordinal));

		// (6) WAS `TypesImplementingICodec_AreNotNamedCodec`: a `*Codec`/`*Builder` is a static MODULE of stateless
		// ops, while a self-serializing `ICodec<T>` was a wire VALUE, so a type could not be both. Thirteen were -
		// AreaFormulaCodec, ChainKillCodec, CrimeCodec, RegenCodec, ... - one batch of V1 IIR decoders that all copied
		// the same wrong shape, which is how `ChainKillCodec.Parse(body)` came to return a `ChainKillCodec`: a "codec"
		// that is a value.
		//
		// The rule is GONE rather than moved, because #530 removed the shape it policed: `ICodec<T>` is deleted (wave
		// 9), a value is a plain record beside its module, and neither is called a codec.
		// `CodecSurfaceConventionTests.NoIcodecImplementations` is what keeps the interface from coming back.

		// (7) The RPC ENDPOINT class ids come from Endpoints, never a hand-rolled hash. Same rule as EntityClass
		// below, for the other id namespace: these are BKDR-31 hashes of the client's class names
		// (0xA9ECB95D = hash("GameCharAgent")), and Endpoints.cs is where all fifteen are recovered and named.
		// CaptureSpellImporter and CaptureCharacterStateImporter had re-declared two of them locally.
		//
		// Faolan.Tests is deliberately NOT scanned: Support/ProtocolDocs/OpcodesPage.cs holds the name -> hash table
		// the docs are generated from and must spell the hashes out, and it is what proves Endpoints right
		// (ProtocolDocsTests recomputes each one through Bkdr31).
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
				if (Path.GetFileName(file) is "Endpoints.cs") // the registry itself
					continue;

				var lines = File.ReadAllLines(file);
				for (var i = 0; i < lines.Length; i++)
				{
					var code = StringLiteral.Replace(LineComment.Replace(BlockComment.Replace(lines[i], ""), ""), "");
					if (code.Contains("Endpoints.", StringComparison.Ordinal))
						continue; // it comes from Endpoints on this line - that IS the convention

					if (EndpointClassLiteral.IsMatch(code))
						offenders.Add($"{Path.GetFileName(file)}:{i + 1}  {code.Trim()}");
				}
			}

			Assert.True(offenders.Count == 0, "RPC endpoint class ids must come from Endpoints (GameCharAgentType = 0xA9ECB95D, ...):\n  " + string.Join("\n  ", offenders));
		}

		// (5) The entity object-CLASS word comes from EntityClass, never a hand-rolled 0xC350. Same disease as the
		// ListFrame rule below, and further along: docs/entity-class-ids.md already records that EntityClass "replaced
		// ~a dozen ad-hoc local constants" - and then nine of them grew back, because nothing was watching. They were
		// `SpellTargetTypeWord`, `CharacterClass` (twice), `ReceiverClass`, `Marker`, `TypeTag`, `LootTypeTag`,
		// `VisualChangeTypeTag` and a bare `C350`, all holding a value the client itself checks by name
		// (MoveTaskIIR_c::PollStatus: `id.type != 50000` -> "id is not of a character!").
		//
		// The cost is not tidiness. TeamMemberRecord carried 0xC79C as `Field8` and ShopContentCodec's shop tag lived
		// on a DATABASE entity as `Shop.WireType`: with no single registry, nobody could see that these words are one
		// id namespace, so each new codec re-derived "some constant word here" from its own capture. One name, one
		// place, one test.
		[Fact]
		public void EntityClassWordComesFromEntityClass_NotAHandRolledLiteral()
		{
			// Faolan.Core.Database is in scope because that is where two of them were hiding: the guild and shop class
			// words were `Guild.WireType` / `Shop.WireType` on the EF entities, a layer that has no business owning a
			// wire constant and that the first version of this test did not look at.
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
				// EntityClass.cs IS the registry. (NpcSpawnScaffold.cs used to be exempted here as "verbatim
				// capture-derived bytes"; its baked array became a typed stream build long before #530 wave 5b
				// deleted the file, so the entry had been stale for a while - a reason to check an exemption is still
				// true, not to carry it forward.)
				var name = Path.GetFileName(file);
				if (name is "EntityClass.cs")
					continue;

				var lines = File.ReadAllLines(file);
				for (var i = 0; i < lines.Length; i++)
				{
					var code = StringLiteral.Replace(LineComment.Replace(BlockComment.Replace(lines[i], ""), ""), "");
					if (code.Contains("EntityClass", StringComparison.Ordinal))
						continue; // it comes from EntityClass on this line - that IS the convention

					if (EntityClassLiteral.IsMatch(code))
						offenders.Add($"{name}:{i + 1}  {code.Trim()}");
				}
			}

			Assert.True(offenders.Count == 0, "entity object-class ids must come from EntityClass (Character = 0xC350, LootBag = 0xC87A, ...):\n  " + string.Join("\n  ", offenders));
		}

		// (4) The count-framed LIST word comes from ListFrame, never a hand-rolled 0x3F1. This word had ELEVEN local
		// names and a pile of raw literals, and that scattering is exactly why it kept getting re-mistaken for a magic
		// constant: 0x7E2 is just ListFrame.ForCount(1), and single-element lists are the common case in captures.
		// Three subsystems shipped it as a "constant" before anyone noticed (resurrection spots #372, the AH search
		// head, and SendMail #378 - still open). One name, one place, one test.
		//
		// Allowed: ListFrame.cs itself. (NpcSpawnScaffold.cs used to be exempted as a "verbatim capture-derived byte
		// template"; its baked array had already become a typed stream build, and #530 wave 5b deleted the file - the
		// exemption was stale before it was removed, which is the reason to re-check one rather than carry it.)
		[Fact]
		public void CountFramedListWordComesFromListFrame_NotAHandRolled0x3F1()
		{
			// Codecs/ is not enough: the character-list framing lives in the PlayerAgent listener and the guild lists
			// in CSPlayerAgent, both of which open-coded it (as decimal 1009 and a 14th local name) while this test
			// scanned only Codecs/ and passed. A wire-shape rule has to cover everywhere the wire is written.
			//
			// Faolan.Core joined the roots when ListFrame moved there (#530): the registry itself is allowlisted by
			// filename, so without this root the one assembly that OWNS the rule would be the one place nothing
			// checked it - and SpanReader/FaolanStream, which sit beside it, are exactly where a stray framing word
			// would now be written.
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
						continue; // it comes from ListFrame on this line - that IS the convention

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
				Assert.True(f.FieldType == typeof(GameServer0X2000Opcodes[]), $"{t.Name}.HandledTypes must be GameServer0X2000Opcodes[], was {f.FieldType.Name}");

				var values = (GameServer0X2000Opcodes[])f.GetValue(null)!;
				Assert.True(values.Length > 1, $"{t.Name}.HandledTypes has {values.Length} entries - use `const uint Opcode` for a single type");
				Assert.True(values.Distinct().Count() == values.Length, $"{t.Name}.HandledTypes contains duplicates");
				Assert.All(values, v => Assert.True(Enum.IsDefined(v), $"{t.Name}.HandledTypes has an undefined member"));
			}

			// Guards the test itself: if nothing declares HandledTypes the multi-type case silently stopped being covered.
			Assert.True(declaring.Count >= 3, $"only {declaring.Count} codecs declare HandledTypes - the reflection filter is probably broken");
		}

		// (3) The one that actually matters: an IIR opcode never appears as a bare hex literal in codec CODE. If it
		// did, `grep GameServer0X2000Opcodes.<Type>` would miss that emitter and the coverage matrix could not
		// attribute it. Comments may still spell the hex for readability - they are stripped before scanning.
		[Fact]
		public void NoCodecSourceContainsAMagicIirOpcodeLiteral()
		{
			// Scans the CODEC layer AND its consumers. It used to cover Codecs/ only, and the gap showed: the
			// snapshot opcode 0x737A6DF9 has had an enum member (FullPlayerCharacterIIR) all along, and Codecs/ was
			// clean, but 12 bare literals had accumulated in the server, the importer and the tests - exactly the
			// emitters `grep GameServer0X2000Opcodes.<Type>` needs to find. Mirrors the ListFrame rule below, which
			// already spanned both trees.
			string[] roots = [TestPaths.Repo("Faolan.Core.Network", "Codecs"), TestPaths.Repo("Faolan"), TestPaths.Repo("Faolan.Importer")];
			var defined = Enum.GetValues<GameServer0X2000Opcodes>().ToHashSet();
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
					if (code.Contains("GameServer0X2000Opcodes", StringComparison.Ordinal))
						continue; // the value comes from the enum on this line - that IS the convention

					foreach (Match m in HexLiteral.Matches(code))
					{
						var value = Convert.ToUInt32(m.Value, 16);
						if (defined.Contains((GameServer0X2000Opcodes)value))
							offenders.Add($"{Path.GetFileName(file)}:{i + 1}  {m.Value} == GameServer0X2000Opcodes.{(GameServer0X2000Opcodes)value}");
					}
				}
			}

			Assert.True(offenders.Count == 0, "IIR opcodes must come from GameServer0X2000Opcodes, not a hex literal:\n  " + string.Join("\n  ", offenders));
		}

		// (2) The pre-#317 name must not reappear on a static const. (Instance members named SubOpcode - the
		// WorldObject column, WorldObjectIIR.SubOpcode - are a different thing and are allowed.)
		[Fact]
		public void NoStaticConstantIsNamedSubOpcode()
		{
			var bad = (from t in CodecTypes from f in Fields(t) where f.Name.EndsWith("SubOpcode", StringComparison.Ordinal) select $"{t.Name}.{f.Name}").ToList();

			Assert.True(bad.Count == 0, "Static opcode constants are named `Opcode`, not `SubOpcode`:\n  " + string.Join("\n  ", bad));
		}

		// (1) Every const named *Opcode carries a real GameServer0X2000Opcodes value - never a hand-typed hash.
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
				if (!Enum.IsDefined((GameServer0X2000Opcodes)value))
					bad.Add($"{t.Name}.{f.Name} = 0x{value:X8} is not a defined GameServer0X2000Opcodes member");
			}

			Assert.True(bad.Count == 0, "Opcode constants must be (uint)GameServer0X2000Opcodes.<Type>:\n  " + string.Join("\n  ", bad));
			Assert.True(checkedCount >= 15, $"only {checkedCount} Opcode constants found - the reflection filter is probably broken");
		}

		private static IEnumerable<FieldInfo> Fields(Type t)
		{
			return t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
		}
	}
}
