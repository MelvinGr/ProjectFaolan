namespace Faolan.Core.Network.Codecs.World
{
	// The PlayfieldProxy stream (client `FUN_00a6a600`): `[u8 version 'b'][u32 x8][u8 bool]` - a reference to a
	// playfield and, when it is pinned, to one live INSTANCE of it. Eight slots, six of which are known:
	//
	//   [0] class word  - PlayfieldClass (0xC79C) normally. NOT a constant: it also carries the instance's DIFFICULTY
	//                     MODE - see the note below. The revive body uses 0xC7A0 (see ResurrectionSpotsBody: the
	//                     0xC79C/0xC79E/0xC7A0 selection is per-PACKET and still undecoded)
	//   [1] map id
	//   [2] the instance OWNER's ID32 TYPE, and
	//   [3] the instance OWNER's ID32 INSTANCE - one pair, not two unrelated words (#512, see below)
	//   [4] always 0 in every capture
	//   [5] a small COUNT, not a flag - 1, 2, 4, 5, 6 and 8 all occur (117/16/5/5/3/2). The teleport records only
	//       ever carry 0 or 1, which is why this used to be modelled as a "ready flag"; the PlayfieldFIIR embedding
	//       disproves that. What it counts is undecoded. [V0]
	//   [6] InstanceWord (0x9C50 = 40016) - a constant that rides with a pinned instance; NOT an entity class id
	//       (it is below the 50000 base, see EntityClass)
	//   [7] the playfield INSTANCE token
	//
	// This was private to TeleportCodec until TeamMemberRecord turned out to embed one (Gitea #454): that record
	// carried the proxy as `Field7`..`Field15`, misaligned by a byte because `Field7 = 0x00000162` had swallowed the
	// version byte ('b' = 0x62). Read aligned, both captured members decode into all ten fields exactly, which is
	// also what identifies 0x01EDA8B2 - shared by two different players in the same map - as an instance TOKEN
	// rather than the constant or timestamp it had been read as. [V0 on the token semantics: structural evidence
	// across two codecs, not yet checked in-client.]
	//
	// ⚠️ The slot notes above are what THREE embeddings agree on (teleport, team member, PlayfieldFIIR). Two of them
	// only became visible when the third was composed here - "[2] is always 0" and "[5] is a ready flag" were both
	// true of the first two embeddings and false of the wire. Widen the evidence before narrowing a slot's name.
	//
	// <b>[2]+[3] are the instance's OWNER as an ID32 (#512).</b> They used to be read as "a per-map word" and "a
	// character handle, 0 elsewhere", which is what two of the three embeddings happen to look like. Across the corpus
	// the pair only ever takes three shapes, and each is an owner:
	//
	//   (0, 0)                     no owner - an ordinary SHARED playfield. The common case.
	//   (2, <id>)                  a PERSONAL instance. Type 2 appears on exactly the maps that are always private -
	//                              King Conan's castle (2040), Tortage Night (4022), the Conarch Armory (5007) - which
	//                              is why it looked per-map: those maps are never anything else. The revive body's
	//                              "character handle" is this same slot, i.e. the owner there is the player.
	//   (0xDEA9, <teamId>)         a TEAM-owned instance - 0xDEA9 (57001) is the team/guild class. Observed once, and
	//                              decisively: entering the Amphitheatre of Karutonia (1092) as a group carries team
	//                              0x000F08DB here, the exact id DispatchYouJoinedTeam had just announced and that the
	//                              `~Team` channel subscribed to seconds earlier.
	//
	// So "whose copy of this playfield is this" is answered by these two words, and Map.PlayfieldFieldA storing a 2
	// per map stays correct for those three maps precisely because they are always personally owned.
	//
	// <b>[0] carries the DIFFICULTY MODE (#511).</b> Across all 183 captured PlayfieldFIIRs the class word is 0xC79C
	// 182 times and 0xCABC exactly once - and that once is the annotated `enered epic instance` entry, to the SAME map
	// (1004, the Spider Caverns) the `energed normal instance` entry 34 s earlier had reached with 0xC79C. Same map id,
	// same everything else; the mode is this word. That is one observation, but it is the cleanest kind: a controlled
	// A/B on one map, minutes apart, with the player narrating which was which.
	public sealed record PlayfieldProxy(uint MapId, uint ClassWord = PlayfieldProxy.PlayfieldClass, uint OwnerId = 0, uint Slot5 = 0, uint InstanceWord = 0, uint InstanceToken = 0,
		bool Trailing = false, uint OwnerType = 0) : ICodec<PlayfieldProxy>
	{
		// [0] for every proxy except the revive body's - and, per the note above, the NORMAL difficulty mode.
		public const uint PlayfieldClass = 0x0000C79C;

		// [0] on an EPIC instance (#511). Observed once, in a controlled A/B against the same map minutes earlier.
		public const uint EpicPlayfieldClass = 0x0000CABC;

		// [2] when the instance belongs to a TEAM - the team/guild class word, the same 0xDEA9 a team's own ID32 uses.
		public const uint TeamOwnerType = 0x0000DEA9;

		// [2] when the instance is PERSONAL (King Conan's castle, Tortage Night, the Conarch Armory).
		public const uint PersonalOwnerType = 0x00000002;

		// [6] on a proxy whose instance is pinned.
		public const uint PinnedInstanceWord = 0x00009C50;

		// [5] on a teleport record whose instance is pinned. Kept as a named constant because that IS the value the
		// teleport bodies carry - but see the slot note above: 1 is the common case of a count, not a boolean true.
		public const uint Ready = 0x00000001;

		public const byte Version = 0x62; // 'b'

		public static PlayfieldProxy Read(FaolanStream s)
		{
			s.ReadByte(); // version 'b'
			var classWord = s.ReadUInt32();
			var mapId = s.ReadUInt32();
			var ownerType = s.ReadUInt32(); // [2] - the owner's ID32 type, see the slot notes
			var ownerId = s.ReadUInt32(); // [3] - the owner's ID32 instance
			s.ReadUInt32(); // [4] - 0 in every capture
			var slot5 = s.ReadUInt32();
			var instanceWord = s.ReadUInt32();
			var token = s.ReadUInt32();
			return new PlayfieldProxy(mapId, classWord, ownerId, slot5, instanceWord, token, s.ReadByte() != 0, ownerType);
		}

		/// <summary>Whether this playfield is a private copy at all - i.e. whether anyone owns it.</summary>
		public bool IsInstanced => OwnerType != 0;

		/// <summary>Whether the owning entity is a TEAM (a group instance) rather than a single character.</summary>
		public bool IsTeamOwned => OwnerType == TeamOwnerType;

		/// <summary>Whether this is an EPIC-difficulty copy of the playfield rather than a normal one.</summary>
		public bool IsEpic => ClassWord == EpicPlayfieldClass;

		public void Write(FaolanStream s)
		{
			s.WriteByte(Version)
				.WriteUInt32(ClassWord) // [0]
				.WriteUInt32(MapId) // [1]
				.WriteUInt32(OwnerType) // [2]
				.WriteUInt32(OwnerId) // [3]
				.WriteUInt32(0) // [4]
				.WriteUInt32(Slot5) // [5]
				.WriteUInt32(InstanceWord) // [6]
				.WriteUInt32(InstanceToken) // [7]
				.WriteByte(Trailing ? (byte)0x01 : (byte)0x00);
		}

		// A proxy naming a map but no particular instance of it - the "begin teleport" form.
		public static PlayfieldProxy ForMap(uint mapId)
		{
			return new PlayfieldProxy(mapId);
		}

		// A proxy pinned to one live playfield instance - the form a destination announce and a team-member record
		// both carry.
		public static PlayfieldProxy ForInstance(uint mapId, uint instanceToken)
		{
			return new PlayfieldProxy(mapId, InstanceWord: PinnedInstanceWord, Slot5: Ready, InstanceToken: instanceToken);
		}
	}
}
