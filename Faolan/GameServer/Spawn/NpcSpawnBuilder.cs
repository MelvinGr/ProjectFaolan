using System;
using System.Buffers.Binary;
using System.Numerics;
using Faolan.Core.Database.Tables;

namespace Faolan.GameServer.Spawn
{
	// Bridges a stored NpcSpawn row to a wire FA015807 (SimpleCharFullUpdate) body, addressed to a live session
	// handle. The transform (position/rotation) is driven by the row's structured columns; the CharacterIIR
	// (identity/appearance/equipment) and the still-raw pre-IIR float block + post-IIR scaffold come from the
	// row's captured `Data` (docs/archive/spawn-dissection-plan.md Phase 4 - those two blocks are the last un-lifted part).
	// Byte-identical to the captured body when the row's columns match the capture (NpcSpawnBuilderTests); this is
	// the unit the world-entry path will call per map instead of replaying the MapWorldBurst blob.
	public static class NpcSpawnBuilder
	{
		// Opt-in (FAOLAN_MINIMAL_SCAFFOLD=1): on a freshSpawn build, also reset the pre-IIR + post-IIR scaffolds to
		// their from-scratch minimal form (NpcSpawnScaffold.WithMinimalScaffolds) instead of carrying the row's
		// captured runtime-state scaffolds. The cross-capture sweep proved those scaffolds hold only per-instance
		// runtime state (0 on a fresh spawn), so this is the last "lift" for the from-DB NPC spawn (Gitea #185); it
		// stays gated until the in-client flip is verified (a cross-session captured scaffold renders today via the
		// playfield-token rewrite, so this is a fidelity/cleanliness step, not a render blocker).
		private static readonly bool UseMinimalScaffold =
			Environment.GetEnvironmentVariable("FAOLAN_MINIMAL_SCAFFOLD") == "1";

		// Produces the FA015807 body for `row`, addressed to `handle`. Returns false if the row has no usable
		// captured body (no Data / no decodable CharacterIIR). With `freshSpawn`, the transient
		// NpcStats.SpawnAnimation is zeroed: it is the animation that was PLAYING when the NPC was captured, not a
		// config value (retail's standing spawn of e.g. Casilda has 0; her row was captured mid-animation as 1).
		// Replaying a non-zero one drives the client into a spawn-anim state the freshly-spawned entity has no
		// reason to be in - on Casilda that left her entity present but undrawn. Default off keeps the byte-exact
		// reproduction the goldens assert; the world-entry/interest spawn paths pass true.
		public static bool TryBuild(NpcSpawn row, uint handle, out byte[] body, bool freshSpawn = false)
		{
			body = null;
			var template = ExtractBody(row?.Data);
			if (template == null || !PlayerSpawnReader.TryReadCharacterMessage(template, out var read))
				return false;

			if (freshSpawn && read.Message.npc_stats != null)
				read.Message.npc_stats.SpawnAnimation = 0;

			var position = new Vector3(row.PositionX, row.PositionY, row.PositionZ);
			var rotation = new Quaternion(row.RotationX, row.RotationY, row.RotationZ, row.RotationW);
			body = SpawnPacketBuilder.BuildSpawn(template, handle, position, rotation, read.Message);

			if (freshSpawn && UseMinimalScaffold)
				body = NpcSpawnScaffold.WithMinimalScaffolds(body);

			return true;
		}

		// The captured `Data` is the full Conan packet ([u32 len][u32 crc][u32 headerLen][header][body]); the
		// FA015807 body starts after the 12-byte prefix + the header. Returns null if the framing is malformed.
		private static byte[] ExtractBody(byte[] data)
		{
			if (data == null || data.Length < 12)
				return null;

			var headerLen = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8, 4));
			var bodyStart = 12 + headerLen;
			return bodyStart <= data.Length
				? data[bodyStart..]
				: null;
		}
	}
}
