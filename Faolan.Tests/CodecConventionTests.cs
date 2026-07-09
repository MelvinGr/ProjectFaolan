using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
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
	// Codecs with NO opcode (sub-record helpers like CharacterStatBlock, StatList, SnapshotItemBuilder) are fine and
	// are deliberately not required to declare one - reflection cannot tell "emits an IIR" from "is a sub-record".
	public class CodecConventionTests
	{
		private const string CodecsNamespace = "Faolan.Core.Network.Codecs";

		// 8-hex-digit literals; IIR opcodes are 32-bit BKDR-31 hashes so they always appear in this form.
		private static readonly Regex HexLiteral = new(@"0x[0-9A-Fa-f]{8}\b");
		private static readonly Regex LineComment = new(@"//.*$", RegexOptions.Multiline);
		private static readonly Regex BlockComment = new(@"/\*.*?\*/", RegexOptions.Singleline);

		private static IEnumerable<Type> CodecTypes =>
			typeof(ICodec<>).Assembly.GetTypes()
				.Where(t => t.Namespace != null && t.Namespace.StartsWith(CodecsNamespace, StringComparison.Ordinal));

		private static IEnumerable<FieldInfo> Fields(Type t) =>
			t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);

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

			Assert.True(bad.Count == 0,
				"Opcode constants must be (uint)GameServer0X2000Opcodes.<Type>:\n  " + string.Join("\n  ", bad));
			Assert.True(checkedCount >= 15, $"only {checkedCount} Opcode constants found - the reflection filter is probably broken");
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
				Assert.True(f.FieldType == typeof(GameServer0X2000Opcodes[]),
					$"{t.Name}.HandledTypes must be GameServer0X2000Opcodes[], was {f.FieldType.Name}");

				var values = (GameServer0X2000Opcodes[])f.GetValue(null)!;
				Assert.True(values.Length > 1, $"{t.Name}.HandledTypes has {values.Length} entries - use `const uint Opcode` for a single type");
				Assert.True(values.Distinct().Count() == values.Length, $"{t.Name}.HandledTypes contains duplicates");
				Assert.All(values, v => Assert.True(Enum.IsDefined(v), $"{t.Name}.HandledTypes has an undefined member"));
			}

			// Guards the test itself: if nothing declares HandledTypes the multi-type case silently stopped being covered.
			Assert.True(declaring.Count >= 3, $"only {declaring.Count} codecs declare HandledTypes - the reflection filter is probably broken");
		}

		// (2) The pre-#317 name must not reappear on a static const. (Instance members named SubOpcode - the
		// WorldObject column, DynelFullUpdateCodec.Parsed.SubOpcode - are a different thing and are allowed.)
		[Fact]
		public void NoStaticConstantIsNamedSubOpcode()
		{
			var bad = (from t in CodecTypes
				from f in Fields(t)
				where f.Name.EndsWith("SubOpcode", StringComparison.Ordinal)
				select $"{t.Name}.{f.Name}").ToList();

			Assert.True(bad.Count == 0, "Static opcode constants are named `Opcode`, not `SubOpcode`:\n  " + string.Join("\n  ", bad));
		}

		// (3) The one that actually matters: an IIR opcode never appears as a bare hex literal in codec CODE. If it
		// did, `grep GameServer0X2000Opcodes.<Type>` would miss that emitter and the coverage matrix could not
		// attribute it. Comments may still spell the hex for readability - they are stripped before scanning.
		[Fact]
		public void NoCodecSourceContainsAMagicIirOpcodeLiteral()
		{
			var codecDir = TestPaths.Repo("Faolan.Core.Network", "Codecs");
			var defined = Enum.GetValues<GameServer0X2000Opcodes>().ToHashSet();
			var offenders = new List<string>();

			foreach (var file in Directory.EnumerateFiles(codecDir, "*.cs", SearchOption.AllDirectories))
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

			Assert.True(offenders.Count == 0,
				"IIR opcodes must come from GameServer0X2000Opcodes, not a hex literal:\n  " + string.Join("\n  ", offenders));
		}
	}
}
