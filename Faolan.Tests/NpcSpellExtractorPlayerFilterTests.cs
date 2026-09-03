using System.Linq;
using System.Numerics;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Importer.Captures;
using Faolan.Importer.Extractors;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// #616: `NpcSpellExtractor` joins a cast's applyFrom handle to a NAME through `NpcSpawnExtractor`, and the
	// worry was that the name is all that separates an NPC's cast from another PLAYER's - because
	// `SimpleCharFullUpdate` spawns both, and a stranger standing next to the capturing client is broadcast to it
	// exactly like an NPC. A stranger named `Guard` would then be harvested as that NPC's spell.
	//
	// The separation is already STRUCTURAL, and these gates are what say so rather than a comment. A player's spawn
	// carries `player_stats` and no `npc_stats`, and `NpcSpawnExtractor.TryBuildEntry` drops it on exactly that
	// (#244) - so a player-backed handle never enters the name map at all, and the name it would have collided under
	// is never consulted. The downstream `NpcTemplates` join in `NpcImportOps.ImportNpcSpells` is a second line, not the
	// only one.
	//
	// Written as a whole-corpus measurement because the claim is about what the corpus CONTAINS: if the extractor's
	// guard regressed, or a player spawn shape appeared that it does not recognise, a single hand-built fixture
	// would keep passing.
	public class NpcSpellExtractorPlayerFilterTests
	{
		// Every captured character spawn, split by what the message says it is. If the extractor ever yields an entry
		// for a body carrying player_stats, a player handle is in the name map and #616's hole is open.
		[Fact]
		public void NoPlayerBackedSpawnEverReachesTheNameMap()
		{
			var players = 0;
			var leaked = 0;

			foreach (var entry in CorpusExtract.PacketsOfType((uint)IirTypes.SimpleCharFullUpdateIIR))
			{
				var packet = new CapturedPacket(0, true, 0x2000, entry.Iir, entry.Full);
				if (!SpawnBodyReader.TryReadCharacterMessage(packet.Body, out var read) || read.Message.player_stats == null)
					continue;

				players++;

				// The map-change is what arms the extractor; without one it drops everything for the wrong reason.
				var extracted = NpcSpawnExtractor.Extract([MapChange(), packet]);
				if (extracted.Count != 0)
					leaked++;
			}

			// The corpus has to actually contain the thing being excluded, or this gate is vacuous.
			Assert.True(players > 0, "no player-backed character spawns in the corpus - the exclusion is untested");
			Assert.Equal(0, leaked);
		}

		// The complement, so the guard above is not passing by rejecting everything: NPC spawns - the ones carrying
		// npc_stats - do come through with a name, which is what the spell join is built on.
		[Fact]
		public void NpcBackedSpawnsStillYieldANamedEntry()
		{
			var named = 0;

			foreach (var entry in CorpusExtract.PacketsOfType((uint)IirTypes.SimpleCharFullUpdateIIR))
			{
				var packet = new CapturedPacket(0, true, 0x2000, entry.Iir, entry.Full);
				if (!SpawnBodyReader.TryReadCharacterMessage(packet.Body, out var read) || read.Message.npc_stats == null)
					continue;

				if (NpcSpawnExtractor.Extract([MapChange(), packet]).Any(e => !string.IsNullOrEmpty(e.Name)))
					named++;
			}

			Assert.True(named > 17_000, $"only {named} NPC spawns produced a named entry - the join the harvest rests on has narrowed");
		}

		// A real PlayfieldFIIR, the same one NpcSpawnExtractorTests builds: the extractor ignores every spawn until a
		// map change has set the current map.
		private static CapturedPacket MapChange()
		{
			var content = PlayfieldFCodec.Build(Vector3.Zero, 4010, 0, 1, 0x01EA7B9C, 15f, 13.86f, 0);
			var body = new byte[4 + content.Length];
			body.WriteU32Be(0, (uint)content.Length);
			content.CopyTo(body, 4);

			var full = new byte[12 + body.Length];
			body.CopyTo(full, 12);
			return new CapturedPacket(0, true, 0x2000, PlayfieldFCodec.Opcode, full);
		}
	}
}
