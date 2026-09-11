using System;
using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Constants;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// The whole-corpus gate for SimpleCharFullUpdateCodec: every captured FA015807 - player and NPC alike - is
	// decoded to its exact last byte and re-encoded byte-for-byte.
	//
	// The post-IIR is one grammar for both families, not opaque "session runtime state the simulation re-derives":
	//
	//   [header][first record, ending in the buff list's frame][active buffs]
	//   [entry slot]                             NPC: the morph table · player: 14-byte entries + the ID list
	//   [the three closing pair lists]
	//
	// The entry slot is the one genuine difference, which is why the two looked like unrelated formats - and the
	// "single record" forms turn out to be this shape with an empty slot rather than a form of their own.
	public class SimpleCharFullUpdateCorpusTests
	{
		// The acceptance bar for a decoded region: what the reader takes apart, the writer puts back byte for
		// byte - across every capture that contains it, not one fixture.
		[Fact]
		public void EveryCapturedSpawn_ParsesToTheExactByte_AndReEncodes()
		{
			var unread = new List<string>();
			var mismatches = new List<string>();
			var census = new Dictionary<string, int>();

			foreach (var (source, body) in CorpusExtract.OfType((uint)IirTypes.SimpleCharFullUpdateIIR))
			{
				var record = body[4..]; // the u32 carrier length is the sender's (#530 rule 2)
				SimpleCharFullUpdateIIR spawn;
				try
				{
					spawn = SimpleCharFullUpdateCodec.Parse(record);
				}
				catch (CodecParseException e)
				{
					unread.Add($"{source}: {e.Message}");
					continue;
				}

				var rebuilt = SimpleCharFullUpdateCodec.Build(spawn);
				if (!rebuilt.AsSpan().SequenceEqual(record))
					mismatches.Add($"{source}: {record.Length} -> {rebuilt.Length} bytes");

				var family = spawn.Post.HasMoveTaskSlot ? "npc" : "player";
				census[family] = census.GetValueOrDefault(family) + 1;
			}

			Assert.True(unread.Count == 0, $"{unread.Count} captured spawns did not decode:\n  " + string.Join("\n  ", unread.Take(10)));
			Assert.True(mismatches.Count == 0, $"{mismatches.Count} captured spawns did not re-encode byte-exact:\n  " + string.Join("\n  ", mismatches.Take(10)));

			// Pinned to the full corpus. The family is told apart by the first record's move-task slot: an NPC has
			// one, a player's first record is the buff list's framing word (length 4). Of the 317 player streams, 31
			// have an empty entry slot - there the player's [count][entryTableX][marker] and the NPC's [count][two
			// words] are the same twelve bytes, so the two readings do not disagree about anything and either
			// reproduces the stream.
			//
			// Reading the buff count off the end of the first record, rather than searching for it, is what resolves
			// the last six - including the one whose ASCII is scrambled in 4-byte groups, a
			// mis-reassembled TCP segment, which reads fine because the corruption is inside a string rather than in
			// the framing.
			Assert.Equal(17717, census.Values.Sum());
			Assert.Equal(317, census["player"]);
			Assert.Equal(17400, census["npc"]);
		}

		// The first record's move-task slot. It reads as "runtime state the simulation re-derives, 20..1,273 bytes"
		// and is not: it is the entity's current MoveTask, in the same modes the MoveTaskIIR packet uses, parsed by the
		// same grammar.
		[Fact]
		public void EveryFirstRecordSlot_IsAMoveTask()
		{
			var modes = new Dictionary<byte, int>();
			var withTask = 0;
			var emptySlot = 0;
			var noSlot = 0;

			foreach (var (_, body) in CorpusExtract.OfType((uint)IirTypes.SimpleCharFullUpdateIIR))
			{
				if (!SpawnBodyReader.TryParse(body, out var spawn))
					continue;

				if (!spawn.Post.HasMoveTaskSlot)
					noSlot++;
				else if (spawn.Post.MoveTask is not { } task)
					emptySlot++;
				else
				{
					withTask++;
					modes[task.Mode] = modes.GetValueOrDefault(task.Mode) + 1;
				}
			}

			// Pinned to the full corpus. Every NPC stream has the slot; every player stream lacks it.
			Assert.Equal(317, noSlot);
			Assert.Equal(14153, emptySlot);
			Assert.Equal(3247, withTask);

			// The five modes are MoveTaskCodec's own: 4 = ListMoveTask (a waypoint path), 7 and 8 = the approach
			// forms, 1 = PathMoveTask, 2 = the stationary from/to/facing record.
			Assert.Equal(new Dictionary<byte, int>
			{
				[0x04] = 3170,
				[0x07] = 32,
				[0x08] = 33,
				[0x01] = 11,
				[0x02] = 1
			}, modes);
		}

		// Every buff group the reader finds, on NPCs and players alike, is a ScaffoldBuffGroup with exactly one
		// record (the inner frame the flat carve knew as "Kind = ForCount(1)" - a spawn carrying two records of one
		// spell has never been captured). The spell ids are the proof that this is a buff list and not an
		// arithmetic coincidence: they resolve in our own catalog and read as a buff bar - and nine of them turn up
		// on NPCs, two of those being necromancer pet summons.
		[Fact]
		public void EverySixtyByteRecordTheReaderFinds_IsAScaffoldBuff()
		{
			var spellIds = new HashSet<uint>();
			var casterLevels = new List<uint>();
			var records = 0;
			var classes = new Dictionary<uint, int>();

			foreach (var (_, body) in CorpusExtract.OfType((uint)IirTypes.SimpleCharFullUpdateIIR))
			{
				if (!SpawnBodyReader.TryParse(body, out var spawn))
					continue;

				foreach (var group in spawn.Post.BuffRecords)
				{
					spellIds.Add(group.SpellId);
					var buff = Assert.Single(group.Records);
					records++;
					casterLevels.Add(buff.CasterLevel);
					classes[buff.TargetClass] = classes.GetValueOrDefault(buff.TargetClass) + 1;
					Assert.True(buff.TimerRemaining <= buff.TimerTotal, "the remaining timer must not exceed the total");
				}
			}

			// 1471 records over all 17,717 captured streams - player and NPC alike. The old census read 670, because
			// it only counted the streams the NPC-shaped decoder accepted; the player-only ones carry buffs too and
			// were simply never walked by that gate.
			Assert.Equal(1471, records);

			// TargetClass is EntityClass.Character on 1469 of them and zero on exactly two. The field was written up
			// as "EntityClass.Character on all 941" from the narrower census - a claim the wider one breaks. It is
			// carried, not asserted, so that record round-trips (the whole-record gate above reproduces it); asserting
			// the class here would have rejected a real captured stream.
			Assert.Equal(new Dictionary<uint, int> { [EntityClass.Character] = 1469, [0] = 2 }, classes);

			// The nine that appear on NPC spawns, all rows in Spells: Runed Flesh (Rank 5 / Rank 1), Ether Channel,
			// Undead Minion: Mutilator, Deathless Acolyte: Arcanist, Hearty Meal, Shrink, Stagger, Frozen Hatred. Two
			// are necromancer pet summons and one is the Frozen Hatred proc - corroboration from a direction this
			// walk knows nothing about.
			uint[] onNpcs = [222825, 1148789, 1151265, 1220007, 1366140, 3161312, 3774365, 3911037, 4377209];
			Assert.All(onNpcs, id => Assert.Contains(id, spellIds));
			Assert.DoesNotContain(0u, spellIds);

			// 0..83, and 83 is above the level cap. So the field is level-like - it is identified as the caster's level
			// by a separate route - but this corpus does not prove it is the level, and asserting 1..80 here
			// would have written a wrong bound into a gate. (The narrower NPC-only census read 5..82; the player
			// streams it skipped carry a 0, which is another reason the field is carried rather than bounded.)
			Assert.Equal(0u, casterLevels.Min());
			Assert.Equal(83u, casterLevels.Max());
		}
	}
}
