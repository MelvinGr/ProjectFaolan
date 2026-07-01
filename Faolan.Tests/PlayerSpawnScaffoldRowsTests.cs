using Faolan.Core.Spawn;
using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Faolan.Core.Database.Tables;
using Faolan.GameServer.Spawn;
using Xunit;

namespace Faolan.Tests
{
	// Locks the scaffold <-> DB-rows mapping: extracting a captured spawn into CharacterAttribute /
	// CharacterSkill / CharacterScaffoldPair / CharacterSpawnFrame rows and regenerating from those rows must
	// reproduce the captured body byte-for-byte (the DB-generated player spawn oracle). Buff records are
	// transient and not stored, so a buffed capture round-trips to its unbuffed form.
	public class PlayerSpawnScaffoldRowsTests
	{
		// Buff records are session state, not character data: rows from a buffed capture regenerate the same
		// spawn WITHOUT the buff records (= the parsed scaffold with BuffRecords cleared).
		[Fact]
		public void BuffedSpawn_RoundTripsToItsUnbuffedForm()
		{
			var body = Golden("SpawnHoxxlingBuffed");
			Assert.True(PlayerSpawnScaffoldCodec.TryParse(body, out var expected, out _));
			expected.BuffRecords.Clear();

			Assert.Equal(Convert.ToHexString(PlayerSpawnScaffoldCodec.Generate(expected)), Convert.ToHexString(RoundTripThroughRows(body)));
		}

		[Theory, InlineData("SpawnDikketestLogin"), InlineData("SpawnNecroL80KingsCastle"), InlineData("SpawnDemoL80RandomActions")]
		public void ExtractThenApply_RegeneratesByteIdentically(string name)
		{
			var body = Golden(name);

			Assert.Equal(Convert.ToHexString(body), Convert.ToHexString(RoundTripThroughRows(body)));
		}

		// The fresh-character defaults are Dikketest's extracted scaffold, with the morph entries now GENERATED
		// from the character's editor sliders: seeding a character that carries Dikketest's CharacterBody (the
		// raw editor stream from his create capture, zigzag-decoded by CharacterMorphs.TryApply) and generating
		// over his transform/CharacterIIR must reproduce his captured spawn byte-for-byte.
		[Fact]
		public void FreshRows_OverDikketestBody_AreByteIdenticalToTheCapture()
		{
			var body = Golden("SpawnDikketestLogin");
			Assert.True(PlayerSpawnScaffoldCodec.TryParse(body, out var scaffold, out _));

			var character = new Character
			{
				Id = 0x02000005,
				// Dikketest is a Barbarian; his captured fresh feat list is the per-class Barbarian list (the
				// profession feat 1800 = Class.Barbarian * 100), so the fresh rows must be generated for that class
				// to reproduce his spawn byte-for-byte (#239 - the feat tree is the client's class source).
				Class = Faolan.Core.Enums.Class.Barbarian,
				Body = new CharacterBody()
			};
			foreach (var (morph, raw) in new (string, uint)[] { ("arm", 59), ("arse", 14), ("bosom", 30), ("chest", 59), ("leg", 59), ("stomach", 14), ("thigh", 59) })
				Assert.True(CharacterMorphs.TryApply(character.Body, morph, raw));

			Assert.True(PlayerSpawnScaffoldRows.EnsureFreshRows(character));
			Assert.False(PlayerSpawnScaffoldRows.EnsureFreshRows(character)); // idempotent
			Assert.True(PlayerSpawnScaffoldRows.TryApplyFrom(character, scaffold));

			Assert.Equal(Convert.ToHexString(body), Convert.ToHexString(PlayerSpawnScaffoldCodec.Generate(scaffold)));
		}

		// A character that never touched a slider (no CharacterBody) gets no morph entries at all: 6 weighted
		// attributes + the closer at idx 7 - the exact entry-table shape of strickenbla's captured spawn.
		[Fact]
		public void FreshRows_WithoutBody_HaveNoMorphEntries()
		{
			// The closer (#250/#269) is derived: value = archetype*100, tag = (feat count + 2)*0x3F1. A Barbarian has
			// 20 fresh feats -> tag (20+2)*0x3F1 = 0x56B6, value 300 (rogue = archetype 3).
			var character = new Character { Id = 0x02000006, Class = Faolan.Core.Enums.Class.Barbarian };
			Assert.True(PlayerSpawnScaffoldRows.EnsureFreshRows(character));

			Assert.Equal(7, character.Attributes.Count);
			var closer = character.Attributes.Single(a => a.Sequence == 6);
			Assert.Equal(7u, closer.StatId);
			Assert.Equal(0x56B6, closer.Tag);
			Assert.Equal(300u, closer.Value);
		}

		// #269 (decoded over 117 retail player self-spawns, zero violations): the closer tag is NOT a per-class
		// constant - it is a COUNT marker, tag = (feat/skill IdList length + 2) * 0x3F1. So a fresh character's closer
		// tag must equal that formula for EVERY class (this is why a mismatched closer crashes - #250). Includes the
		// Assassin (14 feats -> 0x3F10), whose earlier hardcoded 0x56B6 guess was wrong.
		[Theory]
		[InlineData(Faolan.Core.Enums.Class.Guardian)]
		[InlineData(Faolan.Core.Enums.Class.Conqueror)]
		[InlineData(Faolan.Core.Enums.Class.DarkTemplar)]
		[InlineData(Faolan.Core.Enums.Class.Barbarian)]
		[InlineData(Faolan.Core.Enums.Class.Assassin)]
		[InlineData(Faolan.Core.Enums.Class.Ranger)]
		[InlineData(Faolan.Core.Enums.Class.BearShaman)]
		[InlineData(Faolan.Core.Enums.Class.PriestOfMitra)]
		[InlineData(Faolan.Core.Enums.Class.TempestOfSet)]
		[InlineData(Faolan.Core.Enums.Class.Necromancer)]
		[InlineData(Faolan.Core.Enums.Class.Demonologist)]
		[InlineData(Faolan.Core.Enums.Class.HeraldOfXotli)]
		public void FreshCloser_TagIsDerivedFromFeatCount(Faolan.Core.Enums.Class @class)
		{
			var character = new Character { Id = 0x02000008, Class = @class };
			Assert.True(PlayerSpawnScaffoldRows.EnsureFreshRows(character));

			var closer = character.Attributes.OrderBy(a => a.Sequence).Last();
			var expectedTag = (ushort)((character.Skills.Count + 2) * 0x3F1);
			Assert.Equal(expectedTag, closer.Tag);
		}

		[Fact]
		public void FreshCloser_Assassin_Is0x3F10()
		{
			var character = new Character { Id = 0x02000009, Class = Faolan.Core.Enums.Class.Assassin };
			Assert.True(PlayerSpawnScaffoldRows.EnsureFreshRows(character));

			var closer = character.Attributes.OrderBy(a => a.Sequence).Last();
			Assert.Equal(0x3F10, closer.Tag);  // 14 feats -> (14+2)*0x3F1
			Assert.Equal(300u, closer.Value);
		}

		// #239: the AoC client derives the displayed profession from the character's feat tree, whose first
		// (profession) feat = class enum * 100. A fresh character must carry ITS class's profession feat, not the
		// Barbarian default (1800) that every fresh char used to inherit from Dikketest. Verified against retail
		// self-spawns for 8 classes.
		[Theory]
		[InlineData(Faolan.Core.Enums.Class.Necromancer, 4100u)]
		[InlineData(Faolan.Core.Enums.Class.Guardian, 2000u)]
		[InlineData(Faolan.Core.Enums.Class.BearShaman, 2900u)]
		[InlineData(Faolan.Core.Enums.Class.Assassin, 3400u)]
		[InlineData(Faolan.Core.Enums.Class.Barbarian, 1800u)]
		[InlineData(Faolan.Core.Enums.Class.Conqueror, 2200u)] // fallback (no L1 capture) - profession feat still correct
		[InlineData(Faolan.Core.Enums.Class.HeraldOfXotli, 4300u)]
		[InlineData(Faolan.Core.Enums.Class.Demonologist, 4400u)]
		[InlineData(Faolan.Core.Enums.Class.DarkTemplar, 3100u)]
		public void FreshRows_CarryTheClassProfessionFeat(Faolan.Core.Enums.Class @class, uint professionFeat)
		{
			var character = new Character { Id = 0x02000007, Class = @class };
			Assert.True(PlayerSpawnScaffoldRows.EnsureFreshRows(character));

			var feats = character.Skills.Select(s => s.SkillId).OrderBy(id => id).ToArray();
			Assert.Equal(professionFeat, feats.First()); // profession feat = class * 100, and it sorts first
			Assert.Equal((uint)@class * 100, feats.First());
		}

		[Fact]
		public void Template_ExtractThenApply_RegeneratesByteIdentically()
		{
			var body = Support.Golden.Bytes("SpawnStrieTemplate");

			Assert.Equal(Convert.ToHexString(body), Convert.ToHexString(RoundTripThroughRows(body)));
		}

		[Fact]
		public void TryApplyFrom_WithoutRows_ReturnsFalse()
		{
			Assert.True(PlayerSpawnScaffoldCodec.TryParse(Golden("SpawnDikketestLogin"), out var scaffold, out _));

			Assert.False(PlayerSpawnScaffoldRows.TryApplyFrom(new Character { Id = 1 }, scaffold));
			Assert.False(PlayerSpawnScaffoldRows.TryApplyFrom(null, scaffold));
		}

		private static byte[] Golden(string name)
		{
			return File.ReadAllBytes(Path.Combine(GoldenDir(), name + ".bin"));
		}

		private static string GoldenDir([CallerFilePath] string thisFile = "")
		{
			return Path.Combine(Path.GetDirectoryName(thisFile)!, "Golden");
		}

		private static byte[] RoundTripThroughRows(byte[] body)
		{
			Assert.True(PlayerSpawnScaffoldCodec.TryParse(body, out var scaffold, out var error), $"parse failed: {error}");

			var character = new Character { Id = 0x02000005 };
			PlayerSpawnScaffoldRows.ExtractTo(character, scaffold);

			Assert.True(PlayerSpawnScaffoldCodec.TryParse(body, out var rebuilt, out _)); // fresh instance, then rows overwrite
			Assert.True(PlayerSpawnScaffoldRows.TryApplyFrom(character, rebuilt));
			return PlayerSpawnScaffoldCodec.Generate(rebuilt);
		}
	}
}
