using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs
{
	// Builds/parses the ChatTextIIR (sub-opcode 0xE0B51102) body - a WORLD/SYSTEM chat line the server pushes into
	// the client's chat window (distinct from FormatFeedbackIIR's on-screen FIFO overlay and from the AgentServer
	// channel chat). The text is a `remoteformat` (localized tokens + parameters), the same XML shape the feedback
	// and Knubot packets use, so the server can emit a localized system line without literal strings.
	//
	// Carried as an IIR on the WORLD conversation (opcode 0x2000; sender = WorldFraming.Sender, receiver = the
	// player's GameInterface = GameInterfaceType + ClientInstance). The IIR body's `instance` is that receiver
	// instance, so a live send is fully addressable from the session.
	//
	// Wire grammar (big-endian, decoded 2026-06-16 from demol80 randomActions #7564, golden-tested in
	// ChatTextCodecTests):
	//   [u32 len]          = byte length of everything after this field
	//   [u32 0xE0B51102]   ChatTextIIR sub-opcode
	//   [u32 0x0000C350]   standard IIR marker (Character class)
	//   [u32 instance]     the target GameInterface object (= the receiver endpoint instance / ClientInstance)
	//   [u8  0x00]         lead flag (constant in the sole capture)
	//   [u16 xmlLen][remoteformat XML]      the message (no null terminator)
	//   [u8 0x00][u32 0x40000001]           trailer (constant; semantics loose, like FormatFeedback's footer)
	public static class ChatTextCodec
	{
		public const uint SubOpcode = 0xE0B51102;
		private const byte LeadFlag = 0x00;

		// The 5-byte trailer after the XML (constant in the capture: 0x00 then u32 0x40000001). Carried verbatim
		// until a second sample lets the two halves be field-named.
		private const byte TrailerByte = 0x00;
		private const uint IirMarker = EntityClass.Character;
		private const uint TrailerWord = 0x40000001;

		public static byte[] Build(uint instance, byte[] xml) => new Message(instance, xml).ToArray();

		public static Message Parse(byte[] body) => Message.Read(new FaolanStream(body));

		// The decoded ChatTextIIR body as a self-serializing codec (ICodec): Write frames the whole
		// [len][subop][marker][instance][lead][xmlLen][xml][trailer]; Read recovers the (instance, xml).
		public sealed record Message(uint Instance, byte[] Xml) : ICodec<Message>
		{

			public void Write(FaolanStream s)
			{
				var content = new FaolanStream();
				content.WriteUInt32(SubOpcode).WriteUInt32(IirMarker).WriteUInt32(Instance).WriteByte(LeadFlag).WriteUInt16(Xml.Length).WriteArray(Xml).WriteByte(TrailerByte).WriteUInt32(TrailerWord);

				var body = content.ToArray();
				s.WriteUInt32(body.Length);
				s.WriteArray(body);
			}

			public static Message Read(FaolanStream s)
			{
				s.ReadUInt32(); // len
				s.ReadUInt32(); // subop (0xE0B51102)
				s.ReadUInt32(); // 0xC350 marker
				var instance = s.ReadUInt32();
				s.ReadArray(1); // lead flag 0x00
				var xmlLen = s.ReadUInt16();
				var xml = s.ReadArray(xmlLen);
				return new Message(instance, xml);
			}
		}
	}
}
