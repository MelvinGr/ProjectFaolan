using Faolan.Core.Network;
using Faolan.Core.Data;

namespace Faolan.Core.Network.Packets
{
	// Builds/parses the FormatFeedbackIIR (sub-opcode 0x8D5A8B6B) body - AoC's ON-SCREEN feedback / notification
	// mechanism (NOT chat). The client renders the carried `remoteformat` string as on-screen text; with the
	// `showfifomessagefromserver(...)` token family it drives the top-of-screen FIFO message banners (zone-name
	// reveal `GenericDisplayAreaName`, pickup notices `GenericSimpleDynelPickup`, PvP trackers, ...). Plain tokens
	// (`AttributeChanged`/`Increased`, `LostGraceEffect`, ...) render the smaller feedback lines.
	//
	// Carried as an IIR on the WORLD conversation (opcode 0x2000; sender = WorldFraming.Sender 0xB171CE13/0x58,
	// receiver = the player's GameInterface = GameInterfaceType + ClientInstance). The IIR body's `instance` equals
	// that receiver instance (verified in the Strie capture: all 1249 GameInterfaceType packets AND the feedback share
	// 0x02165EFF), so a live send is fully addressable from the session, not just a replay.
	//
	// Wire grammar (big-endian, decoded 2026-06-14 from the gameplay/Has-inventory captures, golden-tested in
	// FormatFeedbackCodecTests):
	//   [u32 len]          = byte length of everything after this field
	//   [u32 0x8D5A8B6B]   FormatFeedbackIIR sub-opcode
	//   [u32 0x0000C350]   standard IIR marker
	//   [u32 instance]     the target GameInterface object (= the receiver endpoint instance / ClientInstance)
	//   [u8  0x01]         lead flag (constant)
	//   [u64 0]            reserved (constant 0)
	//   [u32 typeFlags]    feedback type/priority (0x00000000 for the area-name FIFO banner; 0x42000020 etc. for
	//                      buff/attribute lines - kept verbatim, semantics loose)
	//   [u16 xmlLen][remoteformat XML]      the message (no null terminator)
	//   [u32 footer]       trailing flag (0 or 1 in the captures)
	public static class FormatFeedbackCodec
	{
		public const uint SubOpcode = 0x8D5A8B6B;
		private const uint IirMarker = EntityClass.Character;
		private const byte LeadFlag = 0x01;

		// typeFlags value of the GenericDisplayAreaName / FIFO-banner messages (the top-of-screen overlay).
		public const uint FifoBannerTypeFlags = 0x00000000;

		public sealed record Parsed(uint Instance, uint TypeFlags, uint Footer, byte[] Xml);

		public static byte[] Build(uint instance, uint typeFlags, uint footer, byte[] xml)
		{
			var content = new FaolanStream();
			content
				.WriteUInt32(SubOpcode)
				.WriteUInt32(IirMarker)
				.WriteUInt32(instance)
				.WriteByte(LeadFlag)
				.WriteUInt64(0ul)
				.WriteUInt32(typeFlags)
				.WriteUInt16((ushort)xml.Length)
				.WriteArray(xml)
				.WriteUInt32(footer);

			var body = content.ToArray();
			var s = new FaolanStream();
			s.WriteUInt32((uint)body.Length);
			s.WriteArray(body);
			return s.ToArray();
		}

		public static Parsed Parse(byte[] body)
		{
			var s = new FaolanStream(body);
			s.ReadUInt32();            // len
			s.ReadUInt32();            // subop (0x8D5A8B6B)
			s.ReadUInt32();            // 0xC350 marker
			var instance = s.ReadUInt32();
			s.ReadArray(1);            // lead flag 0x01
			s.ReadUInt64();            // reserved 0
			var typeFlags = s.ReadUInt32();
			var xmlLen = s.ReadUInt16();
			var xml = s.ReadArray(xmlLen);
			var footer = s.ReadUInt32();
			return new Parsed(instance, typeFlags, footer, xml);
		}
	}
}
