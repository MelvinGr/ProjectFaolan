using Faolan.Core.Network.Packets;
using ProtoBuf;

namespace Faolan.GameServer.Packets.Protobuf
{
	/// <summary>
	///     Protobuf-like f4 block embedded in <c>SpawnPlayerPacketData0_2</c>.
	///     This packet section is a mixed legacy summary rather than a clean modern DTO.
	///     The names below reflect currently confirmed or best-guess mappings:
	///     - field 7  = health raw value 594900 -> 5949.00
	///     - field 11 = mana raw value 583000 -> 5830.00
	///     - field 18 = visible level 80
	///     Fields 6/9/10 correlate with nearby stat ids in the capture blocks, but their
	///     visible UI meaning is not fully settled yet, so the raw values are preserved.
	/// </summary>
	[ProtoContract]
	public sealed class SpawnPlayerSummary : FaolanProtobufMessage
	{
		[ProtoIgnore]
		public uint Health => HealthRaw / 100;

		[ProtoMember(7, IsRequired = true)]
		public uint HealthRaw { get; set; }

		[ProtoIgnore]
		public uint Mana => ManaRaw / 100;

		[ProtoMember(11, IsRequired = true)]
		public uint ManaRaw { get; set; }

		[ProtoIgnore]
		public uint MaxHealth => MaxHealthRaw / 100;

		[ProtoMember(6, IsRequired = true)]
		public uint MaxHealthRaw { get; set; }

		[ProtoIgnore]
		public uint MaxMana => MaxManaRaw / 100;

		[ProtoMember(10, IsRequired = true)]
		public uint MaxManaRaw { get; set; }

		[ProtoIgnore]
		public uint Stamina => StaminaRaw / 100;

		[ProtoMember(9, IsRequired = true)]
		public uint StaminaRaw { get; set; }

		[ProtoMember(1, IsRequired = true)]
		public uint Unknown1 { get; set; }

		[ProtoMember(12, IsRequired = true)]
		public uint Unknown12 { get; set; }

		[ProtoMember(14, IsRequired = true)]
		public uint Unknown14 { get; set; }

		[ProtoMember(15, IsRequired = true)]
		public uint Unknown15 { get; set; }

		[ProtoMember(16, IsRequired = true)]
		public uint Unknown16 { get; set; }

		[ProtoMember(17, IsRequired = true)]
		public uint Unknown17 { get; set; }

		[ProtoMember(2, IsRequired = true)]
		public uint Unknown2 { get; set; }

		[ProtoMember(20, IsRequired = true)]
		public uint Unknown20 { get; set; }

		[ProtoMember(21, IsRequired = true)]
		public uint Unknown21 { get; set; }

		[ProtoMember(22, IsRequired = true)]
		public uint Unknown22 { get; set; }

		[ProtoMember(26, IsRequired = true)]
		public uint Unknown26 { get; set; }

		[ProtoMember(29, IsRequired = true)]
		public uint Unknown29 { get; set; }

		[ProtoMember(3, IsRequired = true)]
		public uint Unknown3 { get; set; }

		[ProtoMember(32, IsRequired = true)]
		public uint Unknown32 { get; set; }

		[ProtoMember(34, IsRequired = true)]
		public uint Unknown34 { get; set; }

		[ProtoMember(4, IsRequired = true)]
		public uint Unknown4 { get; set; }

		[ProtoMember(5, IsRequired = true)]
		public uint Unknown5 { get; set; }

		[ProtoMember(8, IsRequired = true)]
		public uint Unknown8 { get; set; }

		[ProtoMember(18, IsRequired = true)]
		public uint VisibleLevel { get; set; }

		[ProtoMember(19, IsRequired = true)]
		public uint VisibleLevelDuplicate { get; set; }
	}
}
