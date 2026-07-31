using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Tables;
using Faolan.Core.Extensions;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// Maps an NPC PLACEMENT (an NpcSpawn row) or a creature TYPE (an NpcTemplate) to the wire spawn value
	// SimpleCharFullUpdateCodec encodes (#243/#244) - the captured `Data` blob is retired. The transform comes from
	// the row's columns, the CharacterIIR stat core from NpcTemplate.StatCore, worn equipment from the *Equipment
	// rows, morphs from the *Morph rows, and the pre-IIR/post-IIR scaffolds are GENERATED from scratch.
	//
	// A ROW -> VALUE mapper, not a codec (the wave-3/4 precedent; row↔value mapping proper is Gitea #533): every
	// method here ends in SimpleCharFullUpdateCodec.Build, which is the one encoder for this IIR. It used to build
	// the body itself, which made it a second emitter of SimpleCharFullUpdateIIR.
	public static class NpcSpawnBuilder
	{
		// Assembles a whole NPC FA015807 spawn RECORD from scratch (#244) - no captured template. The playfield token
		// is written into the transform directly (no post-hoc SpawnTransformPatch); the pre-IIR is the minimal
		// generated scaffold; the post-IIR is the appearance/morph table when `morphs` are given, else the minimal
		// form with `minimalWord2` (the type-stable 1, 2 or 3 - #243/#475).
		//
		// This is the sole NPC spawn-body path (the captured Data blob was retired): every shipped spawn is an
		// animal, a clean-appearance humanoid, or a no-morph spawn expressible from columns (NpcSpawnFromScratchTests).
		//
		// The u32 carrier length belongs to the SENDER (#530 rule 2) - what comes back is the record.
		public static byte[] BuildBody(uint handle, uint iirHeader, Vector3 position, Quaternion rotation, uint playfieldToken, ProtoCoreCharacterIIR message, IReadOnlyList<NpcAppearanceMorph> morphs,
			uint minimalWord2 = SpawnPostIirs.MinimalWord2)
		{
			return SimpleCharFullUpdateCodec.Build(new SimpleCharFullUpdateIIR
			{
				Handle = handle,
				Position = position,
				Rotation = rotation,
				Playfield = playfieldToken,
				PreIir = SpawnPreIirs.Minimal(),
				CoreCharIirHeader = iirHeader,
				Character = message,
				Post = morphs is { Count: > 0 }
					? SpawnPostIirs.Npc(SpawnPostIirs.MinimalHumanoidWord2, morphs)
					: SpawnPostIirs.Npc(minimalWord2)
			});
		}

		// Builds an FA015807 for a creature TYPE at a caller-supplied transform - the spawn-from-type path with NO
		// world placement (#244/#307). This is what a summoned PET/MOUNT and an AMBIENT-POOL member use: the creature
		// type carries the lifted stat core (NpcTemplate.StatCore), worn equipment (NpcTemplate.Equipment),
		// appearance morphs (NpcTemplate.Morphs) and the no-morph word2, and we assemble the whole record FROM
		// SCRATCH at the summoner's position - no captured Data blob. `playfieldToken` MUST be the spawning zone's
		// live token (Zone.PlayfieldToken): it is baked into the transform, and SendWorldSpawn later re-stamps it
		// per recipient via SpawnTransformPatch - but that rewriter can only overwrite a token that is ALREADY a
		// 4-byte varint, so passing 0 (a 1-byte varint) both left the token un-re-stampable AND made the transform 3
		// bytes shorter, desyncing the client's version read into a crash. Zone tokens are always 4-byte (WorldState
		// mints them from 0x00A00000), so passing the zone token keeps the record wire-correct. Throws if the type
		// has no lifted stat core.
		public static byte[] BuildFromType(NpcTemplate npc, uint handle, Vector3 position, Quaternion rotation, uint playfieldToken)
		{
			if (npc?.StatCore?.IirHeader is not { } x)
				throw new InvalidOperationException(
					$"NpcTemplate '{npc?.Name ?? "?"}' (id {npc?.TemplateId}) has no lifted StatCore.IirHeader - the type is not imported/lifted (or StatCore was not Included).");

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

			var minimalWord2Value = npc.StatCore.MinimalPostIirWord2 ?? SpawnPostIirs.MinimalWord2;
			return BuildBody(handle, x, position, rotation, playfieldToken, message, morphs, minimalWord2Value);
		}

		// The LIVE spawn path (#244/#243): assemble the whole record from typed columns with `playfieldToken` baked
		// into the transform - no captured Data, no SpawnTransformPatch. Every shipped spawn is expressible from
		// scratch: an ANIMAL (no equipment -> minimal post-IIR), a humanoid whose APPEARANCE is lifted into morph
		// rows (-> the appearance table), or a NO-MORPH spawn whose type carries a known minimal word2 (#243).
		// THROWS for a row with no lifted type (no StatCore.IirHeader) or a no-morph spawn whose type word2 was never
		// derived - neither occurs in the shipped DB (988/988 build), so a throw signals un-lifted /
		// partially-imported data or a missing Include, not a normal outcome.
		// NB the dev nameplate LABEL (#363) is NOT baked into the spawn name here: the client draws an NPC's floating
		// name from its template_id LDB, ignoring the spawn-body Name, so the label is pushed separately as a
		// SetNameIIR after the spawn (WorldBroadcaster.SendWorldSpawn / SetNameCodec).
		public static byte[] BuildLive(NpcSpawn row, uint handle, uint playfieldToken)
		{
			if (row?.NpcTemplate?.StatCore?.IirHeader is not { } x)
				throw new InvalidOperationException(
					$"NpcSpawn (id {row?.Id}, type '{row?.NpcTemplate?.Name ?? "?"}') has no lifted StatCore.IirHeader - the type is not imported/lifted (or StatCore was not Included).");

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

			// Animal or morph-humanoid: the minimal word2 is unused when morphs are present.
			if (!row.HasEquipment || morphs.Count > 0)
				return BuildBody(handle, x, row.Position, row.Rotation, playfieldToken, message, morphs);

			// No-morph spawn: build the minimal form with the type's fresh word2.
			if (row.NpcTemplate.StatCore.MinimalPostIirWord2 is not { } minimalWord2)
				throw new InvalidOperationException($"NpcSpawn (id {row.Id}, type '{row.NpcTemplate.Name}') is an equipped no-morph humanoid whose type has no derived MinimalPostIirWord2.");

			return BuildBody(handle, x, row.Position, row.Rotation, playfieldToken, message, morphs, minimalWord2);
		}

		// Constructs the WHOLE ProtoCoreCharacterIIR message for a spawn from typed columns/rows (#244) - no captured
		// Data: Name from the type, the stat core from StatCore + placement (ApplyStatCore), worn equipment from the
		// NpcSpawnEquipment rows. Runtime/target fields are omitted (canonical fresh spawn).
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
		// NpcTemplate.StatCore (no per-placement overrides - a type has no placement) + worn equipment from
		// NpcTemplate.Equipment.
		public static ProtoCoreCharacterIIR BuildMessageFromType(NpcTemplate npc)
		{
			var core = npc.StatCore!;
			var msg = new ProtoCoreCharacterIIR
			{
				Name = npc.Name ?? "",
				Stats = NpcStatCoreColumns.ToStats(core),
				npc_stats = NpcStatCoreColumns.ToNpcStats(core),
				Bcc = NpcStatCoreColumns.ToBcc(core)
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

		// Constructs the CharacterIIR stat core (#244) from the type-level NpcTemplate.StatCore + this placement's
		// overrides, replacing whatever was parsed from Data. Type-level fields (senses defaults, flags, race, anim
		// clips, ...) come from the StatCore; the per-placement values override from the NpcSpawn columns: the
		// always-present pools (Health/Life/Mana/... + Level/scale/voice/mesh) are set unconditionally, and the
		// sparse ones (GuildId + Hearing/SightSkill) are RESET off the type value first, then set only if this
		// placement carried them - so a spawn that lacks one doesn't inherit the representative's. No-op when the
		// type hasn't been lifted yet.
		private static void ApplyStatCore(ProtoCoreCharacterIIR msg, NpcSpawn row)
		{
			if (row?.NpcTemplate?.StatCore is not { } core)
				return;

			var s = NpcStatCoreColumns.ToStats(core);
			s.HeadMesh = row.HeadMesh;
			// The wire CharacterStats.Health is the ×100 max-HP value, NOT NpcSpawn.Health (the player-facing DISPLAY
			// value kept for gameplay). For ~all creatures wire Health == wire Life and tracks the per-placement
			// level, so drive it from the lifted wire Life; the rare divergent per-placement value (Rocknose) is in
			// WireHealth.
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

			var n = NpcStatCoreColumns.ToNpcStats(core);
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

			msg.Bcc = NpcStatCoreColumns.ToBcc(core);
			if (core.CanFall.HasValue)
				msg.CanFall = core.CanFall.Value;
			if (core.MovementMode.HasValue)
				msg.MovementMode = core.MovementMode.Value;
		}
	}
}
