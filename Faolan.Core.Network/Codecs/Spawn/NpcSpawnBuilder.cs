using System;
using System.Buffers.Binary;
using System.Linq;
using System.Collections.Generic;
using System.Numerics;
using Faolan.Core.Database.Tables;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protobuf;

namespace Faolan.Core.Network.Codecs
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

			// #267: Casilda's chained look comes from her WORN EQUIPMENT, not the captured idle and not the
			// decor_chain_hangingwoman world object (that renders fine - it's byte-identical to retail). At the post she
			// wears 4 pieces incl. slot-18 tpl402981 (the chains); while following she wears 3. The importer now keeps her
			// fullest loadout (BuildBurstTruthSpawns equipByName), and the captive BCC pose (SendCasildaPose) supplies the
			// hanging animation. We leave her captured idle as-is. See [[casilda_184_not_spawn_swap]].

			// Worn equipment from DB rows (#185): rebuild the CharacterIIR f23 list from NpcSpawnEquipment instead of
			// the captured Data blob's. Only when the row has lifted equipment - else the parsed (Data) list stands,
			// so a row predating the lift is unchanged. Byte-identical to the capture when the rows came from it.
			if (row?.Equipment is { Count: > 0 })
			{
				read.Message.InventoryEntries.Clear();
				foreach (var entry in NpcSpawnEquipmentBuilder.BuildEntries(row.Equipment))
					read.Message.InventoryEntries.Add(entry);
			}

			// Stat core from typed columns (#244): when the type has a lifted StatCore, CONSTRUCT the CharacterIIR stat
			// core (Stats/npc_stats/Bcc + CanFall/MovementMode) from Npc.StatCore + this placement's overrides instead
			// of keeping the one parsed from Data. Byte-identical to the capture (NpcSpawnStatCoreTests), so the stat
			// core is now DB-driven; Data still supplies the frame + name (retired in a later step).
			ApplyStatCore(read.Message, row);

			body = SpawnPacketBuilder.BuildSpawn(template, handle, row.Position, row.Rotation, read.Message);

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
					var morphs = row
						.Morphs
						.OrderBy(m => m.Ordinal)
						.Select(m => new NpcAppearanceMorph
						{
							MorphId = m.MorphId,
							Value = m.Value,
							Text = m.Text ?? ""
						})
						.ToList();
					body = NpcSpawnAppearanceCodec.WithAppearancePostIir(NpcSpawnScaffold.WithMinimalPreIir(body), morphs);
				}
				else
					body = NpcSpawnScaffold.WithFreshScaffolds(body, humanoid);
			}

			return true;
		}

		// Builds an FA015807 for a creature TYPE at a caller-supplied transform - the spawn-from-type path with NO
		// world placement (#244/#307). This is what a summoned PET uses: the pet's creature type carries the lifted
		// stat CORE (Npc.Data) and we place it at the summoner's position. Uses the parsed message's own
		// equipment/morphs (the Data blob already bakes them in); freshSpawn resets the runtime scaffolds like TryBuild.
		public static bool TryBuildFromType(Npc npc, uint handle, Vector3 position, Quaternion rotation, out byte[] body, bool freshSpawn = true)
		{
			body = null;
			var template = ExtractBody(npc?.Data);
			if (template == null || !PlayerSpawnReader.TryReadCharacterMessage(template, out var read))
				return false;

			if (freshSpawn && read.Message.npc_stats != null)
				read.Message.npc_stats.SpawnAnimation = 0;

			body = SpawnPacketBuilder.BuildSpawn(template, handle, position, rotation, read.Message);

			if (freshSpawn)
			{
				var humanoid = read.Message.InventoryEntries.Count > 0;
				body = NpcSpawnScaffold.WithFreshScaffolds(body, humanoid);
			}

			return true;
		}

		// Assembles a whole NPC FA015807 spawn body FROM SCRATCH (#244) - no captured template. The playfield token is
		// written into the transform directly (no post-hoc SpawnPlayfieldRewriter), and every section has its own
		// builder: header constants + handle; transform = [2C 0A <vec3 LE> 12 <quat LE> 18 <token varint>]; pre-IIR =
		// NpcSpawnPreIir.BuildMinimal; the CoreChar header [41][X][msgLen] with X = the lifted IirHeader; the serialised
		// CharacterIIR message; and the post-IIR (minimal for animals, the appearance/morph table for humanoids).
		// Byte-identical to the freshSpawn TryBuild output for animals + clean-appearance humanoids (verified in
		// NpcSpawnFromScratchTests). Humanoids whose captured post-IIR carries runtime state (not the clean morph
		// table) are NOT expressible here - the caller keeps their captured Data (the #243 tail).
		public static byte[] BuildFromScratch(uint handle, uint iirHeader, Vector3 position, Quaternion rotation, uint playfieldToken, ProtoCoreCharacterIIR message, bool humanoid, IReadOnlyList<NpcAppearanceMorph> morphs)
		{
			var msgBytes = message.ToProtobuf();
			var preIir = NpcSpawnPreIirCodec.Encode(NpcSpawnPreIir.BuildMinimal());
			var postIir = humanoid && morphs is { Count: > 0 }
				? NpcSpawnAppearanceCodec.BuildPostIir(morphs)
				: NpcSpawnScaffold.BuildMinimalPostIir();

			var body = new FaolanStream()
				.WriteUInt32(0) // body length placeholder (set below)
				.WriteUInt32(0xFA015807u) // subOpcode (SimpleCharFullUpdate)
				.WriteUInt32(EntityClass.Character) // 0x0000C350 ack
				.WriteUInt32(handle)
				.WriteUInt32(0) // fieldA
				.WriteByte(0x2C).WriteByte(0x0A).WriteArrayByteLength(EncodeVec3(position))
				.WriteByte(0x12).WriteArrayByteLength(EncodeQuat(rotation))
				.WriteByte(0x18).WriteArray(EncodePlayfieldVarint(playfieldToken))
				.WriteArray(preIir)
				.WriteByte(0x41).WriteUInt32(iirHeader).WriteUInt32((uint)msgBytes.Length) // CoreChar header
				.WriteArray(msgBytes)
				.WriteArray(postIir)
				.ToArray();

			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(0, 4), (uint)(body.Length - 4));
			return body;
		}

		// The transform vec3 = [0D x][15 y][1D z], each a little-endian fixed32 (15 bytes).
		private static byte[] EncodeVec3(Vector3 v) => new FaolanStream()
			.WriteByte(0x0D).WriteFloatLittleEndian(v.X)
			.WriteByte(0x15).WriteFloatLittleEndian(v.Y)
			.WriteByte(0x1D).WriteFloatLittleEndian(v.Z)
			.ToArray();

		// The transform quat = [0D x][15 y][1D z][25 w], each a little-endian fixed32 (20 bytes).
		private static byte[] EncodeQuat(Quaternion q) => new FaolanStream()
			.WriteByte(0x0D).WriteFloatLittleEndian(q.X)
			.WriteByte(0x15).WriteFloatLittleEndian(q.Y)
			.WriteByte(0x1D).WriteFloatLittleEndian(q.Z)
			.WriteByte(0x25).WriteFloatLittleEndian(q.W)
			.ToArray();

		// The playfield token in the transform is a fixed-width 4-byte LEB128 varint (tokens are < 2^28).
		private static byte[] EncodePlayfieldVarint(uint token) =>
		[
			(byte)((token & 0x7F) | 0x80),
			(byte)(((token >> 7) & 0x7F) | 0x80),
			(byte)(((token >> 14) & 0x7F) | 0x80),
			(byte)((token >> 21) & 0x7F)
		];

		// Constructs the CharacterIIR stat core (#244) from the type-level Npc.StatCore + this placement's overrides,
		// replacing whatever was parsed from Data. Type-level fields (senses defaults, flags, race, anim clips, ...)
		// come from the StatCore; the per-placement values override from the NpcSpawn columns: the always-present pools
		// (Health/Life/Mana/... + Level/scale/voice/mesh) are set unconditionally, and the sparse ones (GuildId +
		// Hearing/SightSkill) are RESET off the type value first, then set only if this placement carried them - so a
		// spawn that lacks one doesn't inherit the representative's. No-op when the type hasn't been lifted yet.
		private static void ApplyStatCore(ProtoCoreCharacterIIR msg, NpcSpawn row)
		{
			if (row?.Npc?.StatCore is not { } core)
				return;

			var s = NpcStatCoreCodec.ToStats(core);
			s.HeadMesh = row.HeadMesh;
			// The wire CharacterStats.Health is the ×100 max-HP value, NOT NpcSpawn.Health (the player-facing DISPLAY
			// value kept for gameplay). For ~all creatures wire Health == wire Life and tracks the per-placement level,
			// so drive it from the lifted wire Life; the rare divergent per-placement value (Rocknose) is in WireHealth.
			s.Health = row.WireHealth ?? row.Life;
			s.Life = row.Life;
			s.Mana = row.Mana;
			s.MaxMana = row.MaxMana;
			s.Stamina = row.Stamina;
			s.MaxStamina = row.MaxStamina;
			s.Level = row.Level;
			s.ApprenticeLevel = row.ApprenticeLevel;
			s.MonsterData = row.MonsterData;
			s.MonsterScale = row.MonsterScale;
			s.VoiceSet = row.VoiceSet;
			s.ResetGuildId();
			if (row.GuildId.HasValue)
				s.GuildId = row.GuildId.Value;
			msg.Stats = s;

			var n = NpcStatCoreCodec.ToNpcStats(core);
			n.ResetHearingSkill();
			if (row.HearingSkill.HasValue)
				n.HearingSkill = row.HearingSkill.Value;
			n.ResetSightSkill();
			if (row.SightSkill.HasValue)
				n.SightSkill = row.SightSkill.Value;
			n.ResetSmellingSkill();
			if (row.SmellingSkill.HasValue)
				n.SmellingSkill = row.SmellingSkill.Value;
			msg.npc_stats = n;

			msg.Bcc = NpcStatCoreCodec.ToBcc(core);
			if (core.CanFall.HasValue)
				msg.CanFall = core.CanFall.Value;
			if (core.MovementMode.HasValue)
				msg.MovementMode = core.MovementMode.Value;
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
