using Faolan.Core.Network.Packets;
using ProtoBuf;

namespace Faolan.GameServer.Packets.Protobuf
{
	[ProtoContract]
	public sealed class InteractArgs : FaolanProtobufMessage
	{
		[ProtoMember(10)]
		public CastSpellTarget? SpellTarget { get; set; }

		[ProtoMember(5)]
		public ulong Category { get; set; }

		[ProtoMember(3)]
		public ulong Field3 { get; set; }

		[ProtoMember(6)]
		public ulong Field6 { get; set; }

		[ProtoMember(2)]
		public ulong Mode { get; set; }

		[ProtoMember(1)]
		public ulong TypeId { get; set; }
	}

	[ProtoContract]
	public sealed class CastSpellTarget
	{
		[ProtoMember(1)]
		public ulong SpellId { get; set; }

		[ProtoMember(2)]
		public ulong TargetHandle { get; set; }
	}
}
