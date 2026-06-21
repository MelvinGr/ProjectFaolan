using System;
using System.Buffers.Binary;
using System.Linq;
using System.Numerics;
using Faolan.Core.Database.Tables;

namespace Faolan.GameServer.Spawn
{
	// Bridges a stored NpcSpawn row to a wire FA015807 (SimpleCharFullUpdate) body, addressed to a live session
	// handle. The transform (position/rotation) is driven by the row's structured columns; the pre-IIR float block
	// + post-IIR scaffold are GENERATED from scratch (NpcSpawnScaffold), equipment from NpcSpawnEquipment rows and
	// morphs from NpcSpawnMorph rows. The row's captured `Data` is no longer replayed - it is decoded only to source
	// the CharacterIIR stat CORE (CharacterStats / NpcStats / Bcc) that isn't yet in typed storage; lifting that core
	// (mostly TYPE-level, 27 templates) is deferred to the Npc-type split (#244), where `Data` retires.
	// Byte-identical to the captured body when the row's columns match the capture (NpcSpawnBuilderTests); this is
	// the unit the world-entry/interest path calls per NPC instead of replaying the MapWorldBurst blob.
	public static class NpcSpawnBuilder
	{
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

			// #184/#266: Casilda hangs in the chains (the 004709 hangingwoman decor sits at her spot) but her captured
			// spawn often carries the generic standing idle (42024), so she stands free. Force her persistent idle to the
			// hanging-pose anim (156196 - the non-default AnimIdleId her captures show at the cage). PERSISTENT POSE via
			// the Bcc idle anim, not the transient SpawnAnimation (which left her undrawn). Quest-driven freeing is a
			// follow-up; for now she's posed chained at spawn.
			if (row?.Npc?.Name == "Casilda" && read.Message.Bcc != null)
				read.Message.Bcc.AnimIdleId = 156196;

			// Worn equipment from DB rows (#185): rebuild the CharacterIIR f23 list from NpcSpawnEquipment instead of
			// the captured Data blob's. Only when the row has lifted equipment - else the parsed (Data) list stands,
			// so a row predating the lift is unchanged. Byte-identical to the capture when the rows came from it.
			if (row?.Equipment is { Count: > 0 })
			{
				read.Message.InventoryEntries.Clear();
				foreach (var entry in NpcSpawnEquipmentBuilder.BuildEntries(row.Equipment))
					read.Message.InventoryEntries.Add(entry);
			}

			var position = new Vector3(row.PositionX, row.PositionY, row.PositionZ);
			var rotation = new Quaternion(row.RotationX, row.RotationY, row.RotationZ, row.RotationW);
			body = SpawnPacketBuilder.BuildSpawn(template, handle, position, rotation, read.Message);

			// On a fresh spawn, reset the runtime-state scaffolds to their from-scratch form. The pre-IIR
			// (transform/bounds) always resets to minimal. The post-IIR: ANIMALS get the minimal form; a HUMANOID's
			// post-IIR is its APPEARANCE / morph table (Gitea #243) - generated from the row's NpcSpawnMorph rows
			// (the from-DB lift, #185) when present, else the captured post-IIR is kept (the bald-fix fallback for a
			// row imported before the morph lift). (Humanoid = carries worn equipment - the signal the tests key on.)
			if (freshSpawn)
			{
				var humanoid = read.Message.InventoryEntries.Count > 0;
				if (humanoid && row.Morphs is { Count: > 0 })
				{
					var morphs = row.Morphs
						.OrderBy(m => m.Ordinal)
						.Select(m => new NpcAppearanceMorph { MorphId = m.MorphId, Value = m.Value, Text = m.Text ?? "" })
						.ToList();
					body = NpcSpawnAppearanceCodec.WithAppearancePostIir(NpcSpawnScaffold.WithMinimalPreIir(body), morphs);
				}
				else
				{
					body = NpcSpawnScaffold.WithFreshScaffolds(body, humanoid);
				}
			}

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
