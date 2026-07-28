namespace Faolan.Core.Network.Codecs.World
{
	// The PlayfieldProxy stream (client `FUN_00a6a600`): `[u8 version 'b'][u32 x8][u8 bool]` - a reference to a
	// playfield and, when it is pinned, to one live INSTANCE of it. Eight slots, six of which are known:
	//
	//   [0] class word  - PlayfieldClass (0xC79C) normally; the revive body uses 0xC7A0 (see ResurrectionSpotsBody:
	//                     the 0xC79C/0xC79E/0xC7A0 selection is per-PACKET and still undecoded)
	//   [1] map id
	//   [2] a PER-MAP word - 0 on most maps, 2 on 2040/4022/5007 (129/22 across the 151 captured PlayfieldFIIRs).
	//       Stored as Map.PlayfieldFieldA and sent from there (#451).
	//   [3] a character handle - set on the revive body, 0 elsewhere (0 in 82 of 151)
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
	public sealed record PlayfieldProxy(uint MapId, uint ClassWord = PlayfieldProxy.PlayfieldClass, uint CharacterHandle = 0, uint Slot5 = 0, uint InstanceWord = 0, uint InstanceToken = 0,
		bool Trailing = false, uint Slot2 = 0) : ICodec<PlayfieldProxy>
	{
		// [0] for every proxy except the revive body's.
		public const uint PlayfieldClass = 0x0000C79C;

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
			var slot2 = s.ReadUInt32(); // [2] - per-map, see the slot notes
			var handle = s.ReadUInt32(); // [3]
			s.ReadUInt32(); // [4] - 0 in every capture
			var slot5 = s.ReadUInt32();
			var instanceWord = s.ReadUInt32();
			var token = s.ReadUInt32();
			return new PlayfieldProxy(mapId, classWord, handle, slot5, instanceWord, token, s.ReadByte() != 0, slot2);
		}

		public void Write(FaolanStream s)
		{
			s.WriteByte(Version)
				.WriteUInt32(ClassWord) // [0]
				.WriteUInt32(MapId) // [1]
				.WriteUInt32(Slot2) // [2]
				.WriteUInt32(CharacterHandle) // [3]
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
