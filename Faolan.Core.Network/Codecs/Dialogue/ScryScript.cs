using System.Text;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Dialogue
{
	// ScryScriptIIR (0xA36D3B74) — the Tortage destiny/tutorial STEP driver (the "QuestRewards" of the
	// opcode catalog is a misnomer; #91 / #149). Server→client it pushes a named scripted step the client's
	// intro flow runs (e.g. "PlayOrSkip", "IntroDone", "ChangeSex", "ApproachCasilda", or the big
	// "NEW_character_creation Slave Galley" creation step). Decoded byte-exact from captures (Captures/
	// decode_quest.py + ScryScriptIIR ReadSubClass), built here from scratch.
	//
	// Wire (subopcode onward; the 4-byte packet length is added by the send layer):
	//   [subop 0xA36D3B74][ack 0x0000C350][u32 playerHandle][u8 flag=0][u32 BE pbLen][protobuf pbLen bytes]
	// protobuf: f1=5 (version), f2=mode, f3=flag, f4=stepToken (string), f5=resources (string, usually empty),
	//   f6 = sub-message {f1=0, f2=0}.
	public static class ScryScript
	{
		// The no-token form: a StartScriptGroupOnClient (type 0) callback that runs a script group by id, with no
		// step-token/resources (f4/f5 omitted) - the post-morph "nudge" the create-world handshake sends. protobuf
		// = {f1=5 version, f2=0 type, f3=scriptGroupId, f6={0,0}}.
		public static byte[] BuildStartScriptGroup(uint playerHandle, uint scriptGroupId)
		{
			var pb = new FaolanStream();
			WriteVarintField(pb, 1, 5); // version
			WriteVarintField(pb, 2, 0); // type = StartScriptGroupOnClient
			WriteVarintField(pb, 3, scriptGroupId);
			var f6 = new FaolanStream();
			WriteVarintField(f6, 1, 0);
			WriteVarintField(f6, 2, 0);
			WriteBytesField(pb, 6, f6.ToArray());

			return new FaolanStream()
				.WriteUInt32(GameServer0X2000Opcodes.ScryScriptIIR)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(playerHandle)
				.WriteByte(0x00)
				.WriteArrayUInt32Length(pb.ToArray())
				.ToArray();
		}

		// A named scripted step. mode 2 = the simple step-token form ("IntroDone", "ChangeSex", "StartFixed3d", ...);
		// mode 7 = the rich creation step ("NEW_character_creation ..." carried as an f4 <remoteformat> token, an f5
		// resource-preload list "id:value;...", and a non-zero f6 pair). f6Field1/f6Field2 are the trailing sub-message
		// pair (both 0 for the simple steps).
		public static byte[] BuildStep(uint playerHandle, string stepToken, uint mode = 2, uint flag = 0, string resources = "", ulong f6Field1 = 0, ulong f6Field2 = 0)
		{
			var pb = new FaolanStream();
			WriteVarintField(pb, 1, 5); // version
			WriteVarintField(pb, 2, mode);
			WriteVarintField(pb, 3, flag);
			WriteStringField(pb, 4, stepToken);
			WriteStringField(pb, 5, resources);
			var f6 = new FaolanStream();
			WriteVarintField(f6, 1, f6Field1);
			WriteVarintField(f6, 2, f6Field2);
			WriteBytesField(pb, 6, f6.ToArray());

			return new FaolanStream()
				.WriteUInt32(GameServer0X2000Opcodes.ScryScriptIIR)
				.WriteUInt32(EntityClass.Character)
				.WriteUInt32(playerHandle)
				.WriteByte(0x00) // framing flag
				.WriteArrayUInt32Length(pb.ToArray()) // u32 BE protobuf length + body
				.ToArray();
		}

		private static void WriteBytesField(FaolanStream w, int field, byte[] value)
		{
			w.WriteVarint((ulong)((field << 3) | 2)); // wire type 2 (length-delimited)
			w.WriteVarint((ulong)value.Length);
			w.WriteArray(value);
		}

		private static void WriteStringField(FaolanStream w, int field, string value)
		{
			WriteBytesField(w, field, Encoding.UTF8.GetBytes(value ?? ""));
		}

		private static void WriteVarintField(FaolanStream w, int field, ulong value)
		{
			w.WriteVarint((ulong)(field << 3)); // wire type 0 (varint)
			w.WriteVarint(value);
		}
	}
}
