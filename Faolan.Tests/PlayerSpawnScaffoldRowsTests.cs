using System;
using System.Linq;
using Faolan.Core;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Locks the scaffold <-> character mapping: extracting a captured spawn into the character's rows -
	// CharacterSkill / CharacterScaffoldPair / CharacterFeat, plus the appearance model (CharacterBody sliders +
	// CharacterBodyResource refs) - and regenerating from those reproduces the captured content byte-for-byte. The
	// pre-IIR frame is not stored - it is reconstructed from Character.Class (FreshPreFrame) - so a capture
	// round-trips to its constructed form: the buff records + transient frame islands drop out.
	//
	// The entry table is not stored either. It is derived from the appearance model on every send, so these
	// round-trips also gate the derivation: a captured tag has to survive being turned into a resource ref and
	// resolved back. Two of the five captures cannot make it all the way - see
	// EditedAppearance_RoundTripsExceptItsRetailSlots.
	public class PlayerSpawnScaffoldRowsTests
	{
		// Buff records are session state, not character data: rows from a buffed capture regenerate the same
		// spawn without the buff records (= the parsed scaffold with BuffRecords cleared).
		[Fact]
		public void BuffedSpawn_RoundTripsToItsUnbuffedConstructedForm()
		{
			// Hoxxling is a Herald of Xotli; his buffed capture regenerates without the buff records (transient) and
			// with the frame reconstructed from his class - i.e. its constructed form. He is also the widest appearance
			// case in the corpus: 6 weighted refs + 27 morph sliders, all of which have to survive the derivation.
			var body = Golden("SpawnHeraldBuffed");
			Assert.Equal(Convert.ToHexString(ConstructedForm(body, ConanClass.HeraldOfXotli)), Convert.ToHexString(RoundTripThroughRows(body, ConanClass.HeraldOfXotli)));
		}

		// The character-carried content (appearance / feat id-list / pair lists) + IIR still regenerate byte-exact; only
		// the pre-IIR frame is the constructed canonical form (FreshPreFrame(class)). Dikketest's captured frame already
		// equals FreshPreFrame(Barbarian), so his case is still byte-identical to the raw capture.
		[Theory, InlineData("SpawnDikketestLogin", ConanClass.Barbarian), InlineData("SpawnAssassinTemplate", ConanClass.Assassin)]
		public void ExtractThenApply_RegeneratesTheConstructedForm(string name, ConanClass @class)
		{
			var body = Golden(name);

			Assert.Equal(Convert.ToHexString(ConstructedForm(body, @class)), Convert.ToHexString(RoundTripThroughRows(body, @class)));
		}

		// The two captures that do not come back byte-identical, and exactly how far they get.
		//
		// Retail's entry-table index is a per-character slot id that survives appearance edits, not a position: the
		// Necro capture runs 2..15 (slot 1 vacated) and the Demo one runs 4..13 then 24,23 - a hair and a beard
		// re-chosen later, allocated fresh slots and appended after the morphs. Nothing in the character's data
		// derives those numbers, and nothing should: every character we create spawns with 1..n and renders.
		//
		// So the claim these two can support is the strong half of it - every captured (tag, value) survives the trip
		// through CharacterBody + CharacterBodyResources bit-exact, and everything outside the entry table is
		// byte-identical - with the slot numbering explicitly asserted to be the only thing lost.
		[Theory, InlineData("SpawnNecroKingsCastle", ConanClass.Necromancer), InlineData("SpawnDemoRandomActions", ConanClass.Demonologist)]
		public void EditedAppearance_RoundTripsExceptItsRetailSlots(string name, ConanClass @class)
		{
			var body = Golden(name);
			var captured = SimpleCharFullUpdateCodec.Parse(ConstructedForm(body, @class));
			var rebuilt = SimpleCharFullUpdateCodec.Parse(RoundTripThroughRows(body, @class));

			// Every element's payload survives - same (key, value) pairs, same float bits.
			Assert.Equal(PlayerSlot(captured).Appearance.OrderBy(e => e.MorphId).Select(e => (e.MorphId, e.Value)), PlayerSlot(rebuilt).Appearance.OrderBy(e => e.MorphId).Select(e => (e.MorphId, e.Value)));

			// ...and the slot numbering is what does not, which is the whole of the divergence.
			Assert.NotEqual(PlayerSlot(captured).Appearance.Select(e => e.Word2), PlayerSlot(rebuilt).Appearance.Select(e => e.Word2));
			Assert.Equal(Enumerable.Range(1, PlayerSlot(rebuilt).Appearance.Count).Select(i => (uint)i), PlayerSlot(rebuilt).Appearance.Select(e => e.Word2));

			// Everything else in the record is byte-identical: swap the appearance tables and the bytes match exactly.
			PlayerSlot(captured).Appearance.Clear();
			PlayerSlot(captured).Appearance.AddRange(PlayerSlot(rebuilt).Appearance);
			Assert.Equal(Convert.ToHexString(SimpleCharFullUpdateCodec.Build(captured)), Convert.ToHexString(SimpleCharFullUpdateCodec.Build(rebuilt)));
		}

		// The scaffold's class-progression entry is the character's level, and this is the measurement that says so:
		// five captured player spawns across five classes and four levels (two fresh, Hoxxling mid-Tortage at 5, the
		// Necro and Demo at 80), each carrying exactly one id from its class's 80-long progression run, at exactly the
		// rank its character's level implies. `FeatIdMaps` resolves that entry to the level's `<Class> Class Feat NNN`
		// template - the class/level attribute base - which is where the client gets the five attributes and the
		// MaxHealth it draws.
		//
		// So it is a per-character fact and cannot be shipped as class content: granting every dinged character the
		// captured L80's entry makes a level-2 Necromancer holding 85279 come back from its first relog rendering a
		// level-80 one - Str 71 / Int 231 / Con 61, health max 984. It is derived per character
		// (ClassProgressionIdFor), and this pins the read side that proves the derivation.
		[Theory, InlineData("SpawnDikketestLogin", 1, 124400u), InlineData("SpawnAssassinTemplate", 1, 160000u), InlineData("SpawnHeraldBuffed", 5, 114804u),
		 InlineData("SpawnNecroKingsCastle", 80, 85279u), InlineData("SpawnDemoRandomActions", 80, 104579u)]
		public void ClassProgressionEntry_IsTheCharactersLevel(string name, int level, uint expectedId)
		{
			var scaffold = SimpleCharFullUpdateCodec.Parse(Record(Golden(name)));
			var ids = PlayerSlot(scaffold).Skills.Select(e => e.Id).ToArray();

			// Exactly one progression id in the whole list - a character is at one level, and a second entry would
			// mean the run boundaries are wrong.
			Assert.Equal([expectedId], ids.Where(PlayerSpawnScaffoldRows.IsClassProgressionId).ToArray());
			Assert.Equal((ushort)level, PlayerSpawnScaffoldRows.ClassProgressionLevelOf(ids));
		}

		// Derive and read back are exact inverses over every class at every level - the whole 12 x 80 grid, so no
		// class's run can be transposed onto another's without this failing. Level 0 and 81 clamp into the run rather
		// than walking off either end of it into a neighbouring class's band.
		[Theory, InlineData(ConanClass.Guardian), InlineData(ConanClass.Conqueror), InlineData(ConanClass.DarkTemplar), InlineData(ConanClass.Barbarian), InlineData(ConanClass.Assassin), InlineData(ConanClass.Ranger),
		 InlineData(ConanClass.BearShaman), InlineData(ConanClass.PriestOfMitra), InlineData(ConanClass.TempestOfSet), InlineData(ConanClass.Necromancer), InlineData(ConanClass.Demonologist), InlineData(ConanClass.HeraldOfXotli)]
		public void ClassProgressionId_RoundTripsAtEveryLevel(ConanClass @class)
		{
			for (ushort level = 1; level <= 80; level++)
			{
				var id = PlayerSpawnScaffoldRows.ClassProgressionIdFor(@class, level);
				Assert.NotNull(id);
				Assert.True(PlayerSpawnScaffoldRows.IsClassProgressionId(id.Value));
				Assert.Equal(level, PlayerSpawnScaffoldRows.ClassProgressionLevelOf([id.Value]));
			}

			Assert.Equal(PlayerSpawnScaffoldRows.ClassProgressionIdFor(@class, 1), PlayerSpawnScaffoldRows.ClassProgressionIdFor(@class, 0));
			Assert.Equal(PlayerSpawnScaffoldRows.ClassProgressionIdFor(@class, 80), PlayerSpawnScaffoldRows.ClassProgressionIdFor(@class, 81));

			// And the class's fresh rows carry its own level-1 entry and no other - the anchor this table was written
			// down from, so the table cannot drift away from the seeded rows without this failing.
			var character = new Character
			{
				Id = 0x0200000C,
				Class = @class
			};
			Assert.True(PlayerSpawnScaffoldRows.TrySeedFreshRows(character));

			var seeded = character.Skills.Select(s => s.SkillId).ToArray();
			Assert.Equal([PlayerSpawnScaffoldRows.ClassProgressionIdFor(@class, 1)], seeded.Where(PlayerSpawnScaffoldRows.IsClassProgressionId).Cast<uint?>().ToArray());
		}

		// The captured scaffolds carry the captured player's own tradeskill professions, and those are trained
		// (`.tradeskill` -> AddCharacterFeats), not granted by a class. Handing them over with a level-up grant is
		// what makes a level-2 Necromancer relog as a Grandmaster weapon- and armoursmith with six Master gathering
		// skills. `FullSkillIdsFor` is the class's content, so no profession rung may appear in it - and neither may a
		// progression entry, for the same reason.
		//
		// The ids are the profession ladders' own, spelled out rather than resolved through FeatPrereqs: this is a
		// list that must not grow, so it states what is excluded (Necromancer: Weaponsmith/Armorsmith Grandmaster,
		// Woodcutting/Mining/Prospecting/Stonecutting Master, Weaver Grandmaster, Skinner Master; Demonologist:
		// Alchemy Artisan, Gemcutter Novice and six more at its own ranks; Assassin: Mining Apprentice).
		[Theory, InlineData(ConanClass.Necromancer), InlineData(ConanClass.Demonologist), InlineData(ConanClass.Assassin), InlineData(ConanClass.HeraldOfXotli)]
		public void FullSkillScaffold_CarriesNoCapturedCharactersOwnState(ConanClass @class)
		{
			uint[] professionRungs =
			[
				321202, 321502, 321701, 322402, 323201, 323205, 323301, 323300, 323305, 323402, 323405, 323501, 323505, 327200, 327402, 327500, 327505
			];

			var full = PlayerSpawnScaffoldRows.FullSkillIdsFor(@class);
			Assert.NotNull(full);

			Assert.DoesNotContain(full, PlayerSpawnScaffoldRows.IsClassProgressionId);
			Assert.Empty(full.Intersect(professionRungs));

			// It is still the class's scaffold, not an empty list - the castability stand-in it exists for is untouched,
			// and the ids stay ascending because the wire requires it.
			Assert.NotEmpty(full);
			Assert.Equal(full.OrderBy(id => id), full);
		}

		// The unlocked feat list (spawn pre-IIR) extracts to CharacterFeat rows in wire/train order - a different
		// region + id-space than the post-IIR skill scaffold (CharacterSkill). NecroL80 (L80 Necro) carries 25 feats;
		// the ids + order are locked to the capture (tools/capture/decode_unlocked_feats.py). Feats and skills
		// share no ids.
		[Fact]
		public void ExtractTo_ReadsTheUnlockedFeatList_InTrainOrder()
		{
			uint[] expected =
			[
				80004, 82704, 410604, 80200, 80104, 81603, 84004, 84104, 410204, 81804, 82801, 410104, 81702, 80401, 82904, 83401, 85700, 410901, 80800, 80902, 82202, 83000, 85400, 81001, 82500
			];

			var scaffold = SimpleCharFullUpdateCodec.Parse(Record(Golden("SpawnNecroKingsCastle")));
			var character = new Character
			{
				Id = 0x0200000A,
				Class = ConanClass.Necromancer
			};
			PlayerSpawnScaffoldRows.ExtractTo(character, scaffold, MorphRegistry.RefsByTag);

			Assert.Equal(expected, character.Feats.OrderBy(f => f.Sequence).Select(f => f.FeatId).ToArray());
			Assert.Empty(character.Feats.Select(f => f.FeatId).Intersect(character.Skills.Select(s => s.SkillId))); // feats != skills
		}

		// A fresh character (and the fresh-scaffold Dikketest capture) carries no trained feats: the pre-IIR feat
		// list is the bare empty-list marker, so the frame stays byte-identical to a constant fresh frame.
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

			// 234, not 239: the 5 difference is the embedded CoreCharacter's version byte + IirHeader, which belong to
			// the message rather than to the frame.
			Assert.Equal(239 - 5, PlayerSpawnScaffoldRows.FreshPreFrame(ConanClass.Necromancer).ByteCount);
			Assert.Empty(PlayerSpawnScaffoldRows.ExtractPreFrameFeats(PlayerSpawnScaffoldRows.FreshPreFrame(ConanClass.Necromancer)));
		}

		// The Assassin's "closer": its tag 0x3F10 is the skill list's ListFrame word (15 skills), its value 300 the
		// archetype ability that opens the list. Both are derived, so the same facts are asserted where they live.
		[Fact]
		public void FreshAssassin_SkillList_Opens300AndFramesAs0x3F10()
		{
			var character = new Character
			{
				Id = 0x02000009,
				Class = ConanClass.Assassin
			};
			Assert.True(PlayerSpawnScaffoldRows.TrySeedFreshRows(character));

			var scaffold = new SimpleCharFullUpdateIIR();
			Assert.True(PlayerSpawnScaffoldRows.TryApplyFrom(character, scaffold, MorphRegistry.ByName));

			var skills = scaffold.Post.Slot.Skills;
			Assert.Equal(300u, skills[0].Id); // Rogue archetype x 100
			Assert.Equal(15, skills.Count); // the archetype ability + 13 fresh + the derived class-progression entry
			Assert.Equal(0x3F10u, ListFrame.ForCount(skills.Count)); // the skill list's ListFrame word
		}

		// Decoded over 117 retail player self-spawns with zero violations: a fresh character's skill list opens with
		// its archetype ability and frames as (count + 1) x 0x3F1. The client's skill parse validates that frame word,
		// so a mismatched one crashes it. Holds for every class.
		[Theory, InlineData(ConanClass.Guardian), InlineData(ConanClass.Conqueror), InlineData(ConanClass.DarkTemplar), InlineData(ConanClass.Barbarian), InlineData(ConanClass.Assassin), InlineData(ConanClass.Ranger),
		 InlineData(ConanClass.BearShaman), InlineData(ConanClass.PriestOfMitra), InlineData(ConanClass.TempestOfSet), InlineData(ConanClass.Necromancer), InlineData(ConanClass.Demonologist), InlineData(ConanClass.HeraldOfXotli)]
		public void FreshSkillList_OpensWithTheArchetypeAbility(ConanClass @class)
		{
			var character = new Character
			{
				Id = 0x02000008,
				Class = @class
			};
			Assert.True(PlayerSpawnScaffoldRows.TrySeedFreshRows(character));

			var scaffold = new SimpleCharFullUpdateIIR();
			Assert.True(PlayerSpawnScaffoldRows.TryApplyFrom(character, scaffold, MorphRegistry.ByName));

			var skills = scaffold.Post.Slot.Skills;
			Assert.Equal(PlayerSpawnScaffoldRows.ArchetypeSkillId(@class), skills[0].Id);

			// One more than the seeded rows: the archetype ability is derived, the progression entry replaces the
			// seeded run anchor 1:1.
			Assert.Equal(character.Skills.Count + 1, skills.Count);
		}

		// FreshPreFrame(feats) and ExtractPreFrameFeats are exact inverses, and the scaffold grows by 4 bytes per
		// feat (126-byte head incl. the marker + 4*count + 108-byte tail).
		[Fact]
		public void FreshPreFrame_And_ExtractPreFrameFeats_RoundTrip()
		{
			uint[] feats = [110004, 110100, 111304, 111402, 110300, 112700]; // hakotep (hox) unlocked set
			var frame = PlayerSpawnScaffoldRows.FreshPreFrame(ConanClass.HeraldOfXotli, 0, feats);

			Assert.Equal(126 + 4 * feats.Length + 108, frame.ByteCount);
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
			// word 4 to the mask's place, so reading the index back off PlayerSpawnPreFrame would make it
			// self-referential.
			Assert.Equal(expectedHex, Convert.ToHexString(PreIirBytes(frame).AsSpan(18, 4).ToArray()).ToLowerInvariant());
		}

		// The client derives the displayed profession from the character's feat tree, whose first (profession) feat =
		// class enum * 100. A fresh character must carry its own class's profession feat, not the Barbarian default
		// (1800). Verified against retail self-spawns for 8 classes.
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

		// A character that carries Dikketest's appearance model - his CharacterBody (the raw editor stream from his
		// create capture, zigzag-decoded by CharacterMorphs.TryApply) and his chosen resource refs - must reproduce
		// his captured spawn byte-for-byte. This is the brand-new-character path, the one every created character
		// takes: both halves of the entry table are generated, the weighted half from the refs through the real
		// registry and the morph half from the sliders.
		//
		// The refs have to be passed in. Without them the weighted half would come from a hardcoded copy of the
		// capture's own entries, so the golden would reproduce a constant and go on passing even if resolving refs to
		// tags broke entirely. They are recovered from his captured entry table through the registry, which is exactly
		// what ExtractTo does when it imports a captured character.
		[Fact]
		public void FreshRows_OverDikketestBody_AreByteIdenticalToTheCapture()
		{
			var body = Golden("SpawnDikketestLogin");
			var scaffold = SimpleCharFullUpdateCodec.Parse(Record(body));

			var character = new Character
			{
				Id = 0x02000005,
				// Dikketest is a Barbarian; his captured fresh feat list is the per-class Barbarian list (the
				// profession feat 1800 = Class.Barbarian * 100), so the fresh rows must be generated for that class
				// to reproduce his spawn byte-for-byte - the feat tree is the client's class source.
				Class = ConanClass.Barbarian,
				Body = new CharacterBody(),
				// His editor choices: the always-sent face paint (*texscript_183875 - the first element of his captured
				// spawn, key 0xB7), then in send order: hair, hair colour, body marking, face marking, eye colour, skin
				// colour - exactly one per category. No beard: he chose none, and nothing invents one for him.
				//
				// This list is recovered from his captured entry table through the registry, so it is only as good as the
				// registry's record alignment: an off-by-one there renames his choices (the hair at tag 176 reading as
				// `*hairmesh_208094` instead of `*hairmesh_208096`, the skin colour at tag 478 reading as a category-10
				// face marking) and the mis-tagging is then preserved in a golden.
				BodyResources = new[] { "*texscript_183875", "*hairmesh_208096", "*texscript_1108153", "*texscript_1136777", "*texscript_198071", "*texscript_213930", "*texscript_3113362" }
					.Select((r, i) => new CharacterBodyResource
					{
						CharacterId = 0x02000005,
						Sequence = i,
						Ref = r
					})
					.ToList()
			};
			foreach (var (morph, raw) in new (string, uint)[] { ("arm", 59), ("arse", 14), ("bosom", 30), ("chest", 59), ("leg", 59), ("stomach", 14), ("thigh", 59) })
				// raw = Dikketest's captured wire values; decode as the read side does before storing.
				Assert.True(CharacterMorphs.TryApply(character.Body, morph, (int)(raw >> 1) ^ -(int)(raw & 1)));

			Assert.True(PlayerSpawnScaffoldRows.TrySeedFreshRows(character));
			Assert.False(PlayerSpawnScaffoldRows.TrySeedFreshRows(character)); // idempotent
			Assert.True(PlayerSpawnScaffoldRows.TryApplyFrom(character, scaffold, MorphRegistry.ByName));

			Assert.Equal(Convert.ToHexString(Record(body)), Convert.ToHexString(SimpleCharFullUpdateCodec.Build(scaffold)));
		}

		// A character that never touched a slider (no CharacterBody) gets no morph entries at all: 6 weighted
		// attributes + the closer at idx 7 - the exact entry-table shape of strickenbla's captured spawn.
		[Fact]
		public void FreshAppearance_WithoutBody_HasNoMorphEntries()
		{
			// The closer is derived: value = archetype*100, tag = (feat count + 2)*0x3F1. A Barbarian has 20 fresh
			// feats -> tag (20+2)*0x3F1 = 0x56B6, value 300 (rogue = archetype 3).
			var character = new Character
			{
				Id = 0x02000006,
				Class = ConanClass.Barbarian
			};

			// A character that chose nothing and morphed nothing carries no appearance at all - standing in captured
			// weighted attributes would ship somebody else's hair and face markings.
			Assert.Empty(PlayerSpawnScaffoldRows.BuildAppearanceEntries(character, []));
		}

		// A never-seeded character has nothing to describe a spawn with, and the marker for that is its skill rows.
		// The entry table cannot be that marker now that it is derived: an appearance-less character legitimately
		// generates the default one.
		[Fact]
		public void TryApplyFrom_WithoutRows_ReturnsFalse()
		{
			var scaffold = SimpleCharFullUpdateCodec.Parse(Record(Golden("SpawnDikketestLogin")));

			Assert.False(PlayerSpawnScaffoldRows.TryApplyFrom(new Character
			{
				Id = 1
			}, scaffold, MorphRegistry.ByName));
			Assert.False(PlayerSpawnScaffoldRows.TryApplyFrom(null!, scaffold, MorphRegistry.ByName));
		}

		// The frame is not stored - TryApplyFrom reconstructs it from the character's class. So a captured spawn
		// round-trips to its constructed form: identical appearance/feats/pairs + IIR, with the frame regenerated as
		// FreshPreFrame(class) and transient buff/session records dropped.
		private static byte[] ConstructedForm(byte[] body, ConanClass @class)
		{
			var s = SimpleCharFullUpdateCodec.Parse(Record(body));
			// The frame is reconstructed, but the unlocked feat list it carries is character data and survives the row
			// round-trip - so the constructed form keeps the captured feats (spliced into the canonical frame).
			var feats = PlayerSpawnScaffoldRows.ExtractPreFrameFeats(s.PreIir);
			s.PreIir = PlayerSpawnScaffoldRows.FreshPreFrame(@class, 0, feats);
			s.Post.BuffRecords.Clear();
			return SimpleCharFullUpdateCodec.Build(s);
		}

		// Support.Golden, spelled out: this class has its own Golden(name) helper, so the bare name binds to the method.
		private static byte[] Golden(string name)
		{
			return Support.Golden.Bytes(name);
		}

		// A captured body minus the leading u32 carrier length, which belongs to the sender.
		private static byte[] Record(byte[] body)
		{
			return body[4..];
		}

		private static SpawnEntrySlot PlayerSlot(SimpleCharFullUpdateIIR spawn)
		{
			return spawn.Post.Slot;
		}

		// The pre-IIR scaffold composes into a spawn record rather than standing alone.
		private static byte[] PreIirBytes(SpawnPreIir frame)
		{
			using var stream = new FaolanStream();
			SpawnPreIirCodec.Write(stream, frame);
			return stream.ToArray();
		}

		// Capture -> character -> capture, through the shipped MorphTarget catalog in both directions: extraction turns
		// the entry table's weighted rows into resource refs and its morph rows into CharacterBody sliders, generation
		// resolves them back. Nothing about the entry table is carried across as data.
		private static byte[] RoundTripThroughRows(byte[] body, ConanClass @class)
		{
			var scaffold = SimpleCharFullUpdateCodec.Parse(Record(body));

			var character = new Character
			{
				Id = 0x02000005,
				Class = @class
			};
			PlayerSpawnScaffoldRows.ExtractTo(character, scaffold, MorphRegistry.RefsByTag);

			// The captured character's level, read back off the scaffold's own class-progression entry. That entry is
			// derived on the way out (TryApplyFrom) rather than replayed from the rows, because the client reads it as
			// the character's level and draws its attributes and health from the matching class/level template - so a
			// level has to come in for one to go back out, and these round-trips are what prove the two directions are
			// exact inverses on real captured bytes. See ClassProgressionEntry_IsTheCharactersLevel for the levels
			// each capture states.
			character.Level = PlayerSpawnScaffoldRows.ClassProgressionLevelOf(character.Skills.Select(s => s.SkillId)) ?? 0;

			var rebuilt = SimpleCharFullUpdateCodec.Parse(Record(body)); // fresh instance, then the character overwrites
			Assert.True(PlayerSpawnScaffoldRows.TryApplyFrom(character, rebuilt, MorphRegistry.ByName));
			return SimpleCharFullUpdateCodec.Build(rebuilt);
		}
	}
}
