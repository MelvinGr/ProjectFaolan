using Faolan.Core.Network;
using Faolan.Core.Network.Opcodes;
using Faolan.Core.Network.Packets;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Faolan.Core.Network.Codecs.GameServer
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
        public const uint SubOpcode = (uint)GameServer0X2000Opcodes.ScryScriptIIR;
        public const uint TypeTagAck = EntityClass.Character;

        // The no-token form: a StartScriptGroupOnClient (type 0) callback that runs a script group by id, with no
        // step-token/resources (f4/f5 omitted) - the post-morph "nudge" the create-world handshake sends. protobuf
        // = {f1=5 version, f2=0 type, f3=scriptGroupId, f6={0,0}}.
        public static byte[] BuildStartScriptGroup(uint playerHandle, uint scriptGroupId)
        {
            var pb = new List<byte>();
            WriteVarintField(pb, 1, 5); // version
            WriteVarintField(pb, 2, 0); // type = StartScriptGroupOnClient
            WriteVarintField(pb, 3, scriptGroupId);
            var f6 = new List<byte>();
            WriteVarintField(f6, 1, 0);
            WriteVarintField(f6, 2, 0);
            WriteBytesField(pb, 6, f6.ToArray());

            var body = new List<byte>(16 + pb.Count);
            WriteU32(body, SubOpcode);
            WriteU32(body, TypeTagAck);
            WriteU32(body, playerHandle);
            body.Add(0x00);
            WriteU32(body, (uint)pb.Count);
            body.AddRange(pb);
            return body.ToArray();
        }

        // A named scripted step. mode 2 = the simple step-token form ("IntroDone", "ChangeSex", "StartFixed3d", ...);
        // mode 7 = the rich creation step ("NEW_character_creation ..." carried as an f4 <remoteformat> token, an f5
        // resource-preload list "id:value;...", and a non-zero f6 pair). f6Field1/f6Field2 are the trailing sub-message
        // pair (both 0 for the simple steps).
        public static byte[] BuildStep(uint playerHandle, string stepToken, uint mode = 2, uint flag = 0, string resources = "", ulong f6Field1 = 0, ulong f6Field2 = 0)
        {
            var pb = new List<byte>();
            WriteVarintField(pb, 1, 5); // version
            WriteVarintField(pb, 2, mode);
            WriteVarintField(pb, 3, flag);
            WriteStringField(pb, 4, stepToken);
            WriteStringField(pb, 5, resources);
            var f6 = new List<byte>();
            WriteVarintField(f6, 1, f6Field1);
            WriteVarintField(f6, 2, f6Field2);
            WriteBytesField(pb, 6, f6.ToArray());

            var body = new List<byte>(16 + pb.Count);
            WriteU32(body, SubOpcode);
            WriteU32(body, TypeTagAck);
            WriteU32(body, playerHandle);
            body.Add(0x00); // framing flag
            WriteU32(body, (uint)pb.Count); // u32 BE protobuf length
            body.AddRange(pb);
            return body.ToArray();
        }

        private static void WriteBytesField(List<byte> w, int field, byte[] value)
        {
            Varint.WriteVarint(w, (ulong)((field << 3) | 2)); // wire type 2 (length-delimited)
            Varint.WriteVarint(w, (ulong)value.Length);
            w.AddRange(value);
        }

        private static void WriteStringField(List<byte> w, int field, string value)
        {
            WriteBytesField(w, field, Encoding.UTF8.GetBytes(value ?? ""));
        }

        private static void WriteU32(List<byte> w, uint v)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, v);
            w.Add(b[0]);
            w.Add(b[1]);
            w.Add(b[2]);
            w.Add(b[3]);
        }

        private static void WriteVarintField(List<byte> w, int field, ulong value)
        {
            Varint.WriteVarint(w, (ulong)(field << 3)); // wire type 0 (varint)
            Varint.WriteVarint(w, value);
        }
    }
}
