using System;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Spawn;
using Xunit;

namespace Faolan.Tests
{
	// Locks the scaffold <-> DB-rows mapping: extracting a captured spawn into CharacterAttribute / CharacterSkill /
	// CharacterScaffoldPair rows and regenerating from those rows reproduces the row-carried content byte-for-byte.
	// The pre-IIR frame + EntryTableX are NOT stored - they're reconstructed from Character.Class (FreshPreFrame) - so
	// a capture round-trips to its CONSTRUCTED form: the buff records + transient frame islands drop out.
	public class PlayerSpawnScaffoldRowsTests
	{
		// Buff records are session state, not character data: rows from a buffed capture regenerate the same
		// spawn WITHOUT the buff records (= the parsed scaffold with BuffRecords cleared).
		[Fact]
		public void BuffedSpawn_RoundTripsToItsUnbuffedConstructedForm()
		{
			// Hoxxling is a Herald of Xotli; his buffed capture regenerates WITHOUT the buff records (transient) and
			// with the frame reconstructed from his class - i.e. its constructed form.
			var body = Golden("SpawnHoxxlingBuffed");
			Assert.Equal(Convert.ToHexString(ConstructedForm(body, ConanClass.HeraldOfXotli)), Convert.ToHexString(RoundTripThroughRows(body, ConanClass.HeraldOfXotli)));
		}

		// The row-carried content (entry table / feat id-list / pair lists) + IIR still regenerate byte-exact; only the
		// pre-IIR frame is now the constructed canonical form (FreshPreFrame(class)). Dikketest's captured frame already
		// equals FreshPreFrame(Barbarian), so his case is still byte-identical to the raw capture.
		[Theory, InlineData("SpawnDikketestLogin", ConanClass.Barbarian), InlineData("SpawnNecroKingsCastle", ConanClass.Necromancer), InlineData("SpawnDemoRandomActions", ConanClass.Demonologist)]
		public void ExtractThenApply_RegeneratesTheConstructedForm(string name, ConanClass @class)
		{
			var body = Golden(name);

			Assert.Equal(Convert.ToHexString(ConstructedForm(body, @class)), Convert.ToHexString(RoundTripThroughRows(body, @class)));
		}

		// The unlocked FEAT list (spawn pre-IIR) extracts to CharacterFeat rows in wire/train order - a DIFFERENT
		// region + id-space than the post-IIR skill scaffold (CharacterSkill). NecroL80 (L80 Necro) carries 25 feats;
		// the ids + order are locked to the capture (tools/capture/decode_unlocked_feats.py). Feats and skills
		// share NO ids.
		[Fact]
		public void ExtractTo_ReadsTheUnlockedFeatList_InTrainOrder()
		{
			uint[] expected =
			[
				80004, 82704, 410604, 80200, 80104, 81603, 84004, 84104, 410204, 81804, 82801, 410104, 81702, 80401, 82904, 83401, 85700, 410901, 80800, 80902, 82202, 83000, 85400, 81001, 82500
			];

			Assert.True(PlayerSpawnScaffoldCodec.TryParse(Golden("SpawnNecroKingsCastle"), out var scaffold, out _));
			var character = new Character
			{
				Id = 0x0200000A,
				Class = ConanClass.Necromancer
			};
			PlayerSpawnScaffoldRows.ExtractTo(character, scaffold);

			Assert.Equal(expected, character.Feats.OrderBy(f => f.Sequence).Select(f => f.FeatId).ToArray());
			Assert.Empty(character.Feats.Select(f => f.FeatId).Intersect(character.Skills.Select(s => s.SkillId))); // feats != skills
		}

		// A fresh character (and the fresh-scaffold Dikketest capture) carries no trained feats: the pre-IIR feat
		// list is the bare 0x3F1 marker, so the frame stays byte-identical to the old constant-239 fresh frame.
		[Fact]
		public void FreshCharacter_HasNoFeats_AndAnEmptyFeatFrame()
		{
			var character = new Character
			{
				Id = 0x0200000B,
				Class = ConanClass.Necromancer
			};
			Assert.True(PlayerSpawnScaffoldRows.TrySeedFreshRows(character));
			Assert.Empty(character.Feats);

			Assert.Equal(239, PlayerSpawnScaffoldRows.FreshPreFrame(ConanClass.Necromancer).ByteCount);
			Assert.Empty(PlayerSpawnScaffoldRows.ExtractPreFrameFeats(PlayerSpawnScaffoldRows.FreshPreFrame(ConanClass.Necromancer)));
		}

		[Fact]
		public void FreshCloser_Assassin_Is0x3F10()
		{
			var character = new Character
			{
				Id = 0x02000009,
				Class = ConanClass.Assassin
			};
			Assert.True(PlayerSpawnScaffoldRows.TrySeedFreshRows(character));

			var closer = character.Attributes.OrderBy(a => a.Sequence).Last();
			Assert.Equal(0x3F10, closer.Tag); // 14 feats -> (14+2)*0x3F1
			Assert.Equal(300u, closer.Value);
		}

		// #269 (decoded over 117 retail player self-spawns, zero violations): the closer tag is NOT a per-class
		// constant - it is a COUNT marker, tag = (feat/skill IdList length + 2) * 0x3F1. So a fresh character's closer
		// tag must equal that formula for EVERY class (this is why a mismatched closer crashes - #250). Includes the
		// Assassin (14 feats -> 0x3F10), whose earlier hardcoded 0x56B6 guess was wrong.
		[Theory, InlineData(ConanClass.Guardian), InlineData(ConanClass.Conqueror), InlineData(ConanClass.DarkTemplar), InlineData(ConanClass.Barbarian), InlineData(ConanClass.Assassin), InlineData(ConanClass.Ranger),
		 InlineData(ConanClass.BearShaman), InlineData(ConanClass.PriestOfMitra), InlineData(ConanClass.TempestOfSet), InlineData(ConanClass.Necromancer), InlineData(ConanClass.Demonologist), InlineData(ConanClass.HeraldOfXotli)]
		public void FreshCloser_TagIsDerivedFromFeatCount(ConanClass @class)
		{
			var character = new Character
			{
				Id = 0x02000008,
				Class = @class
			};
			Assert.True(PlayerSpawnScaffoldRows.TrySeedFreshRows(character));

			var closer = character.Attributes.OrderBy(a => a.Sequence).Last();
			var expectedTag = (ushort)((character.Skills.Count + 2) * 0x3F1);
			Assert.Equal(expectedTag, closer.Tag);
		}

		// FreshPreFrame(feats) and ExtractPreFrameFeats are exact inverses, and the frame grows by 4 bytes per feat
		// (126-byte head incl. the marker + 4*count + 113-byte tail).
		[Fact]
		public void FreshPreFrame_And_ExtractPreFrameFeats_RoundTrip()
		{
			uint[] feats = [110004, 110100, 111304, 111402, 110300, 112700]; // hakotep (hox) unlocked set
			var frame = PlayerSpawnScaffoldRows.FreshPreFrame(ConanClass.HeraldOfXotli, 0, feats);

			Assert.Equal(126 + 4 * feats.Length + 113, frame.ByteCount);
			Assert.Equal(feats, PlayerSpawnScaffoldRows.ExtractPreFrameFeats(frame).ToArray());
		}

		// The vanity hidden-slots mask rides PreFrame[18..22] big-endian (bit = base equip slot id). Byte-locked to the
		// vanity login dumps: hiding Head (slot 2) => 00000004, Chest (slot 5) => 00000020, and a fresh frame => 0.
		[Theory, InlineData(0u, "00000000"), InlineData(1u << 2, "00000004"), InlineData(1u << 5, "00000020"), InlineData((1u << 5) | (1u << 31), "80000020")]
		// Head (slot 2), from vanity-head-selected
		// Chest (slot 5), from vanity-chest-selected
		// chest + global master (slot 31)
		public void FreshPreFrame_WritesVanityMaskBigEndian(uint mask, string expectedHex)
		{
			var frame = PlayerSpawnScaffoldRows.FreshPreFrame(ConanClass.Assassin, mask);

			// Byte offset 18, spelled out rather than taken from the codec: this is the assertion that pins scaffold
			// word 4 to the place #336 found the mask by a different route, so reading the index back off
			// PlayerSpawnPreFrame would make it self-referential.
			Assert.Equal(expectedHex, Convert.ToHexString(PlayerSpawnPreFrameCodec.Build(frame).AsSpan(18, 4).ToArray()).ToLowerInvariant());
		}

		// #239: the AoC client derives the displayed profession from the character's feat tree, whose first
		// (profession) feat = class enum * 100. A fresh character must carry ITS class's profession feat, not the
		// Barbarian default (1800) that every fresh char used to inherit from Dikketest. Verified against retail
		// self-spawns for 8 classes.
		[Theory, InlineData(ConanClass.Necromancer, 4100u), InlineData(ConanClass.Guardian, 2000u), InlineData(ConanClass.BearShaman, 2900u), InlineData(ConanClass.Assassin, 3400u), InlineData(ConanClass.Barbarian, 1800u),
		 InlineData(ConanClass.Conqueror, 2200u), InlineData(ConanClass.HeraldOfXotli, 4300u), InlineData(ConanClass.Demonologist, 4400u), InlineData(ConanClass.DarkTemplar, 3100u)]
		// fallback (no L1 capture) - profession feat still correct
		public void FreshRows_CarryTheClassProfessionFeat(ConanClass @class, uint professionFeat)
		{
			var character = new Character
			{
				Id = 0x02000007,
				Class = @class
			};
			Assert.True(PlayerSpawnScaffoldRows.TrySeedFreshRows(character));

			var feats = character.Skills.Select(s => s.SkillId).OrderBy(id => id).ToArray();
			Assert.Equal(professionFeat, feats.First()); // profession feat = class * 100, and it sorts first
			Assert.Equal((uint)@class * 100, feats.First());
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
				Class = ConanClass.Barbarian,
				Body = new CharacterBody()
			};
			foreach (var (morph, raw) in new (string, uint)[] { ("arm", 59), ("arse", 14), ("bosom", 30), ("chest", 59), ("leg", 59), ("stomach", 14), ("thigh", 59) })
				// raw = Dikketest's captured wire values; decode as the read side does before storing.
				Assert.True(CharacterMorphs.TryApply(character.Body, morph, (int)(raw >> 1) ^ -(int)(raw & 1)));

			Assert.True(PlayerSpawnScaffoldRows.TrySeedFreshRows(character));
			Assert.False(PlayerSpawnScaffoldRows.TrySeedFreshRows(character)); // idempotent
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
			var character = new Character
			{
				Id = 0x02000006,
				Class = ConanClass.Barbarian
			};
			Assert.True(PlayerSpawnScaffoldRows.TrySeedFreshRows(character));

			Assert.Equal(7, character.Attributes.Count);
			var closer = character.Attributes.Single(a => a.Sequence == 6);
			Assert.Equal(7u, closer.EntryIndex);
			Assert.Equal(0x56B6, closer.Tag);
			Assert.Equal(300u, closer.Value);
		}

		[Fact]
		public void Template_ExtractThenApply_RegeneratesTheConstructedForm()
		{
			var body = Support.Golden.Bytes("SpawnStrieTemplate"); // Strie is an Assassin

			Assert.Equal(Convert.ToHexString(ConstructedForm(body, ConanClass.Assassin)), Convert.ToHexString(RoundTripThroughRows(body, ConanClass.Assassin)));
		}

		[Fact]
		public void TryApplyFrom_WithoutRows_ReturnsFalse()
		{
			Assert.True(PlayerSpawnScaffoldCodec.TryParse(Golden("SpawnDikketestLogin"), out var scaffold, out _));

			Assert.False(PlayerSpawnScaffoldRows.TryApplyFrom(new Character
			{
				Id = 1
			}, scaffold));
			Assert.False(PlayerSpawnScaffoldRows.TryApplyFrom(null!, scaffold));
		}

		// The frame + EntryTableX are no longer stored - TryApplyFrom reconstructs them from the character's class. So
		// a captured spawn round-trips to its CONSTRUCTED form: identical rows (entry table / feats / pairs) + IIR, with
		// the frame regenerated as FreshPreFrame(class), EntryTableX 0xB7, and transient buff/session records dropped.
		private static byte[] ConstructedForm(byte[] body, ConanClass @class)
		{
			Assert.True(PlayerSpawnScaffoldCodec.TryParse(body, out var s, out _));
			// The frame is reconstructed, but the unlocked FEAT list it carries IS character data and survives the row
			// round-trip - so the constructed form keeps the captured feats (spliced into the canonical frame).
			var feats = PlayerSpawnScaffoldRows.ExtractPreFrameFeats(s.Frame);
			s.Frame = PlayerSpawnScaffoldRows.FreshPreFrame(@class, 0, feats);
			s.EntryTableX = 0xB7;
			s.BuffRecords.Clear();
			return PlayerSpawnScaffoldCodec.Generate(s);
		}

		private static byte[] Golden(string name)
		{
			return File.ReadAllBytes(Path.Combine(GoldenDir(), name + ".bin"));
		}

		private static string GoldenDir([CallerFilePath] string thisFile = "")
		{
			return Path.Combine(Path.GetDirectoryName(thisFile)!, "Golden");
		}

		private static byte[] RoundTripThroughRows(byte[] body, ConanClass @class)
		{
			Assert.True(PlayerSpawnScaffoldCodec.TryParse(body, out var scaffold, out var error), $"parse failed: {error}");

			var character = new Character
			{
				Id = 0x02000005,
				Class = @class
			};
			PlayerSpawnScaffoldRows.ExtractTo(character, scaffold);

			Assert.True(PlayerSpawnScaffoldCodec.TryParse(body, out var rebuilt, out _)); // fresh instance, then rows overwrite
			Assert.True(PlayerSpawnScaffoldRows.TryApplyFrom(character, rebuilt));
			return PlayerSpawnScaffoldCodec.Generate(rebuilt);
		}
	}
}
