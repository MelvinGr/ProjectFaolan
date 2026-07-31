using System;
using System.IO;
using System.Runtime.CompilerServices;
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
	//   SpawnHoxxlingBuffed     - active buffs (two 60-byte buff records)
	//
	// ⚠️ The goldens are whole captured BODIES; the codec builds the RECORD, because the leading u32 length belongs
	// to the framing sender (#530 rule 2). So every comparison here is against `body[4..]`, and the four bytes it
	// drops are the sender's - nothing about the record changed.
	public class SimpleCharFullUpdateCodecTests
	{
		// The generated CharacterIIR component slot must agree with the existing splice path: re-encoding the
		// decoded record of the template and feeding its CharacterIir through SetCharacterMessage's oracle.
		[Fact]
		public void Build_AfterCharacterIirSwap_StaysParseable()
		{
			var s = SimpleCharFullUpdateCodec.Parse(Record("SpawnDikketestLogin"));
			var donor = Support.Golden.Bytes("SpawnStrieTemplate");
			Assert.True(SpawnBodyReader.TryReadCharacterMessage(donor, out var strie));

			s.Character = strie.Message; // a different character, and a different serialised length than Dikketest's
			var rebuilt = SimpleCharFullUpdateCodec.Build(s);

			var back = SimpleCharFullUpdateCodec.Parse(rebuilt);
			Assert.Equal("Strie", back.Character.Name);
			Assert.Equal(PlayerSlot(s).Entries.Count, PlayerSlot(back).Entries.Count);
		}

		[Theory, InlineData("SpawnEditorPreview"), InlineData("SpawnDikketestLogin"), InlineData("SpawnNecroKingsCastle"), InlineData("SpawnDemoRandomActions"), InlineData("SpawnHoxxlingBuffed")]
		public void ParseThenBuild_IsByteIdentical(string name)
		{
			var record = Record(name);

			var rebuilt = SimpleCharFullUpdateCodec.Build(SimpleCharFullUpdateCodec.Parse(record));

			Assert.Equal(Convert.ToHexString(record), Convert.ToHexString(rebuilt));
		}

		[Fact]
		public void ParseThenBuild_Template_IsByteIdentical()
		{
			var record = Support.Golden.Bytes("SpawnStrieTemplate")[4..];

			Assert.Equal(Convert.ToHexString(record), Convert.ToHexString(SimpleCharFullUpdateCodec.Build(SimpleCharFullUpdateCodec.Parse(record))));
		}

		[Fact]
		public void Parse_BuffedSpawn_LiftsBuffRecords()
		{
			var s = SimpleCharFullUpdateCodec.Parse(Record("SpawnHoxxlingBuffed"));

			Assert.Equal(2, s.Post.BuffRecords.Count);

			// The record is decoded now, not a 60-byte blob (#475): each carries a real spell id, the standard
			// [EntityClass.Character][handle] reference to the character it is running on, and a timer pair whose
			// remaining half never exceeds its total.
			Assert.All(s.Post.BuffRecords, r =>
			{
				Assert.NotEqual(0u, r.SpellId);
				Assert.Equal(ScaffoldBuff.DefaultKind, r.Kind);
				Assert.Equal(EntityClass.Character, r.TargetClass);
				Assert.Equal(s.Handle, r.TargetHandle);
				Assert.InRange(r.CasterLevel, 1u, 80u);
				Assert.True(r.TimerRemaining <= r.TimerTotal, "the remaining timer must not exceed the total");
				Assert.All(r.Trailer, word => Assert.Equal(0u, word));
			});

			Assert.Equal(35, PlayerSlot(s).Entries.Count);
		}

		[Fact]
		public void Parse_GearedSpawn_LiftsEndPairsAndPreTable()
		{
			var s = SimpleCharFullUpdateCodec.Parse(Record("SpawnNecroKingsCastle"));

			// The scaffold with one feat spec carrying 25 feats. The 339 the old model reported included the 5
			// bytes of the embedded CoreCharacter header its parse happened to cut into; those are the message's.
			Assert.Equal(339 - 5, s.PreIir.ByteCount);
			Assert.Equal(14, PlayerSlot(s).Entries.Count);
			Assert.Equal(0x2B6u, PlayerSlot(s).EntryTableX);
			Assert.Equal(8, s.Post.EndPairs.Count);
			Assert.Equal(new ScaffoldPair(10, -14375), s.Post.EndPairs[0]);
		}

		// Rule 4: Parse is strict. It used to be a TryParse whose `false` every caller had to remember to check.
		[Fact]
		public void Parse_RejectsNonSpawnBodies()
		{
			Assert.Throws<CodecParseException>(() => SimpleCharFullUpdateCodec.Parse(new byte[] { 1, 2, 3 }));
			Assert.Throws<CodecParseException>(() => SimpleCharFullUpdateCodec.Parse(new byte[64]));
		}

		[Fact]
		public void Parse_Template_LiftsTheDocumentedScaffold()
		{
			var s = SimpleCharFullUpdateCodec.Parse(Support.Golden.Bytes("SpawnStrieTemplate")[4..]);

			Assert.Equal(0x02165EFFu, s.Handle);
			Assert.Equal(5235752UL, s.Playfield);
			Assert.Equal(239 - 5, s.PreIir.ByteCount);
			Assert.Empty(s.Post.BuffRecords);

			// Strie: 7 entries (the weighted attributes idx 1..6 with weight 1.0 + the [u16][u32] closer at 7).
			var slot = PlayerSlot(s);
			Assert.Equal(7, slot.Entries.Count);
			Assert.Equal(new ScaffoldEntry(1, 0x0311, 0x3F800000), slot.Entries[0]);
			Assert.Equal(new ScaffoldEntry(7, 0x3F10, 0x12C), slot.Entries[6]);

			// 14 ability/feat ids, ascending, all rank 0; no ending pairs.
			Assert.Equal(14, slot.IdList.Count);
			Assert.Equal(0xD48u, slot.IdList[0].Id);
			Assert.Equal(0x50E24u, slot.IdList[^1].Id);
			Assert.All(slot.IdList, id => Assert.Equal(0, id.Rank));
			Assert.Empty(s.Post.EndPairs);
			Assert.Empty(s.Post.EndPairs2);
		}

		internal static PlayerEntrySlot PlayerSlot(SimpleCharFullUpdateIIR spawn)
		{
			return Assert.IsType<PlayerEntrySlot>(spawn.Post.Slot);
		}

		// The captured body minus the leading u32 carrier length - i.e. the record the codec owns.
		private static byte[] Record(string name)
		{
			return Golden.Bytes(name)[4..];
		}
	}
}
