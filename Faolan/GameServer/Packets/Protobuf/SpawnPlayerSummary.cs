using Faolan.Core.Network.Packets;
using ProtoBuf;

namespace Faolan.GameServer.Packets.Protobuf
{
	/// <summary>
	///     The f4 summary block embedded in <c>SpawnPlayerPacketData0_2</c>. Its field numbers line up 1:1 with the
	///     client's <c>ProtoCoreCharacterIIR.CharacterStats</c> (recovered schema, docs/aoc-client-protos.proto), so the
	///     fields are named from that schema - this resolves what used to be "UnknownN".
	///     NOTE: this stays a bespoke wire-tolerant model rather than reusing the generated CharacterStats, because the
	///     generated type declares field 20 (radius) as <c>sint32</c> (zig-zag), whereas this spawn block carries a plain
	///     <c>uint</c> there - swapping to the generated type would zig-zag-encode field 20 and corrupt the packet. All
	///     fields are <c>IsRequired</c> so the exact captured field set is always serialized (proven spawn bytes).
	/// </summary>
	[ProtoContract]
	public sealed class SpawnPlayerSummary : FaolanProtobufMessage
	{
		[ProtoMember(1, IsRequired = true)] public uint Sex { get; set; }
		[ProtoMember(2, IsRequired = true)] public uint Side { get; set; }
		[ProtoMember(3, IsRequired = true)] public uint Race { get; set; }
		[ProtoMember(4, IsRequired = true)] public uint MonsterScale { get; set; }
		[ProtoMember(5, IsRequired = true)] public uint MonsterData { get; set; }

		// life / health (field 6/7), wire-scaled ×100.
		[ProtoMember(6, IsRequired = true)] public uint MaxHealthRaw { get; set; }
		[ProtoMember(7, IsRequired = true)] public uint HealthRaw { get; set; }

		[ProtoMember(8, IsRequired = true)] public uint MaxStaminaRaw { get; set; }
		[ProtoMember(9, IsRequired = true)] public uint StaminaRaw { get; set; }
		[ProtoMember(10, IsRequired = true)] public uint MaxManaRaw { get; set; }
		[ProtoMember(11, IsRequired = true)] public uint ManaRaw { get; set; }

		[ProtoMember(12, IsRequired = true)] public uint LooksPackage { get; set; }
		[ProtoMember(14, IsRequired = true)] public uint MountScale { get; set; }
		[ProtoMember(15, IsRequired = true)] public uint MountFlags { get; set; }
		[ProtoMember(16, IsRequired = true)] public uint Flags { get; set; }
		[ProtoMember(17, IsRequired = true)] public uint HeadMesh { get; set; }
		[ProtoMember(18, IsRequired = true)] public uint VisibleLevel { get; set; } // level
		[ProtoMember(19, IsRequired = true)] public uint ApprenticeLevel { get; set; }
		[ProtoMember(20, IsRequired = true)] public uint RadiusRaw { get; set; } // schema: sint32 radius; plain uint on the wire here
		[ProtoMember(21, IsRequired = true)] public uint Bcc { get; set; }
		[ProtoMember(22, IsRequired = true)] public uint BccState { get; set; }
		[ProtoMember(26, IsRequired = true)] public uint TradeskillFlags { get; set; }
		[ProtoMember(29, IsRequired = true)] public uint CarsGroup { get; set; }
		[ProtoMember(32, IsRequired = true)] public uint RunSpeed { get; set; }
		[ProtoMember(34, IsRequired = true)] public uint OrientationMode { get; set; }

		[ProtoIgnore] public uint MaxHealth => MaxHealthRaw / 100;
		[ProtoIgnore] public uint Health => HealthRaw / 100;
		[ProtoIgnore] public uint Stamina => StaminaRaw / 100;
		[ProtoIgnore] public uint MaxMana => MaxManaRaw / 100;
		[ProtoIgnore] public uint Mana => ManaRaw / 100;
	}
}
