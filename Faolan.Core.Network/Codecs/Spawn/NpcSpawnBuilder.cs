using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Tables;
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
		// form.
		//
		// This is the sole NPC spawn-body path (the captured Data blob was retired): every shipped spawn is an
		// animal, a clean-appearance humanoid, or a no-morph spawn expressible from columns (NpcSpawnFromScratchTests).
		//
		// ⚠️ The post-IIR's movement mode is DERIVED from the message rather than passed in, and that is the whole
		// point of it being a single expression here: the value occurs twice in one spawn body (SpawnPostIir
		// .MovementMode) and retail sends the same number in both places on 5,738 of 5,739 captured NPC spawns and on
		// all 209 player ones. It used to arrive from a second stored column, which let the two disagree - 45 of the
		// 368 shipped types carried a different number in each, so those spawns went out in a shape retail never sent
		// (#605).
		//
		// The u32 carrier length belongs to the SENDER (#530 rule 2) - what comes back is the record.
		public static byte[] BuildBody(uint handle, uint iirHeader, Vector3 position, Quaternion rotation, uint playfieldToken, ProtoCoreCharacterIIR message, IReadOnlyList<NpcAppearanceMorph> morphs)
		{
			var movementMode = message.ShouldSerializeMovementMode() ? message.MovementMode : SpawnPostIirs.DefaultMovementMode;
			return SimpleCharFullUpdateCodec.Build(new SimpleCharFullUpdateIIR
			{
				Handle = handle,
				Position = position,
				Rotation = rotation,
				Playfield = playfieldToken,
				PreIir = SpawnPreIirs.Minimal(),
				CoreCharIirHeader = iirHeader,
				Character = message,
				Post = SpawnPostIirs.Npc(movementMode, morphs is { Count: > 0 } ? morphs : null)
			});
		}

		// ⭐ #803: stamps `npc_stats.spawn_animation` into an ALREADY-BUILT spawn record - the client's one-shot
		// "play your entrance animation now" trigger (`SimpleChar_t` slot 36 plays `spawnanim`, falls back to
		// `defaultidleanim` when the creature declares none, then clears the stat itself; #800).
		//
		// Why a re-stamp of a built body rather than a `BuildLive` parameter: the flag is a property of ONE DELIVERY,
		// not of the entity, and the spawn body is built once at map load and re-sent for the entity's whole life. A
		// build parameter would have to be decided at map load, which is the one moment it is never true.
		//
		// A re-encode rather than a byte patch, because the field lives inside the embedded protobuf and setting it
		// changes lengths at three nesting levels. That costs a parse+build per respawn delivery and is safe by the
		// same gate everything else here rests on: `Parse` -> `Build` is byte-exact over every captured spawn body
		// (ProtobufCorpusRoundTripTests / the codec round-trip gate), so a record that came out of `BuildBody`
		// survives the trip unchanged apart from the one field.
		//
		// A body with no `npc_stats` at all (a player record) comes back untouched - the flag has no slot there.
		public static byte[] WithSpawnAnimation(byte[] body)
		{
			var record = SimpleCharFullUpdateCodec.Parse(body);
			if (record.Character?.npc_stats is not { } npc)
				return body;

			// Retail never sends 0 - the field is UNSET or 1, i.e. a presence flag (#607).
			npc.SpawnAnimation = 1;

			return SimpleCharFullUpdateCodec.Build(record);
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
		// `wearing` overrides the type's own appearance with ONE HARVESTED PLACEMENT's (#836). Retail dresses the
		// individuals of a type differently - the three captured beach Poachers wear three different kits, sharing only
		// their leather legs - so a pool of eight built from the single type default is a uniform that retail never
		// fields. The type's appearance stays the fallback for a creature we have no placement of (a summoned pet).
		public static byte[] BuildFromType(NpcTemplate npc, uint handle, Vector3 position, Quaternion rotation, uint playfieldToken, NpcSpawn? wearing = null)
		{
			if (npc?.StatCore?.IirHeader is not { } x)
				throw new InvalidOperationException(
					$"NpcTemplate '{npc?.Name ?? "?"}' (id {npc?.TemplateId}) has no lifted StatCore.IirHeader - the type is not imported/lifted (or StatCore was not Included).");

			var morphSource = wearing?.Morphs is { Count: > 0 }
				? wearing.Morphs.OrderBy(m => m.Ordinal).Select(m => (m.MorphId, m.Value, m.Word2, m.Text))
				: npc.Morphs.OrderBy(m => m.Ordinal).Select(m => (m.MorphId, m.Value, m.Word2, m.Text));
			var morphs = morphSource
				.Select(m => new NpcAppearanceMorph
				{
					MorphId = m.MorphId,
					Value = m.Value,
					Word2 = m.Word2, // #672: carried, or the two tables that use it would round-trip lossily
					Text = m.Text ?? ""
				})
				.ToList();
			var message = BuildMessageFromType(npc, wearing);
			return BuildBody(handle, x, position, rotation, playfieldToken, message, morphs);
		}

		// The LIVE spawn path (#244/#243): assemble the whole record from typed columns with `playfieldToken` baked
		// into the transform - no captured Data, no SpawnTransformPatch. Every shipped spawn is expressible from
		// scratch: an ANIMAL (no equipment -> minimal post-IIR), a humanoid whose APPEARANCE is lifted into morph
		// rows (-> the appearance table), or a NO-MORPH spawn whose type carries a known minimal word2 (#243).
		// THROWS for a row with no lifted type (no StatCore.IirHeader) - which does not occur in the shipped DB
		// (988/988 build), so a throw signals un-lifted / partially-imported data or a missing Include, not a normal
		// outcome.
		// NB the dev nameplate LABEL (#363) is NOT baked into the spawn name here: the client draws an NPC's floating
		// name from its template_id LDB, ignoring the spawn-body Name, so the label is pushed separately as a
		// SetNameIIR after the spawn (WorldBroadcaster.SendWorldSpawn / SetNameCodec).
		// `movementMode` overrides the type's stored one. It has to be overridable because it is NOT type identity:
		// retail sends BOTH 1 and 2 for the same creature - 387 of 834 captured NPC names carry more than one value,
		// Nobleman alone 2x213 and 1x79, and the overall split is 9,334 / 8,016 / 50 over 17,400 spawns. It is
		// per-spawn-event STATE (how the creature is moving right now), which #244 lifted into the TYPE core, so every
		// spawn of a type went out locked to one value. See #714.
		//
		// `animIdleId` overrides the type's held POSE, and is overridable for exactly the same reason: it is state, not
		// identity. `NpcStatCore.AnimIdleId` is documented as "the pose it holds", and a creature whose pose depends on
		// the story holds different ones at different times - Casilda's 156196 is HER chained struggle (unique across
		// all 1,208 templates, where the generic idle 42024 is shared by 1,008 of them), and once freed she holds the
		// generic one. Retail's own captures show both spellings of her spawn body (#827).
		//
		// `bccState` overrides the type's BEHAVIOUR-CONTROL state - `NpcStatCore.BccState`, which the client calls
		// "BCC State" (CharacterStatId 0x14). It decides which behaviour tree runs, so it is state in the same way the
		// pose is: 1,186 templates carry the ordinary 1, and Casilda's 80 is her CAPTIVE tree. Retail flips it to 1 by
		// StatUpdate at the moment of her rescue (#827).
		public static byte[] BuildLive(NpcSpawn row, uint handle, uint playfieldToken, uint? movementMode = null, uint? animIdleId = null, uint? bccState = null)
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
					Word2 = m.Word2, // #672: carried, or the two tables that use it would round-trip lossily
					Text = m.Text ?? ""
				})
				.ToList();

			var message = BuildMessageFromColumns(row);
			if (movementMode is { } mode)
				message.MovementMode = mode;
			if (animIdleId is { } idle)
				(message.Bcc ??= new ProtoCoreCharacterIIR.CharacterBcc()).AnimIdleId = idle;
			if (bccState is { } bcc)
				(message.Stats ??= new ProtoCoreCharacterIIR.CharacterStats()).BccState = bcc;

			return BuildBody(handle, x, row.Position, row.Rotation, playfieldToken, message, morphs);
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
		public static ProtoCoreCharacterIIR BuildMessageFromType(NpcTemplate npc, NpcSpawn? wearing = null)
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
			// ...and this individual's VOICE with the outfit. Retail rolls VoiceSet per spawn, not per type: it scores
			// 0% stable at BOTH type and placement scope over the corpus (`--report field-stability`), five enumerated
			// values with 2 the modal one, and a Poacher Marksman alone was captured with four of them. A pool that
			// speaks with one voice is as wrong as one that wears one outfit, and it costs nothing to carry it on the
			// placement we are already drawing from.
			if (wearing != null && msg.Stats != null)
				msg.Stats.VoiceSet = wearing.VoiceSet;

			// This individual's kit when one was handed in, the type's otherwise. Not merged: a kit is a whole outfit,
			// and mixing one NPC's chest with another's would dress a creature retail never dressed.
			if (wearing?.Equipment is { Count: > 0 } worn)
			{
				foreach (var entry in NpcSpawnEquipmentBuilder.BuildEntries(worn))
					msg.InventoryEntries.Add(entry);
			}
			else if (npc.Equipment is { Count: > 0 })
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
