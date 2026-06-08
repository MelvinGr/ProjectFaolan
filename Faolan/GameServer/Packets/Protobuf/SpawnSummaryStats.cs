using Faolan.Core.Network.Packets;
using ProtoBuf;

namespace Faolan.GameServer.Packets.Protobuf
{
	/// <summary>
	///     The f6 summary-stats block in <c>SpawnPlayerPacketData0_2</c>. Field numbers map 1:1 onto the client's
	///     <c>ProtoCoreCharacterIIR.PlayerStats</c> (recovered schema, docs/aoc-client-protos.proto), so the fields are
	///     named from it (resolving the former "UnknownN"). Kept as a bespoke <c>IsRequired</c> model so the exact
	///     captured field set is serialized (proven spawn bytes).
	/// </summary>
	[ProtoContract]
	public sealed class SpawnSummaryStats : FaolanProtobufMessage
	{
		[ProtoMember(1, IsRequired = true)] public uint PvpLevel { get; set; }
		[ProtoMember(2, IsRequired = true)] public uint OverallKills { get; set; }   // pvp_total_kills
		[ProtoMember(3, IsRequired = true)] public uint OverallDeaths { get; set; }  // pvp_total_deaths
		[ProtoMember(8, IsRequired = true)] public uint PlayerFlags { get; set; }
		[ProtoMember(11, IsRequired = true)] public uint Strength { get; set; }
		[ProtoMember(13, IsRequired = true)] public uint SubscriptionFlags { get; set; }
		[ProtoMember(16, IsRequired = true)] public uint HardcoreLevel { get; set; }
	}
}
