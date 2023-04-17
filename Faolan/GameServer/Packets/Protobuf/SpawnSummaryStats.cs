using Faolan.Core.Network.Packets;
using ProtoBuf;

namespace Faolan.GameServer.Packets.Protobuf
{
	/// <summary>
	///     Protobuf-like f6 block embedded in <c>SpawnPlayerPacketData0_2</c>.
	///     Confirmed mappings from packet bytes and capture correlation:
	///     - field 2 = overall kills (145)
	///     - field 3 = overall deaths (204)
	///     The remaining fields are still stable but not yet semantically decoded.
	/// </summary>
	[ProtoContract]
	public sealed class SpawnSummaryStats : FaolanProtobufMessage
	{
		[ProtoMember(3, IsRequired = true)]
		public uint OverallDeaths { get; set; }

		[ProtoMember(2, IsRequired = true)]
		public uint OverallKills { get; set; }

		[ProtoMember(1, IsRequired = true)]
		public uint Unknown1 { get; set; }

		[ProtoMember(11, IsRequired = true)]
		public uint Unknown11 { get; set; }

		[ProtoMember(13, IsRequired = true)]
		public uint Unknown13 { get; set; }

		[ProtoMember(16, IsRequired = true)]
		public uint Unknown16 { get; set; }

		[ProtoMember(8, IsRequired = true)]
		public uint Unknown8 { get; set; }
	}
}
