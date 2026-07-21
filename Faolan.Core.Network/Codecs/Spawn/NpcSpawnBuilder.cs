using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Faolan.Core.Database.Tables;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// Builds a wire FA015807 (SimpleCharFullUpdate) NPC spawn body wholly from typed storage (#243/#244) - the
	// captured `Data` blob is retired. From a placement (TryBuildLive off an NpcSpawn row) or a creature TYPE
	// (TryBuildFromType off an NpcTemplate, for summoned pets/mounts): the transform from the row's columns, the CharacterIIR
	// stat core from NpcTemplate.StatCore, worn equipment from the *Equipment rows, morphs from the *Morph rows, and the
	// pre-IIR/post-IIR scaffolds GENERATED from scratch (BuildBody + NpcSpawnScaffold). This is the unit the
	// world-entry/interest path calls per NPC.
	public static class NpcSpawnBuilder
	{
		// Assembles a whole NPC FA015807 spawn body FROM SCRATCH (#244) - no captured template. The playfield token is
		// written into the transform directly (no post-hoc SpawnPlayfieldRewriter), and every section has its own
		// builder: header constants + handle; transform = [2C 0A <vec3 LE> 12 <quat LE> 18 <token varint>]; pre-IIR =
		// NpcSpawnPreIir.BuildMinimal; the CoreChar header [41][X][msgLen] with X = the lifted IirHeader; the serialised
		// CharacterIIR message; and the post-IIR: the appearance/morph table when `morphs` are given, else the minimal
		// form with `minimalRecordCount` (the type-stable 1 or 2 - #243).
		// This is the sole NPC spawn-body path (the captured Data blob was retired): every shipped spawn is an animal,
		// a clean-appearance humanoid, or a no-morph spawn expressible from columns (NpcSpawnFromScratchTests).
		public static byte[] BuildBody(uint handle, uint iirHeader, Vector3 position, Quaternion rotation, uint playfieldToken, ProtoCoreCharacterIIR message, IReadOnlyList<NpcAppearanceMorph> morphs,
			uint minimalRecordCount = NpcSpawnScaffold.MinimalRecordCount)
		{
			var msgBytes = message.ToProtobuf();
			var preIir = NpcSpawnPreIirCodec.Encode(NpcSpawnPreIir.BuildMinimal());
			var postIir = morphs is { Count: > 0 }
				? NpcSpawnAppearanceCodec.BuildPostIir(morphs)
				: NpcSpawnScaffold.BuildMinimalPostIir(minimalRecordCount);

			var body = new FaolanStream()
				.WriteUInt32(0) // body length placeholder (set below)
				.WriteUInt32(GameServer0X2000Opcodes.SimpleCharFullUpdateIIR) // the entity-spawn subop ("Spawn")
				.WriteUInt32(EntityClass.Character) // 0x0000C350 ack
				.WriteUInt32(handle)
				.WriteUInt32(0) // fieldA
				// The 0x2C transform component: a ProtoGameCodeDynelIIR { position=1, rotation=2, playfield_instance=3 }.
				// All 15787 captured FA015807/66AEDD50 groups carry exactly those three fields in that order, and every
				// captured playfield token falls in [2^21, 2^28) - where the MINIMAL varint protobuf-net writes is
				// already 4 bytes, so SpawnPlayfieldRewriter's splice-free in-place re-address still holds.
				.WriteByte(0x2C)
				.WriteArray(new ProtoGameCodeDynelIIR
				{
					Position = position,
					Rotation = rotation,
					PlayfieldInstance = playfieldToken
				}.ToProtobuf())
				.WriteArray(preIir)
				.WriteByte(0x41)
				.WriteUInt32(iirHeader)
				.WriteUInt32(msgBytes.Length) // CoreChar header
				.WriteArray(msgBytes)
				.WriteArray(postIir)
				.ToArray();

			BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(0, 4), (uint)(body.Length - 4));
			return body;
		}

		// Constructs the WHOLE ProtoCoreCharacterIIR message for a spawn from typed columns/rows (#244) - no captured
		// Data: Name from the type, the stat core from StatCore + placement (ApplyStatCore), worn equipment from the
		// NpcSpawnEquipment rows. Runtime/target fields are omitted (canonical fresh spawn). Feed this to
		// BuildBody to assemble the whole body from the DB.
		public static ProtoCoreCharacterIIR BuildMessageFromColumns(NpcSpawn row)
		{
			var msg = new ProtoCoreCharacterIIR
			{
				Name = row.NpcTemplate?.Name ?? ""
			};
			ApplyStatCore(msg, row);
			if (row.Equipment is { Count: > 0 })
			{
				foreach (var entry in NpcSpawnEquipmentBuilder.BuildEntries(row.Equipment))
					msg.InventoryEntries.Add(entry);
			}

			return msg;
		}

		// Constructs the WHOLE ProtoCoreCharacterIIR for a creature TYPE (#307): Name + the stat core straight from
		// NpcTemplate.StatCore (no per-placement overrides - a type has no placement) + worn equipment from NpcTemplate.Equipment.
		public static ProtoCoreCharacterIIR BuildMessageFromType(NpcTemplate npc)
		{
			var core = npc.StatCore;
			var msg = new ProtoCoreCharacterIIR
			{
				Name = npc.Name ?? "",
				Stats = NpcStatCoreCodec.ToStats(core),
				npc_stats = NpcStatCoreCodec.ToNpcStats(core),
				Bcc = NpcStatCoreCodec.ToBcc(core)
			};
			if (core.CanFall.HasValue)
				msg.CanFall = core.CanFall.Value;
			if (core.MovementMode.HasValue)
				msg.MovementMode = core.MovementMode.Value;
			if (npc.Equipment is { Count: > 0 })
			{
				foreach (var entry in NpcSpawnEquipmentBuilder.BuildEntries(npc.Equipment))
					msg.InventoryEntries.Add(entry);
			}

			return msg;
		}

		// Builds an FA015807 for a creature TYPE at a caller-supplied transform - the spawn-from-type path with NO world
		// placement (#244/#307). This is what a summoned PET/MOUNT uses: the creature type carries the lifted stat core
		// (NpcTemplate.StatCore), worn equipment (NpcTemplate.Equipment), appearance morphs (NpcTemplate.Morphs) and the no-morph recordCount,
		// and we assemble the whole body FROM SCRATCH at the summoner's position - no captured Data blob. The token is a
		// placeholder (the caller re-stamps it per recipient via SendWorldSpawn). Returns false if the type has no
		// lifted stat core.
		public static bool TryBuildFromType(NpcTemplate npc, uint handle, Vector3 position, Quaternion rotation, out byte[] body)
		{
			body = null;
			if (npc?.StatCore?.IirHeader is not { } x)
				return false;

			var morphs = npc
				.Morphs.OrderBy(m => m.Ordinal)
				.Select(m => new NpcAppearanceMorph
				{
					MorphId = m.MorphId,
					Value = m.Value,
					Text = m.Text ?? ""
				})
				.ToList();
			var message = BuildMessageFromType(npc);

			var minimalRc = npc.StatCore.MinimalPostIirRecordCount ?? NpcSpawnScaffold.MinimalRecordCount;
			body = BuildBody(handle, x, position, rotation, 0, message, morphs, minimalRc);
			return true;
		}

		// The LIVE spawn path (#244/#243): assemble the whole body from typed columns with `playfieldToken` baked into
		// the transform - no captured Data, no SpawnPlayfieldRewriter. Every shipped spawn is expressible from scratch:
		// an ANIMAL (no equipment -> minimal post-IIR), a humanoid whose APPEARANCE is lifted into morph rows (-> the
		// appearance table), or a NO-MORPH spawn whose type carries a known minimal recordCount (-> the minimal form
		// with that count, #243). Returns false only for a row with no lifted type (no StatCore.IirHeader) or a no-morph
		// spawn whose type recordCount was never derived - neither occurs in the shipped DB (988/988 build). The returned
		// body already carries the live token, so the caller never post-patches it.
		// NB the dev nameplate LABEL (#363) is NOT baked into the spawn name here: the client draws an NPC's floating
		// name from its template_id LDB, ignoring the spawn-body Name, so the label is pushed separately as a
		// SetNameIIR after the spawn (WorldBroadcaster.SendWorldSpawn / CreateEditorRecordBuilder.BuildWorldSetNameIir).
		public static bool TryBuildLive(NpcSpawn row, uint handle, uint playfieldToken, out byte[] body)
		{
			body = null;
			if (row?.NpcTemplate?.StatCore?.IirHeader is not { } x)
				return false;

			var morphs = row
				.Morphs.OrderBy(m => m.Ordinal)
				.Select(m => new NpcAppearanceMorph
				{
					MorphId = m.MorphId,
					Value = m.Value,
					Text = m.Text ?? ""
				})
				.ToList();

			var message = BuildMessageFromColumns(row);

			// Animal or morph-humanoid: minimal record count is 1 (unused when morphs are present).
			if (!row.HasEquipment || morphs.Count > 0)
			{
				body = BuildBody(handle, x, row.Position, row.Rotation, playfieldToken, message, morphs);
				return true;
			}

			// No-morph spawn: build the minimal form with the type's fresh record count (1 or 2) when known.
			if (row.NpcTemplate.StatCore.MinimalPostIirRecordCount is not { } minimalRecordCount)
				return false;

			body = BuildBody(handle, x, row.Position, row.Rotation, playfieldToken, message, morphs, minimalRecordCount);
			return true;
		}

		// Constructs the CharacterIIR stat core (#244) from the type-level NpcTemplate.StatCore + this placement's overrides,
		// replacing whatever was parsed from Data. Type-level fields (senses defaults, flags, race, anim clips, ...)
		// come from the StatCore; the per-placement values override from the NpcSpawn columns: the always-present pools
		// (Health/Life/Mana/... + Level/scale/voice/mesh) are set unconditionally, and the sparse ones (GuildId +
		// Hearing/SightSkill) are RESET off the type value first, then set only if this placement carried them - so a
		// spawn that lacks one doesn't inherit the representative's. No-op when the type hasn't been lifted yet.
		private static void ApplyStatCore(ProtoCoreCharacterIIR msg, NpcSpawn row)
		{
			if (row?.NpcTemplate?.StatCore is not { } core)
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
	}
}
