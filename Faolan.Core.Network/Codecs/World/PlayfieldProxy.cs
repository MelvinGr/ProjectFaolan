using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.World
{
	// The PlayfieldProxy stream (client `FUN_00a6a600`): `[u8 version 'b'][u32 x8][u8 bool]` - a reference to a
	// playfield and, when it is pinned, to one live instance of it. Eight slots, six of which are known:
	//
	//   [0] class word  - PlayfieldClass (0xC79C) normally; it also carries the instance's difficulty mode, see below.
	//                     The revive body uses 0xC7A0 (see ResurrectionSpotsBody: the 0xC79C/0xC79E/0xC7A0 selection is
	//                     per-packet and undecoded)
	//   [1] map id
	//   [2] the instance owner's ID32 type, and
	//   [3] the instance owner's ID32 instance - one pair, not two unrelated words (see below)
	//   [4] always 0 in every capture
	//   [5] a small count, not a flag - 1, 2, 4, 5, 6 and 8 all occur (117/16/5/5/3/2); the teleport records only ever
	//       carry 0 or 1. What it counts is undecoded [V0]; the best-fitting reading is the map copy's ordinal - the
	//       back-to-back Spider Caverns entries carry 1 with token 0x50B36B then 2 with the consecutive token
	//       0x50B36C, i.e. the player's second minted copy of that map. It is not the difficulty (that is [0] alone):
	//       the 4/5/6/8 values occur on maps with no epic mode. We emit 1.
	//   [6] InstanceWord (0x9C50 = 40016) - a constant that rides with a pinned instance; not an entity class id (it is
	//       below the 50000 base, see EntityClass)
	//   [7] the playfield instance token
	//
	// The slot notes are what three independent embeddings agree on (the teleport records, TeamMemberRecord and
	// PlayfieldFIIR); widen the evidence across all three before narrowing a slot's name. Read aligned, both captured
	// team members decode into all ten fields exactly, which is what identifies 0x01EDA8B2 - shared by two different
	// players in the same map - as an instance token. [V0 on the token semantics: structural evidence across two
	// codecs, not checked in-client.]
	//
	// [2]+[3] are the instance's owner as an ID32. Across the corpus the pair takes four shapes, and each is an owner:
	//
	//   (0, 0)                     no owner - an ordinary shared playfield. The common case.
	//   (2, <id>)                  a personal instance. Type 2 appears on exactly the maps that are always private -
	//                              King Conan's castle (2040), Tortage Night (4022), the Conarch Armory (5007). The
	//                              revive body's "character handle" is this same slot, i.e. the owner there is the
	//                              player.
	//   (0xDEA9, <teamId>)         a team-owned instance - 0xDEA9 (57001) is the team/guild class. Entering the
	//                              Amphitheatre of Karutonia (1092) as a group carries team 0x000F08DB here, the exact
	//                              id DispatchYouJoinedTeam had just announced and that the `~Team` channel subscribed
	//                              to seconds earlier.
	//   (0, <characterId>)         a personal instance that names its owner without the type-2 tag. 47 of the 218
	//                              bodies across all dumps carry ownerType 0 with a non-zero owner; every one is map
	//                              4010 (the Tortage beach island) in a create/login capture, and the id is the
	//                              player's own character id (35022073 in `Barbarian-create`). So map 4010 is
	//                              instanced even though it carries no owner type: do not read "type 2 means personal"
	//                              as "anything else is shared".
	//
	// So "whose copy of this playfield is this" is answered by these two words, and Map.PlayfieldFieldA storing a 2 per
	// map is correct for those three maps precisely because they are always personally owned.
	//
	// [0] carries the difficulty mode. Across all 183 captured PlayfieldFIIRs the class word is 0xC79C 182 times and
	// 0xCABC exactly once - the annotated `enered epic instance` entry, to the same map (1004, the Spider Caverns) the
	// `energed normal instance` entry 34 s earlier had reached with 0xC79C.
	public sealed record PlayfieldProxy(uint MapId, uint ClassWord = PlayfieldProxy.PlayfieldClass, uint OwnerId = 0, uint Slot5 = 0, uint InstanceWord = 0, uint InstanceToken = 0,
		bool Trailing = false, uint OwnerType = 0)
	{
		/// <summary>[0] for every proxy except the revive body's, and the normal difficulty mode.</summary>
		public const uint PlayfieldClass = 0x0000C79C;

		/// <summary>[0] on an epic instance.</summary>
		/// <remarks>
		///     The difficulty is picked at the scry message box and carried here. The corpus shows the whole chain twice,
		///     34 seconds apart on the same door and the same map: LookAt(the Spider Caverns door 0x8C5) -> UseDynel ->
		///     DispatchScryShowMessageBox "Do you want to travel to the Spider Caverns?" -> ScryMessageBoxAnswer[0] ->
		///     IntendedTeleportIIR + PlayfieldFIIR carrying this word. Only the answer's option index differs: `1` yields
		///     0xC79C and `2` yields 0xCABC. See ScryMessageBoxAnswerCodec for the picker's own half.
		/// </remarks>
		public const uint EpicPlayfieldClass = 0x0000CABC;

		/// <summary>[2] when the instance belongs to a team - the same 0xDEA9 class word a team's own ID32 uses.</summary>
		public const uint TeamOwnerType = 0x0000DEA9;

		/// <summary>[2] when the instance is personal (King Conan's castle, Tortage Night, the Conarch Armory).</summary>
		public const uint PersonalOwnerType = 0x00000002;

		/// <summary>[6] on a proxy whose instance is pinned.</summary>
		public const uint PinnedInstanceWord = 0x00009C50;

		/// <summary>[5] on a teleport record whose instance is pinned.</summary>
		/// <remarks>
		///     Named because that is the value the teleport bodies carry, but per the slot notes above 1 is the common
		///     case of a count, not a boolean true.
		/// </remarks>
		public const uint Ready = 0x00000001;

		public const byte Version = 0x62; // 'b'

		// A sub-record's composition pair: this value is only ever read/written inside a larger body (PlayfieldFIIR,
		// the teleport records, a team-member record), so both halves are `internal`.
		internal static PlayfieldProxy Read(ref SpanReader r)
		{
			r.ReadU8(); // version 'b'
			var classWord = r.ReadU32Be();
			var mapId = r.ReadU32Be();
			var ownerType = r.ReadU32Be(); // [2] - the owner's ID32 type, see the slot notes
			var ownerId = r.ReadU32Be(); // [3] - the owner's ID32 instance
			r.ReadU32Be(); // [4] - 0 in every capture
			var slot5 = r.ReadU32Be();
			var instanceWord = r.ReadU32Be();
			var token = r.ReadU32Be();

			return new PlayfieldProxy(mapId, classWord, ownerId, slot5, instanceWord, token, r.ReadU8() != 0, ownerType);
		}

		/// <summary>Whether this playfield is a private copy at all - i.e. whether anyone owns it.</summary>
		public bool IsInstanced => OwnerType != 0;

		/// <summary>Whether the owning entity is a team (a group instance) rather than a single character.</summary>
		public bool IsTeamOwned => OwnerType == TeamOwnerType;

		/// <summary>Whether this is an epic-difficulty copy of the playfield rather than a normal one.</summary>
		public bool IsEpic => ClassWord == EpicPlayfieldClass;

		internal void Write(FaolanStream s)
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

		/// <summary>A proxy naming a map but no particular instance of it - the "begin teleport" form.</summary>
		/// <param name="epic">
		///     Selects the difficulty class word; the captured epic begin differs from the normal one in that word alone.
		/// </param>
		public static PlayfieldProxy ForMap(uint mapId, bool epic = false)
		{
			return new PlayfieldProxy(mapId, epic ? EpicPlayfieldClass : PlayfieldClass);
		}

		/// <summary>
		///     A proxy pinned to one live playfield instance - the form a destination announce and a team-member record
		///     both carry.
		/// </summary>
		/// <param name="epic">Selects the difficulty class word.</param>
		public static PlayfieldProxy ForInstance(uint mapId, uint instanceToken, bool epic = false)
		{
			return new PlayfieldProxy(mapId, epic ? EpicPlayfieldClass : PlayfieldClass, InstanceWord: PinnedInstanceWord, Slot5: Ready, InstanceToken: instanceToken);
		}
	}
}
