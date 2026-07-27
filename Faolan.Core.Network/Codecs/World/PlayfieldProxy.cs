namespace Faolan.Core.Network.Codecs.World
{
	// The PlayfieldProxy stream (client `FUN_00a6a600`): `[u8 version 'b'][u32 x8][u8 bool]` - a reference to a
	// playfield and, when it is pinned, to one live INSTANCE of it. Eight slots, six of which are known:
	//
	//   [0] class word  - PlayfieldClass (0xC79C) normally; the revive body uses 0xC7A0 (see ResurrectionSpotsBody:
	//                     the 0xC79C/0xC79E/0xC7A0 selection is per-PACKET and still undecoded)
	//   [1] map id
	//   [2] always 0 in every capture
	//   [3] a character handle - set on the revive body, 0 elsewhere
	//   [4] always 0 in every capture
	//   [5] ready flag - 1 once the instance is pinned, 0 while it is not
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
	public sealed record PlayfieldProxy(uint MapId, uint ClassWord = PlayfieldProxy.PlayfieldClass, uint CharacterHandle = 0, uint ReadyFlag = 0, uint InstanceWord = 0, uint InstanceToken = 0,
		bool Trailing = false) : ICodec<PlayfieldProxy>
	{
		// [0] for every proxy except the revive body's.
		public const uint PlayfieldClass = 0x0000C79C;

		// [6] on a proxy whose instance is pinned.
		public const uint PinnedInstanceWord = 0x00009C50;

		// [5] once the instance is valid/ready.
		public const uint Ready = 0x00000001;

		public const byte Version = 0x62; // 'b'

		public static PlayfieldProxy Read(FaolanStream s)
		{
			s.ReadByte(); // version 'b'
			var classWord = s.ReadUInt32();
			var mapId = s.ReadUInt32();
			s.ReadUInt32(); // [2] - 0 in every capture
			var handle = s.ReadUInt32(); // [3]
			s.ReadUInt32(); // [4] - 0 in every capture
			var ready = s.ReadUInt32();
			var instanceWord = s.ReadUInt32();
			var token = s.ReadUInt32();
			return new PlayfieldProxy(mapId, classWord, handle, ready, instanceWord, token, s.ReadByte() != 0);
		}

		public void Write(FaolanStream s)
		{
			s.WriteByte(Version)
				.WriteUInt32(ClassWord) // [0]
				.WriteUInt32(MapId) // [1]
				.WriteUInt32(0) // [2]
				.WriteUInt32(CharacterHandle) // [3]
				.WriteUInt32(0) // [4]
				.WriteUInt32(ReadyFlag) // [5]
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
			return new PlayfieldProxy(mapId, InstanceWord: PinnedInstanceWord, ReadyFlag: Ready, InstanceToken: instanceToken);
		}
	}
}
