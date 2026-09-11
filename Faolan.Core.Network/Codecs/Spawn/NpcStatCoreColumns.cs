using System;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Spawn
{
	/// <summary>
	///     Converts between a captured CharacterIIR's stat-core sub-messages (CharacterStats, NpcStats, CharacterBcc) and
	///     the typed NpcStatCore side table, so an NPC spawn body can be constructed from the database rather than spliced
	///     from a captured blob.
	/// </summary>
	/// <remarks>
	///     The mapping is explicit per field rather than reflective: it runs in the per-spawn build path, and a
	///     hand-written map is compile-checked, so a proto field that gains no column is a build error rather than a
	///     silently dropped field.
	///     <para>
	///         Presence is preserved exactly - a field is copied only when ShouldSerialize is true on the lift side, or the
	///         column is non-null on the build side - so an unset field stays off the wire, which is what keeps the rebuilt
	///         core byte-identical to the capture (NpcStatCoreColumnsTests).
	///     </para>
	/// </remarks>
	public static class NpcStatCoreColumns
	{
		/// <summary>
		///     Captured message to typed columns, the importer side. A column stays null unless the proto field was set.
		/// </summary>
		public static NpcStatCore Lift(ProtoCoreCharacterIIR m)
		{
			var s = m.Stats;
			var n = m.npc_stats;
			var b = m.Bcc;
			var core = new NpcStatCore();

			if (s != null)
			{
				if (s.ShouldSerializeSex())
					core.Sex = (Sex)s.Sex;
				if (s.ShouldSerializeSide())
					core.Side = s.Side;
				if (s.ShouldSerializeRace())
					core.Race = s.Race;
				if (s.ShouldSerializeMonsterScale())
					core.MonsterScale = s.MonsterScale;
				if (s.ShouldSerializeMonsterData())
					core.MonsterData = s.MonsterData;
				if (s.ShouldSerializeLife())
					core.Life = s.Life;
				if (s.ShouldSerializeHealth())
					core.Health = s.Health;
				if (s.ShouldSerializeMaxStamina())
					core.MaxStamina = s.MaxStamina;
				if (s.ShouldSerializeStamina())
					core.Stamina = s.Stamina;
				if (s.ShouldSerializeMaxMana())
					core.MaxMana = s.MaxMana;
				if (s.ShouldSerializeMana())
					core.Mana = s.Mana;
				if (s.ShouldSerializeLooksPackage())
					core.LooksPackage = s.LooksPackage;
				if (s.ShouldSerializeMountMesh())
					core.MountMesh = s.MountMesh;
				if (s.ShouldSerializeMountScale())
					core.MountScale = s.MountScale;
				if (s.ShouldSerializeMountFlags())
					core.MountFlags = s.MountFlags;
				if (s.ShouldSerializeFlags())
					core.Flags = s.Flags;
				if (s.ShouldSerializeHeadMesh())
					core.HeadMesh = s.HeadMesh;
				if (s.ShouldSerializeLevel())
					core.Level = s.Level;
				if (s.ShouldSerializeApprenticeLevel())
					core.ApprenticeLevel = s.ApprenticeLevel;
				if (s.ShouldSerializeRadius())
					core.Radius = s.Radius;
				if (s.ShouldSerializeBcc())
					core.Bcc = s.Bcc;
				if (s.ShouldSerializeBccState())
					core.BccState = s.BccState;
				if (s.ShouldSerializeBloodType())
					core.BloodType = s.BloodType;
				if (s.ShouldSerializeSkulk())
					core.Skulk = s.Skulk;
				if (s.ShouldSerializeMaxSkulk())
					core.MaxSkulk = s.MaxSkulk;
				if (s.ShouldSerializeTradeskillFlags())
					core.TradeskillFlags = s.TradeskillFlags;
				if (s.ShouldSerializeClientScryScript())
					core.ClientScryScript = s.ClientScryScript;
				if (s.ShouldSerializeSoundScryScript())
					core.SoundScryScript = s.SoundScryScript;
				if (s.ShouldSerializeCarsGroup())
					core.CarsGroup = s.CarsGroup;
				if (s.ShouldSerializeMovementMultiplier())
					core.MovementMultiplier = s.MovementMultiplier;
				if (s.ShouldSerializeOocMovementMultiplier())
					core.OocMovementMultiplier = s.OocMovementMultiplier;
				if (s.ShouldSerializeRunSpeed())
					core.RunSpeed = s.RunSpeed;
				if (s.ShouldSerializeMaxRunSpeed())
					core.MaxRunSpeed = s.MaxRunSpeed;
				if (s.ShouldSerializeOrientationMode())
					core.OrientationMode = s.OrientationMode;
				if (s.ShouldSerializeGuildId())
					core.GuildId = s.GuildId;
				if (s.ShouldSerializeVoiceSet())
					core.VoiceSet = s.VoiceSet;
				if (s.ShouldSerializeChatId())
					core.ChatId = s.ChatId;
			}

			if (n != null)
			{
				if (n.ShouldSerializeSightSkill())
					core.SightSkill = n.SightSkill;
				if (n.ShouldSerializeHearingSkill())
					core.HearingSkill = n.HearingSkill;
				if (n.ShouldSerializeSmellingSkill())
					core.SmellingSkill = n.SmellingSkill;
				if (n.ShouldSerializeTemplateId())
					core.TemplateId = n.TemplateId;
				if (n.ShouldSerializeSuperFamily())
					core.SuperFamily = n.SuperFamily;
				if (n.ShouldSerializeCommunity())
					core.Community = n.Community;
				if (n.ShouldSerializeLosHeight())
					core.LosHeight = n.LosHeight;
				if (n.ShouldSerializeSpawnAnimation())
					core.SpawnAnimation = n.SpawnAnimation;
				if (n.ShouldSerializeNpcFlags())
					core.NpcFlags = n.NpcFlags;
				if (n.ShouldSerializeNormalset())
					core.Normalset = n.Normalset;
				if (n.ShouldSerializeProfessionName())
					core.ProfessionName = n.ProfessionName;
				if (n.ShouldSerializeProfessionType())
					core.ProfessionType = n.ProfessionType;
				if (n.ShouldSerializeFactionId())
					core.FactionId = n.FactionId;
				if (n.ShouldSerializeNpcType())
					core.NpcType = n.NpcType;
			}

			if (b != null)
			{
				if (b.ShouldSerializeAnimIdleId())
					core.AnimIdleId = b.AnimIdleId;
				if (b.ShouldSerializeAnimIdleTimer())
					core.AnimIdleTimer = b.AnimIdleTimer;
				if (b.ShouldSerializeAnimIdleBlend())
					core.AnimIdleBlend = b.AnimIdleBlend;
				if (b.ShouldSerializeAnimActionId())
					core.AnimActionId = b.AnimActionId;
				if (b.ShouldSerializeAnimActionTimer())
					core.AnimActionTimer = b.AnimActionTimer;
				if (b.ShouldSerializeAnimActionBlend())
					core.AnimActionBlend = b.AnimActionBlend;
				if (b.ShouldSerializeAnimForcedId())
					core.AnimForcedId = b.AnimForcedId;
				if (b.ShouldSerializeAnimForcedTimer())
					core.AnimForcedTimer = b.AnimForcedTimer;
				if (b.ShouldSerializeAnimForcedBlend())
					core.AnimForcedBlend = b.AnimForcedBlend;
				if (b.ShouldSerializeAnimFlags())
					core.AnimFlags = b.AnimFlags;
			}

			if (m.ShouldSerializeCanFall())
				core.CanFall = m.CanFall;
			if (m.ShouldSerializeMovementMode())
				core.MovementMode = m.MovementMode;
			return core;
		}

		/// <summary>
		///     Resets the action-animation slot to its resting value, for a core that is about to become a type row.
		///     <para>
		///         <see cref="Lift" /> is faithful to the message it is handed and must stay that way - it is the
		///         message-to-columns map, and NpcStatCoreColumnsTests.StatCore_RoundTripsByteExact gates that it loses
		///         nothing. This operates one level up: a type row is lifted from one representative spawn, so a field with
		///         no type-level value at all still gets one - whatever that NPC happened to be doing at the instant it was
		///         captured - and every other placement of the type then inherits it.
		///     </para>
		///     <para>
		///         The action slot is unambiguously that. Only 31 of 1018 lifted types carry <c>AnimActionId</c> at all;
		///         retail sends 0 on 93% of spawns, <c>AnimActionTimer</c> 0 on 92% and <c>AnimActionBlend</c> false on 95%
		///         (<c>--report field-stability</c>, third table), so a non-zero value means the representative was caught
		///         part-way through an emote and says nothing about the type.
		///     </para>
		///     <para>
		///         Presence is preserved: a field is reset only where the type actually carried one, and never introduced.
		///         Nulling it instead would drop it from the rebuilt message and change the body's length, which is a bigger
		///         divergence from retail than the value ever was.
		///     </para>
		///     <para>
		///         <c>AnimIdleTimer</c> is deliberately not reset. It looks like the clearest case - 50.2% type and 45.2%
		///         placement stability, with 92 of its 94 distinct values strictly between 0 and 1, which reads as an
		///         animation phase sampled at a random instant - but weighted by how often retail sends them those 92 carry
		///         only 24% of sightings: the field rests at exactly 0 (264 times) or exactly 1 (269 times) and is merely
		///         caught in between mid-blend. Which endpoint a type rests at is real type-level information; flattening all
		///         1018 types to 0 drops <c>--report spawn-diff</c> from 340 byte-exact rebuilds to 173.
		///     </para>
		///     <para>
		///         <c>MovementMode</c> (59.0/60.6) and <c>npc_stats.SpawnAnimation</c> (86.2/74.4) also measure as
		///         per-spawn-event and are also left alone: neither has a resting value clear enough to reset to -
		///         MovementMode's mode carries 69% of spawns - and picking the mode would be the same guess this method
		///         exists to remove.
		///     </para>
		/// </summary>
		public static void ClearPerSpawnEventState(NpcStatCore core)
		{
			if (core.AnimActionTimer.HasValue)
				core.AnimActionTimer = 0;
			if (core.AnimActionId.HasValue)
				core.AnimActionId = 0;
			if (core.AnimActionBlend.HasValue)
				core.AnimActionBlend = false;
		}

		/// <summary>
		///     Reads the CoreCharacter-IIR header word out of a captured spawn body: the embedded message is preceded by
		///     <c>[u8 version][u32 header][u32 msgLen]</c>, and that header word is a type-stable field-presence and shape
		///     mask, lifted so the header can be rebuilt from the database. Returns null when the body is not a decodable
		///     spawn.
		/// </summary>
		/// <remarks>
		///     <paramref name="body" /> is the raw wire body including its leading u32 carrier length, which the codec does
		///     not own, so the length is stripped here, at the boundary between a captured body and the record.
		/// </remarks>
		public static uint? ReadIirHeaderWord(byte[] body)
		{
			if (body == null || body.Length < 4)
				return null;

			try
			{
				return SimpleCharFullUpdateCodec.Parse(body.AsSpan(4)).CoreCharIirHeader;
			}
			catch (CodecParseException)
			{
				return null;
			}
		}

		/// <summary>Typed columns to CharacterBcc, the build side.</summary>
		public static ProtoCoreCharacterIIR.CharacterBcc ToBcc(NpcStatCore c)
		{
			var b = new ProtoCoreCharacterIIR.CharacterBcc();
			if (c.AnimIdleId.HasValue)
				b.AnimIdleId = c.AnimIdleId.Value;
			if (c.AnimIdleTimer.HasValue)
				b.AnimIdleTimer = c.AnimIdleTimer.Value;
			if (c.AnimIdleBlend.HasValue)
				b.AnimIdleBlend = c.AnimIdleBlend.Value;
			if (c.AnimActionId.HasValue)
				b.AnimActionId = c.AnimActionId.Value;
			if (c.AnimActionTimer.HasValue)
				b.AnimActionTimer = c.AnimActionTimer.Value;
			if (c.AnimActionBlend.HasValue)
				b.AnimActionBlend = c.AnimActionBlend.Value;
			if (c.AnimForcedId.HasValue)
				b.AnimForcedId = c.AnimForcedId.Value;
			if (c.AnimForcedTimer.HasValue)
				b.AnimForcedTimer = c.AnimForcedTimer.Value;
			if (c.AnimForcedBlend.HasValue)
				b.AnimForcedBlend = c.AnimForcedBlend.Value;
			if (c.AnimFlags.HasValue)
				b.AnimFlags = c.AnimFlags.Value;
			return b;
		}

		/// <summary>Typed columns to NpcStats, the build side.</summary>
		public static ProtoCoreCharacterIIR.NpcStats ToNpcStats(NpcStatCore c)
		{
			var n = new ProtoCoreCharacterIIR.NpcStats();
			if (c.SightSkill.HasValue)
				n.SightSkill = c.SightSkill.Value;
			if (c.HearingSkill.HasValue)
				n.HearingSkill = c.HearingSkill.Value;
			if (c.SmellingSkill.HasValue)
				n.SmellingSkill = c.SmellingSkill.Value;
			if (c.TemplateId.HasValue)
				n.TemplateId = c.TemplateId.Value;
			if (c.SuperFamily.HasValue)
				n.SuperFamily = c.SuperFamily.Value;
			if (c.Community.HasValue)
				n.Community = c.Community.Value;
			if (c.LosHeight.HasValue)
				n.LosHeight = c.LosHeight.Value;
			if (c.SpawnAnimation.HasValue)
				n.SpawnAnimation = c.SpawnAnimation.Value;
			if (c.NpcFlags.HasValue)
				n.NpcFlags = c.NpcFlags.Value;
			if (c.Normalset.HasValue)
				n.Normalset = c.Normalset.Value;
			if (c.ProfessionName.HasValue)
				n.ProfessionName = c.ProfessionName.Value;
			if (c.ProfessionType.HasValue)
				n.ProfessionType = c.ProfessionType.Value;
			if (c.FactionId.HasValue)
				n.FactionId = c.FactionId.Value;
			if (c.NpcType.HasValue)
				n.NpcType = c.NpcType.Value;
			return n;
		}

		/// <summary>
		///     Typed columns to CharacterStats, the build side. Only the non-null columns are set, so ShouldSerialize matches
		///     the original and a field that was unset stays off the wire.
		/// </summary>
		public static ProtoCoreCharacterIIR.CharacterStats ToStats(NpcStatCore c)
		{
			var s = new ProtoCoreCharacterIIR.CharacterStats();
			if (c.Sex.HasValue)
				s.Sex = (uint)c.Sex.Value;
			if (c.Side.HasValue)
				s.Side = c.Side.Value;
			if (c.Race.HasValue)
				s.Race = c.Race.Value;
			if (c.MonsterScale.HasValue)
				s.MonsterScale = c.MonsterScale.Value;
			if (c.MonsterData.HasValue)
				s.MonsterData = c.MonsterData.Value;
			if (c.Life.HasValue)
				s.Life = c.Life.Value;
			if (c.Health.HasValue)
				s.Health = c.Health.Value;
			if (c.MaxStamina.HasValue)
				s.MaxStamina = c.MaxStamina.Value;
			if (c.Stamina.HasValue)
				s.Stamina = c.Stamina.Value;
			if (c.MaxMana.HasValue)
				s.MaxMana = c.MaxMana.Value;
			if (c.Mana.HasValue)
				s.Mana = c.Mana.Value;
			if (c.LooksPackage.HasValue)
				s.LooksPackage = c.LooksPackage.Value;
			if (c.MountMesh.HasValue)
				s.MountMesh = c.MountMesh.Value;
			if (c.MountScale.HasValue)
				s.MountScale = c.MountScale.Value;
			if (c.MountFlags.HasValue)
				s.MountFlags = c.MountFlags.Value;
			if (c.Flags.HasValue)
				s.Flags = c.Flags.Value;
			if (c.HeadMesh.HasValue)
				s.HeadMesh = c.HeadMesh.Value;
			if (c.Level.HasValue)
				s.Level = c.Level.Value;
			if (c.ApprenticeLevel.HasValue)
				s.ApprenticeLevel = c.ApprenticeLevel.Value;
			if (c.Radius.HasValue)
				s.Radius = c.Radius.Value;
			if (c.Bcc.HasValue)
				s.Bcc = c.Bcc.Value;
			if (c.BccState.HasValue)
				s.BccState = c.BccState.Value;
			if (c.BloodType.HasValue)
				s.BloodType = c.BloodType.Value;
			if (c.Skulk.HasValue)
				s.Skulk = c.Skulk.Value;
			if (c.MaxSkulk.HasValue)
				s.MaxSkulk = c.MaxSkulk.Value;
			if (c.TradeskillFlags.HasValue)
				s.TradeskillFlags = c.TradeskillFlags.Value;
			if (c.ClientScryScript.HasValue)
				s.ClientScryScript = c.ClientScryScript.Value;
			if (c.SoundScryScript.HasValue)
				s.SoundScryScript = c.SoundScryScript.Value;
			if (c.CarsGroup.HasValue)
				s.CarsGroup = c.CarsGroup.Value;
			if (c.MovementMultiplier.HasValue)
				s.MovementMultiplier = c.MovementMultiplier.Value;
			if (c.OocMovementMultiplier.HasValue)
				s.OocMovementMultiplier = c.OocMovementMultiplier.Value;
			if (c.RunSpeed.HasValue)
				s.RunSpeed = c.RunSpeed.Value;
			if (c.MaxRunSpeed.HasValue)
				s.MaxRunSpeed = c.MaxRunSpeed.Value;
			if (c.OrientationMode.HasValue)
				s.OrientationMode = c.OrientationMode.Value;
			if (c.GuildId.HasValue)
				s.GuildId = c.GuildId.Value;
			if (c.VoiceSet.HasValue)
				s.VoiceSet = c.VoiceSet.Value;
			if (c.ChatId.HasValue)
				s.ChatId = c.ChatId.Value;
			return s;
		}
	}
}
