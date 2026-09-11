using System;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Constants;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// Locks the SimpleCharFullUpdateCodec round-trip: a player FA015807 body decomposed into the structured record
	// (transform / pre-IIR scaffold / CharacterIIR / entry table / ID list / end pairs / buff records) and
	// regenerated must be byte-for-byte identical. The golden fixtures are real captured player spawns covering
	// every layout variant the captures contain (the Python prototype proved the grammar over all 37 player-own
	// spawns, including captures added after the codec was written; these pin the C# port):
	//   SpawnEditorPreview      - in-editor preview, 228-byte constant pre frame, empty scaffold
	//   SpawnDikketestLogin     - fresh char at world entry (the fresh-character generation reference)
	//   SpawnNecroKingsCastle  - geared char, 339-byte pre frame (one feat spec carrying 25 feats), end pairs
	//   SpawnDemoRandomActions- both ending pair lists non-empty
	//   SpawnHeraldBuffed       - active buffs (two 60-byte buff records)
	//
	// The goldens are whole captured bodies; the codec builds the record, because the leading u32 length belongs
	// to the framing sender. So every comparison here is against `body[4..]`, and the four bytes it drops are the
	// sender's.
	public class SimpleCharFullUpdateCodecTests
	{
		// The generated CharacterIIR component slot must agree with the existing splice path: re-encoding the
		// decoded record of the template and feeding its CharacterIir through SetCharacterMessage's oracle.
		[Fact]
		public void Build_AfterCharacterIirSwap_StaysParseable()
		{
			var s = SimpleCharFullUpdateCodec.Parse(Record("SpawnDikketestLogin"));
			var donor = Golden.Bytes("SpawnAssassinTemplate");
			Assert.True(SpawnBodyReader.TryReadCharacterMessage(donor, out var strie));

			s.Character = strie.Message; // a different character, and a different serialised length than Dikketest's
			var rebuilt = SimpleCharFullUpdateCodec.Build(s);

			var back = SimpleCharFullUpdateCodec.Parse(rebuilt);
			Assert.Equal("Strie", back.Character.Name);
			Assert.Equal(s.Post.Slot.Appearance.Count, back.Post.Slot.Appearance.Count);
		}

		[Theory, InlineData("SpawnEditorPreview"), InlineData("SpawnDikketestLogin"), InlineData("SpawnNecroKingsCastle"), InlineData("SpawnDemoRandomActions"), InlineData("SpawnHeraldBuffed")]
		public void ParseThenBuild_IsByteIdentical(string name)
		{
			var record = Record(name);

			var rebuilt = SimpleCharFullUpdateCodec.Build(SimpleCharFullUpdateCodec.Parse(record));

			Assert.Equal(Convert.ToHexString(record), Convert.ToHexString(rebuilt));
		}

		[Fact]
		public void ParseThenBuild_Template_IsByteIdentical()
		{
			var record = Golden.Bytes("SpawnAssassinTemplate")[4..];

			Assert.Equal(Convert.ToHexString(record), Convert.ToHexString(SimpleCharFullUpdateCodec.Build(SimpleCharFullUpdateCodec.Parse(record))));
		}

		[Fact]
		public void Parse_BuffedSpawn_LiftsBuffRecords()
		{
			var s = SimpleCharFullUpdateCodec.Parse(Record("SpawnHeraldBuffed"));

			Assert.Equal(2, s.Post.BuffRecords.Count);

			// The list is decoded to the client's own carve: per spell a group whose
			// inner list holds the records - each with the standard [EntityClass.Character][handle] reference to the
			// character it is running on and a timer pair whose remaining half never exceeds its total.
			Assert.All(s.Post.BuffRecords, g =>
			{
				Assert.NotEqual(0u, g.SpellId);
				var r = Assert.Single(g.Records);
				Assert.Equal(EntityClass.Character, r.TargetClass);
				Assert.Equal(s.Handle, r.TargetHandle);
				Assert.InRange(r.CasterLevel, 1u, 80u);
				Assert.True(r.TimerRemaining <= r.TimerTotal, "the remaining timer must not exceed the total");
				Assert.All(r.Trailer, word => Assert.Equal(0u, word));
			});

			Assert.Equal(35, s.Post.Slot.Appearance.Count);
		}

		[Fact]
		public void Parse_GearedSpawn_LiftsEndPairsAndPreTable()
		{
			var s = SimpleCharFullUpdateCodec.Parse(Record("SpawnNecroKingsCastle"));

			// The scaffold with one feat spec carrying 25 feats. The 339 in the capture includes the 5 bytes of the
			// embedded CoreCharacter header, which belong to the message rather than to the scaffold.
			Assert.Equal(339 - 5, s.PreIir.ByteCount);
			// His first appearance element is his beard (*beardmesh_3181878, wire key 0x2B6), not an "EntryTableX"
			// scalar. He has no face-paint element: retail's slot 1 is vacated
			// (his Word2 ordinals run 2..15), i.e. he removed it.
			Assert.Equal(14, s.Post.Slot.Appearance.Count);
			Assert.Equal(0x2B6u, s.Post.Slot.Appearance[0].MorphId);
			Assert.Equal(8, s.Post.EndPairs.Count);
			Assert.Equal(new ScaffoldPair(10, -14375), s.Post.EndPairs[0]);
		}

		// Parse is strict: a non-spawn body throws rather than returning a `false` every caller has to remember to
		// check.
		[Fact]
		public void Parse_RejectsNonSpawnBodies()
		{
			Assert.Throws<CodecParseException>(() => SimpleCharFullUpdateCodec.Parse(new byte[] { 1, 2, 3 }));
			Assert.Throws<CodecParseException>(() => SimpleCharFullUpdateCodec.Parse(new byte[64]));
		}

		[Fact]
		public void Parse_Template_LiftsTheDocumentedScaffold()
		{
			var s = SimpleCharFullUpdateCodec.Parse(Golden.Bytes("SpawnAssassinTemplate")[4..]);

			Assert.Equal(0x02165EFFu, s.Handle);
			Assert.Equal(5235752UL, s.Playfield);
			Assert.Equal(239 - 5, s.PreIir.ByteCount);
			Assert.Empty(s.Post.BuffRecords);

			// Strie: 7 weighted appearance elements, slot ordinals 1..7, weight 1.0, opening with the always-sent
			// face paint (0xB7 = *texscript_183875).
			var slot = s.Post.Slot;
			Assert.Equal(7, slot.Appearance.Count);
			Assert.Equal(0xB7u, slot.Appearance[0].MorphId);
			Assert.Equal(1f, slot.Appearance[0].Value);
			Assert.Equal(1u, slot.Appearance[0].Word2);
			Assert.Equal(0x311u, slot.Appearance[1].MorphId);
			Assert.All(slot.Appearance, e => Assert.Equal("", e.Text));

			// 15 ability/feat ids: the archetype ability (300, Rogue) first, then 14 ascending; all rank 0. The list
			// opens with its own frame word.
			Assert.Equal(15, slot.Skills.Count);
			Assert.Equal(300u, slot.Skills[0].Id);
			Assert.Equal(0xD48u, slot.Skills[1].Id);
			Assert.Equal(0x50E24u, slot.Skills[^1].Id);
			Assert.All(slot.Skills, sk => Assert.Equal(0, sk.Rank));
			Assert.Empty(s.Post.EndPairs);
			Assert.Empty(s.Post.EndPairs2);
		}

		// The captured body minus the leading u32 carrier length - i.e. the record the codec owns.
		private static byte[] Record(string name)
		{
			return Golden.Bytes(name)[4..];
		}
	}
}
