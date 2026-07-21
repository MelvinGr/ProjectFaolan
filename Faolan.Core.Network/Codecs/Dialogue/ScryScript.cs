using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Dialogue
{
	// ScryScriptIIR (0xA36D3B74) — the Tortage destiny/tutorial STEP driver (the "QuestRewards" of the
	// opcode catalog is a misnomer; #91 / #149). Server→client it pushes a named scripted step the client's
	// intro flow runs (e.g. "PlayOrSkip", "IntroDone", "ChangeSex", "ApproachCasilda", or the big
	// "NEW_character_creation Slave Galley" creation step). Decoded byte-exact from captures (Captures/
	// scripts/decode_quest.py + ScryScriptIIR ReadSubClass), built here from scratch.
	//
	// Wire (subopcode onward; the 4-byte packet length is added by the send layer):
	//   [subop 0xA36D3B74][ack 0x0000C350][u32 playerHandle][u8 flag=0][u32 BE pbLen][protobuf pbLen bytes]
	// The protobuf is a ProtoScryScriptIIR: version=1, type=2, int_parameter=3, string_parameter_one=4 (the step
	// token), string_parameter_two=5 (resources), id_parameter=6 (a ProtoID32 pair).
	//
	// ⚠️ int_parameter (field 3) is a **sint32 - ZIGZAG encoded**. The callers here pass the RAW WIRE VARINT (the
	// values were lifted straight out of captures, e.g. 48126 / 385910), not the decoded integer, so the raw value
	// is zigzag-DECODED on the way in and protobuf-net re-encodes it to the identical bytes. Evidence it really is
	// zigzag: across the corpus field 3 is even in 1151 occurrences and odd in only 14 - exactly the signature of
	// zigzag (positives even, negatives odd), not of arbitrary ids. So a "scriptGroupId" of 48126 is really
	// int_parameter 24063. Renaming the call sites to the decoded values is a follow-up; what matters here is that
	// the bytes on the wire stay identical to retail's.
	public static class ScryScript
	{
		// The IIR type this codec emits (#317 convention; read by the coverage matrix).
		public const uint Opcode = (uint)GameServer0X2000Opcodes.ScryScriptIIR;

		// The no-token form: a StartScriptGroupOnClient (type 0) callback that runs a script group by id, with no
		// step-token/resources (f4/f5 omitted) - the post-morph "nudge" the create-world handshake sends. protobuf
		// = {f1=5 version, f2=0 type, f3=scriptGroupId, f6={0,0}}.
		public static byte[] BuildStartScriptGroup(uint playerHandle, uint scriptGroupId)
		{
			var pb = new ProtoScryScriptIIR
			{
				Version = 5,
				type = ProtoScryScriptIIR.Type.StartScriptGroupOnClient,
				IntParameter = ZigZagDecode(scriptGroupId),
				IdParameter = new ProtoID32
				{
					Type = 0,
					Instance = 0
				}
			}.ToProtobuf();

			return new FaolanStream()
				.WriteUInt32(GameServer0X2000Opcodes.ScryScriptIIR)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(playerHandle)
				.WriteByte(0x00)
				.WriteArrayUInt32Length(pb)
				.ToArray();
		}

		// A named scripted step. mode 2 = the simple step-token form ("IntroDone", "ChangeSex", "StartFixed3d", ...);
		// mode 7 = the rich creation step ("NEW_character_creation ..." carried as an f4 <remoteformat> token, an f5
		// resource-preload list "id:value;...", and a non-zero f6 pair). f6Field1/f6Field2 are the trailing sub-message
		// pair (both 0 for the simple steps).
		public static byte[] BuildStep(uint playerHandle, string stepToken, uint mode = 2, uint flag = 0, string resources = "", ulong f6Field1 = 0, ulong f6Field2 = 0)
		{
			var pb = new ProtoScryScriptIIR
			{
				Version = 5,
				type = (ProtoScryScriptIIR.Type)mode,
				IntParameter = ZigZagDecode(flag),
				StringParameterOne = stepToken ?? "",
				StringParameterTwo = resources ?? "",
				IdParameter = new ProtoID32
				{
					Type = (uint)f6Field1,
					Instance = (uint)f6Field2
				}
			}.ToProtobuf();

			return new FaolanStream()
				.WriteUInt32(GameServer0X2000Opcodes.ScryScriptIIR)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(playerHandle)
				.WriteByte(0x00) // framing flag
				.WriteArrayUInt32Length(pb) // u32 BE protobuf length + body
				.ToArray();
		}

		// The raw wire varint -> the sint32 protobuf-net will zigzag-RE-encode back to that exact varint.
		private static int ZigZagDecode(ulong raw)
		{
			return (int)(raw >> 1) ^ -(int)(raw & 1);
		}
	}
}
