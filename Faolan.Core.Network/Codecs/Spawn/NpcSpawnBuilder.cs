using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Tables;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Spawn
{
	/// <summary>
	///     Maps an NPC placement (an NpcSpawn row) or a creature type (an NpcTemplate) onto the wire spawn value
	///     <see cref="SimpleCharFullUpdateCodec" /> encodes. The transform comes from the row's columns, the CharacterIIR
	///     stat core from NpcTemplate.StatCore, worn equipment from the equipment rows and morphs from the morph rows,
	///     while the pre-IIR and post-IIR scaffolds are generated from scratch.
	/// </summary>
	/// <remarks>
	///     A row-to-value mapper rather than a codec: every method here ends in SimpleCharFullUpdateCodec.Build, which is
	///     the one encoder for this IIR.
	/// </remarks>
	public static class NpcSpawnBuilder
	{
		/// <summary>
		///     Assembles a whole NPC spawn record from scratch, with no captured template. The playfield token is written
		///     into the transform directly rather than patched afterwards, the pre-IIR is the minimal generated scaffold, and
		///     the post-IIR is the appearance/morph table when <paramref name="morphs" /> are given and the minimal form
		///     otherwise.
		/// </summary>
		/// <remarks>
		///     This is the sole NPC spawn-body path: every shipped spawn is an animal, a clean-appearance humanoid, or a
		///     no-morph spawn expressible from columns (NpcSpawnFromScratchTests).
		///     <para>
		///         The post-IIR's movement mode is derived from the message rather than passed in, and must stay a single
		///         expression: the value occurs twice in one spawn body, and retail sends the same number in both places on
		///         5,738 of 5,739 captured NPC spawns and on all 209 player ones. Sourcing the second from its own stored
		///         column lets the two disagree, which produces a shape retail never sent.
		///     </para>
		///     <para>The u32 carrier length belongs to the framing sender; what comes back is the record.</para>
		/// </remarks>
		public static byte[] BuildBody(uint handle, uint iirHeader, Vector3 position, Quaternion rotation, uint playfieldToken, ProtoCoreCharacterIIR message, IReadOnlyList<AppearanceEntry> morphs)
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

		/// <summary>
		///     Stamps <c>npc_stats.spawn_animation</c> into an already-built spawn record - the client's one-shot "play your
		///     entrance animation now" trigger. SimpleChar_t slot 36 plays <c>spawnanim</c>, falls back to
		///     <c>defaultidleanim</c> when the creature declares none, and then clears the stat itself.
		/// </summary>
		/// <remarks>
		///     It re-stamps a built body rather than taking a BuildLive parameter because the flag is a property of one
		///     delivery, not of the entity: the spawn body is built once at map load and re-sent for the entity's whole life,
		///     and map load is the one moment the flag is never true.
		///     <para>
		///         It re-encodes rather than patching bytes because the field lives inside the embedded protobuf and setting
		///         it changes lengths at three nesting levels. That costs a parse and build per respawn delivery and is safe
		///         by the gate everything else here rests on: Parse into Build is byte-exact over every captured spawn body
		///         (ProtobufCorpusRoundTripTests and the codec round-trip gate), so a record out of
		///         <see cref="BuildBody" /> survives the trip unchanged apart from the one field.
		///     </para>
		///     <para>A body with no npc_stats at all - a player record - comes back untouched, the flag having no slot there.</para>
		/// </remarks>
		public static byte[] WithSpawnAnimation(byte[] body)
		{
			var record = SimpleCharFullUpdateCodec.Parse(body);
			if (record.Character?.npc_stats is not { } npc)
				return body;

			// Retail never sends 0: the field is unset or 1, i.e. a presence flag.
			npc.SpawnAnimation = 1;

			return SimpleCharFullUpdateCodec.Build(record);
		}

		/// <summary>
		///     Builds a spawn record for a creature type at a caller-supplied transform: the spawn-from-type path, with no
		///     world placement. A summoned pet or mount and an ambient-pool member use it - the type carries the lifted stat
		///     core, worn equipment, appearance morphs and the no-morph movement mode, and the whole record is assembled from
		///     scratch at the summoner's position. Throws if the type has no lifted stat core.
		/// </summary>
		/// <remarks>
		///     <paramref name="playfieldToken" /> must be the spawning zone's live token (Zone.PlayfieldToken). It is baked
		///     into the transform and re-stamped per recipient later by <see cref="SpawnTransformPatch" />, but that
		///     rewriter can only overwrite a token that is already a 4-byte varint: passing 0 leaves the token
		///     un-re-stampable and makes the transform three bytes shorter, desyncing the client's version read into a crash.
		///     Zone tokens are always 4-byte, so passing the zone token keeps the record wire-correct.
		///     <para>
		///         <paramref name="wearing" /> overrides the type's own appearance with one harvested placement's. Retail
		///         dresses the individuals of a type differently - the three captured beach Poachers wear three different
		///         kits, sharing only their leather legs - so a pool built from the single type default is a uniform retail
		///         never fields. The type's appearance stays the fallback for a creature with no placement, such as a summoned
		///         pet.
		///     </para>
		/// </remarks>
		public static byte[] BuildFromType(NpcTemplate npc, uint handle, Vector3 position, Quaternion rotation, uint playfieldToken, NpcSpawn? wearing = null,
			uint? normalsetOverride = null)
		{
			if (npc?.StatCore?.IirHeader is not { } x)
				throw new InvalidOperationException(
					$"NpcTemplate '{npc?.Name ?? "?"}' (id {npc?.TemplateId}) has no lifted StatCore.IirHeader - the type is not imported/lifted (or StatCore was not Included).");

			var morphSource = wearing?.Morphs is { Count: > 0 }
				? wearing.Morphs.OrderBy(m => m.Ordinal).Select(m => (m.MorphId, m.Value, m.Word2, m.Text))
				: npc.Morphs.OrderBy(m => m.Ordinal).Select(m => (m.MorphId, m.Value, m.Word2, m.Text));
			var morphs = morphSource
				.Select(m => new AppearanceEntry
				{
					MorphId = m.MorphId,
					Value = m.Value,
					Word2 = m.Word2, // carried, or the two tables that use it would round-trip lossily
					Text = m.Text ?? ""
				})
				.ToList();
			var message = BuildMessageFromType(npc, wearing);
			if (normalsetOverride is { } typeNormalset)
				(message.npc_stats ??= new ProtoCoreCharacterIIR.NpcStats()).Normalset = typeNormalset;
			return BuildBody(handle, x, position, rotation, playfieldToken, message, morphs);
		}

		/// <summary>
		///     The live spawn path: assembles the whole record from typed columns with <paramref name="playfieldToken" />
		///     baked into the transform. Every shipped spawn is expressible from scratch - an animal (no equipment, so the
		///     minimal post-IIR), a humanoid whose appearance is lifted into morph rows (the appearance table), or a no-morph
		///     spawn whose type carries a known minimal movement mode.
		/// </summary>
		/// <remarks>
		///     It throws for a row with no lifted type (no StatCore.IirHeader), which does not occur in the shipped database,
		///     so a throw signals un-lifted or partially imported data, or a missing Include, rather than a normal outcome.
		///     <para>
		///         The dev nameplate label is deliberately not baked into the spawn name: the client draws an NPC's floating
		///         name from its template_id LDB and ignores the spawn-body Name, so the label is pushed separately as a
		///         SetNameIIR after the spawn.
		///     </para>
		///     <para>
		///         The four overrides are all per-spawn state rather than type identity, which is why they are parameters.
		///         <paramref name="movementMode" /> is how the creature is moving right now: retail sends both 1 and 2 for the
		///         same creature - 387 of 834 captured NPC names carry more than one value, and the overall split is
		///         9,334 / 8,016 / 50 over 17,400 spawns - so a type-level value locks every spawn of that type to one number.
		///         <paramref name="animIdleId" /> is the pose it holds, and a creature whose pose depends on the story holds
		///         different ones at different times: Casilda's 156196 is her chained struggle, unique across all 1,208
		///         templates where the generic idle 42024 is shared by 1,008 of them, and once freed she holds the generic one.
		///         <paramref name="bccState" /> is the behaviour-control state the client calls "BCC State" (CharacterStatId
		///         0x14), which decides which behaviour tree runs: 1,186 templates carry the ordinary 1, and Casilda's 80 is
		///         her captive tree, which retail flips to 1 by StatUpdate at the moment of her rescue.
		///         <paramref name="normalsetOverride" /> is the nameplate decoration set (NpcStats.normalset, the skull and
		///         frame selector), which retail sends absent in a normal instance and 13 - the elite frame - in the epic copy
		///         of the same map.
		///     </para>
		/// </remarks>
		public static byte[] BuildLive(NpcSpawn row, uint handle, uint playfieldToken, uint? movementMode = null, uint? animIdleId = null, uint? bccState = null,
			uint? normalsetOverride = null)
		{
			if (row?.NpcTemplate?.StatCore?.IirHeader is not { } x)
				throw new InvalidOperationException(
					$"NpcSpawn (id {row?.Id}, type '{row?.NpcTemplate?.Name ?? "?"}') has no lifted StatCore.IirHeader - the type is not imported/lifted (or StatCore was not Included).");

			var morphs = row
				.Morphs.OrderBy(m => m.Ordinal)
				.Select(m => new AppearanceEntry
				{
					MorphId = m.MorphId,
					Value = m.Value,
					Word2 = m.Word2, // carried, or the two tables that use it would round-trip lossily
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
			if (normalsetOverride is { } normalset)
				(message.npc_stats ??= new ProtoCoreCharacterIIR.NpcStats()).Normalset = normalset;

			return BuildBody(handle, x, row.Position, row.Rotation, playfieldToken, message, morphs);
		}

		/// <summary>
		///     Constructs the whole ProtoCoreCharacterIIR message for a spawn from typed columns and rows: the name from the
		///     type, the stat core from StatCore plus the placement (<see cref="ApplyStatCore" />), and worn equipment from
		///     the NpcSpawnEquipment rows. Runtime and target fields are omitted, giving the canonical fresh spawn.
		/// </summary>
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

		/// <summary>
		///     Constructs the whole ProtoCoreCharacterIIR for a creature type: the name, the stat core straight from
		///     NpcTemplate.StatCore with no per-placement overrides, and worn equipment from NpcTemplate.Equipment.
		/// </summary>
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
			// This individual's voice travels with the outfit. Retail rolls VoiceSet per spawn rather than per type: it
			// scores 0% stable at both type and placement scope over the corpus (--report field-stability), across five
			// enumerated values with 2 the modal one, and a single Poacher Marksman was captured with four of them.
			if (wearing != null && msg.Stats != null)
				msg.Stats.VoiceSet = wearing.VoiceSet;

			// This individual's kit when one was handed in, the type's otherwise. Not merged: a kit is a whole outfit,
			// and mixing one NPC's chest with another's dresses a creature retail never dressed.
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

		/// <summary>
		///     Constructs the CharacterIIR stat core from the type-level NpcTemplate.StatCore plus this placement's
		///     overrides. Type-level fields - sense defaults, flags, race, animation clips - come from the StatCore, while
		///     the per-placement values override from the NpcSpawn columns: the always-present pools (health, life, mana,
		///     level, scale, voice, mesh) are set unconditionally, and the sparse ones (guild id, hearing and sight skill)
		///     are reset off the type value first and set only if this placement carried them, so a spawn that lacks one does
		///     not inherit the representative's. A no-op when the type has not been lifted yet.
		/// </summary>
		private static void ApplyStatCore(ProtoCoreCharacterIIR msg, NpcSpawn row)
		{
			if (row?.NpcTemplate?.StatCore is not { } core)
				return;

			var s = NpcStatCoreColumns.ToStats(core);
			s.HeadMesh = row.HeadMesh;
			// The wire CharacterStats.Health is the x100 max-HP value, not NpcSpawn.Health, which is the player-facing
			// display value kept for gameplay. For almost every creature the wire Health equals the wire Life and
			// tracks the per-placement level, so it is driven from the lifted wire Life; the rare divergent
			// per-placement value lives in WireHealth.
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
