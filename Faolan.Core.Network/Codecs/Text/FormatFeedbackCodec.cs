using System.Text;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Text
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
		// typeFlags of the buff/effect feedback LINES (not the FIFO banner) - e.g. LostGraceEffect, AttributeChanged.
		public const uint EffectLineTypeFlags = 0x42000020;

		// typeFlags value of the GenericDisplayAreaName / FIFO-banner messages (the top-of-screen overlay).
		public const uint FifoBannerTypeFlags = 0x00000000;
		public const uint Opcode = (uint)GameServer0X2000Opcodes.FormatFeedbackIIR; // 0x8D5A8B6B
		private const byte LeadFlag = 0x01;

		public static byte[] Build(uint instance, uint typeFlags, uint footer, byte[] xml)
		{
			return new Message(instance, typeFlags, footer, xml).ToArray();
		}

		// The GenericDisplayAreaName FIFO-banner XML (the top-of-screen zone/area-name reveal) for a given client
		// localized-text id. Byte-exact against the captured banners (all carry this same token/category/key; only the
		// text id varies per area - the ids are per-SUB-REGION, sourced from the client's region volumes, #348). Send
		// the result via Build(instance, FifoBannerTypeFlags, 1, xml).
		public static byte[] BuildAreaNameXml(uint textId)
		{
			return Encoding.Latin1.GetBytes("<remoteformat token=\"showfifomessagefromserver(8)|main(1)|GenericDisplayAreaName\" category=\"50018\" " +
			                                "key=\"pcAdIWYhSXfTFDe5I1g7\" knubot=\"0\"  ><parameter value=\"7;&lt;localized category=&#39;50025&#39; " +
			                                $"token=&#39;DisplayedText(3)|GenericDisplayAreaName|{textId}&#39;&gt;\" name=\"DisplayedText\" /></remoteformat>");
		}

		// The LostGraceEffect feedback XML - the "you have lost the grace" on-screen line retail shows the instant a
		// player LOSES the Shield of the Risen protection (an aggressive action), sent right before the buff-removal
		// twin (NecroL80_..._conans-castle-exit rec#508: LostGraceEffect feedback -> 0x37f38c01 shield remove, while the
		// aggro-status stream shows the player in combat). NOT sent on a playfield swap of the shield (rec#206/#1750
		// swap the 1h<->10h with no LostGraceEffect). Byte-exact against that record; send via
		// Build(instance, EffectLineTypeFlags, 1, xml). The key is the capture-verbatim per-token key (same treatment
		// as BuildAreaNameXml's fixed key).
		public static byte[] BuildLostGraceEffectXml()
		{
			return Encoding.Latin1.GetBytes("<remoteformat token=\"LostGraceEffect\" category=\"100\" key=\"RO5SQlZn.h/DvvaH&#36;WGu\" knubot=\"0\"  ></remoteformat>");
		}

		// The "resurrection point discovered" FIFO banner (#372) - retail's toast when a player walks near a point
		// they had not found yet. Sent alongside the DispatchValidResurrectionSpots re-send that adds the point to the
		// player's list (ResurrectionSpotsBody); the banner is what the player actually SEES. Send via
		// Build(instance, FifoBannerTypeFlags, 1, xml) - same framing as the area-name banner.
		//
		// This message identifies its string by `id=` rather than the `token=` the other feedback XML uses, which is
		// why a scan for `remoteformat token="..."` finds no discovery message and wrongly suggests none exists (#372
		// was stuck on exactly that). The parameter is a NESTED `<localized>` naming the point by its own id under
		// category 270 - the LDB category holding the point names ("Water's Edge", "Secluded Cove", ...). So no name
		// ever crosses the wire; the client resolves it.
		//
		// `nameKey` is the per-POINT key of that category-270 string: constant per point, different between points
		// (784237 and 784238 have different ones), unlike the outer key which is fixed per message id. Pass the
		// capture-verbatim ESCAPED form. Null omits the attribute - the area-name banner's nested `<localized>` has no
		// key at all and renders, so it is not obviously required for every string.
		public static byte[] BuildRespawnPointDiscoveredXml(uint pointId, string? nameKey)
		{
			var key = nameKey == null
				? ""
				: $"key=&quot;{nameKey}&quot; ";
			return Encoding.Latin1.GetBytes("<remoteformat id=\"218327236\" category=\"100\" key=\"KsATGOK`H%9l@@z[1mo,\" knubot=\"0\"  >" +
			                                $"<parameter value=\"7;&lt;localized id=&quot;{pointId}&quot; category=&quot;270&quot; " + $"flags=&quot;&quot; {key}&gt;\" /></remoteformat>");
		}

		public static Message Parse(byte[] body)
		{
			return Message.Read(new FaolanStream(body));
		}

		// The decoded FormatFeedbackIIR body as a self-serializing codec (ICodec): Write frames the whole
		// [len][subop][marker][instance][lead][reserved][typeFlags][xmlLen][xml][footer]; Read recovers the fields.
		public sealed record Message(uint Instance, uint TypeFlags, uint Footer, byte[] Xml) : ICodec<Message>
		{
			public static Message Read(FaolanStream s)
			{
				s.ReadUInt32(); // len
				s.ReadUInt32(); // subop (0x8D5A8B6B)
				s.ReadUInt32(); // 0xC350 marker
				var instance = s.ReadUInt32();
				s.ReadArray(1); // lead flag 0x01
				s.ReadUInt64(); // reserved 0
				var typeFlags = s.ReadUInt32();
				var xmlLen = s.ReadUInt16();
				var xml = s.ReadArray(xmlLen);
				var footer = s.ReadUInt32();
				return new Message(instance, typeFlags, footer, xml);
			}

			public void Write(FaolanStream s)
			{
				s.WriteArrayUInt32Length(content =>
					content
						.WriteUInt32(Opcode)
						.WriteUInt32(EntityClass.Character)
						.WriteUInt32(Instance)
						.WriteByte(LeadFlag)
						.WriteUInt64(0ul)
						.WriteUInt32(TypeFlags)
						.WriteUInt16(Xml.Length)
						.WriteArray(Xml)
						.WriteUInt32(Footer));
			}
		}
	}
}
