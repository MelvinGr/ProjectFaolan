using System.Collections.Generic;
using System.Linq;
using Faolan.Core.Enums;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Constants;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// What the NPC post-IIR's record BODY actually contains (Gitea #475), gated over the whole corpus by the
	// PRODUCTION readers rather than by a walk this file keeps to itself.
	//
	// It was carried as an opaque `byte[] Records` described as "session runtime state the simulation re-derives".
	// It is the same grammar the player's stream has:
	//
	//   [SpawnPostIirCommon prologue]            the header, the first record and the ACTIVE BUFFS
	//   [entry slot]                             NPC: the morph table · PLAYER: 14-byte entries + the ID list
	//   [SpawnPostIirCommon tail]                the three closing pair lists
	//
	// The entry slot is the one genuine difference, which is why the two looked like unrelated formats - and the
	// "single record" forms turn out to be this shape with an EMPTY slot rather than a form of their own.
	public class NpcPostIirBodyTests
	{
		[Fact]
		public void EveryCapturedPostIirStream_IsReadByOneOfTheTwoCodecs()
		{
			var census = new Dictionary<string, int>();

			foreach (var (_, body) in CorpusExtract.OfType((uint)GameServer0X2000Opcodes.SimpleCharFullUpdateIIR))
			{
				if (!PlayerSpawnReader.TryReadCharacterMessage(body, out var read))
					continue;

				var npc = NpcSpawnPostIir.TryDecodePost(body[(read.Offset + read.Length)..], out _);
				var player = PlayerSpawnScaffoldCodec.TryParse(body, out _, out _);
				var key = npc && player ? "both" : npc ? "npc" : player ? "player" : "UNREAD";
				census[key] = census.GetValueOrDefault(key) + 1;
			}

			// Pinned to the FULL corpus: every one of the 11,632 captured streams is read to its exact last byte by
			// one of the two codecs, and nothing is left over.
			//
			// The 27 that BOTH read are player streams whose entry slot is EMPTY - there the player's
			// [count][entryTableX][marker] and the NPC's [count][two words] are the same twelve bytes, so the two
			// codecs do not disagree about anything; they read the same stream and produce the same bytes back. The
			// other 4,215 empty-slot streams are NPCs, which the player codec declines on its header (firstRecordLen
			// 4 / word2 2 are the PLAYER's values), so the families stay distinguishable even where the shape is not.
			//
			// This was 99.95% until the buff count stopped being searched for. Reading it off the end of the first
			// record (SpawnPostIirCommon) resolves the last six, including the one whose ASCII is scrambled in
			// 4-byte groups - a mis-reassembled TCP segment, which reads fine because the corruption is inside a
			// string rather than in the framing.
			Assert.Equal(11632, census.Values.Sum());
			Assert.Equal(0, census.GetValueOrDefault("UNREAD"));
			Assert.Equal(232, census["player"]);
			Assert.Equal(11373, census["npc"]);
			Assert.Equal(27, census["both"]);
		}

		// The acceptance bar for a decoded region (#475): what the reader takes apart, the writer puts back byte for
		// byte - across every capture that contains it, not one fixture.
		[Fact]
		public void EveryNpcShapedStream_ReEncodesByteExact()
		{
			var streams = 0;

			foreach (var (_, body) in CorpusExtract.OfType((uint)GameServer0X2000Opcodes.SimpleCharFullUpdateIIR))
			{
				if (!PlayerSpawnReader.TryReadCharacterMessage(body, out var read))
					continue;

				var post = body[(read.Offset + read.Length)..];
				if (!NpcSpawnPostIir.TryDecodePost(post, out var decoded))
					continue;

				Assert.Equal(post, decoded.Encode());
				streams++;
			}

			Assert.Equal(11373 + 27, streams);
		}

		// Every 60-byte record the readers find, on NPCs and players alike, is a ScaffoldBuff. The spell ids are the
		// proof that this is a buff list and not an arithmetic coincidence: they resolve in our own catalog and read
		// as a buff bar - and nine of them turn up on NPCs, two of those being necromancer PET summons.
		[Fact]
		public void EverySixtyByteRecordTheReadersFind_IsAScaffoldBuff()
		{
			var spellIds = new HashSet<uint>();
			var casterLevels = new List<uint>();
			var records = 0;

			foreach (var (_, body) in CorpusExtract.OfType((uint)GameServer0X2000Opcodes.SimpleCharFullUpdateIIR))
			{
				if (!PlayerSpawnReader.TryReadCharacterMessage(body, out var read))
					continue;
				if (!NpcSpawnPostIir.TryDecodePost(body[(read.Offset + read.Length)..], out var decoded))
					continue;

				foreach (var buff in decoded.Common.BuffRecords)
				{
					records++;
					spellIds.Add(buff.SpellId);
					casterLevels.Add(buff.CasterLevel);
					Assert.Equal(ScaffoldBuff.DefaultKind, buff.Kind);
					Assert.Equal(EntityClass.Character, buff.TargetClass);
					Assert.True(buff.TimerRemaining <= buff.TimerTotal, "the remaining timer must not exceed the total");
				}
			}

			Assert.Equal(670, records);

			// The nine that appear on NPC spawns, all rows in Spells: Runed Flesh (Rank 5 / Rank 1), Ether Channel,
			// Undead Minion: Mutilator, Deathless Acolyte: Arcanist, Hearty Meal, Shrink, Stagger, Frozen Hatred. Two
			// are necromancer PET summons and one is the Frozen Hatred proc - corroboration from a direction this
			// walk knows nothing about (#307/#140).
			uint[] onNpcs = [222825, 1148789, 1151265, 1220007, 1366140, 3161312, 3774365, 3911037, 4377209];
			Assert.All(onNpcs, id => Assert.Contains(id, spellIds));
			Assert.DoesNotContain(0u, spellIds);

			// ⚠️ 5..82, and 82 is ABOVE the level cap. So the field is level-LIKE - #438 identified it as the caster's
			// level by a separate route - but this corpus does not prove it IS the level, and asserting 1..80 here
			// would have written a wrong bound into a gate.
			Assert.Equal(5u, casterLevels.Min());
			Assert.Equal(82u, casterLevels.Max());
		}
	}
}
