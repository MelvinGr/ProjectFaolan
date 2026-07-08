using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Faolan.Core.Network.Codecs.Dialogue
{
    // Decodes a captured conversation packet's raw Conan Data into the structured Knubot-screen columns (#188), so
    // the importer can populate ConversationPacket.{SubOpcode,Env,Field,Flag,ScreensJson} and the runtime builds the
    // screen from fields. Pure byte-slicing (no codec dependency); returns null for non-Knubot packets, which keep
    // replaying from Data. Mirrors KnubotCodec's framing: carrier = Data[12 + headerLen ..] = [u32 len][u32 sub]
    // [u32 0xC350][u32 instance][u8 env][u16 ver][u32 0xC350][u32 npc][ body ].
    //
    // The non-Knubot packets in a conversation node (~48 of 355 in faolan.db) are NOT dialogue text - they are
    // DispatchSetDialogueCamera (opcode 0x2015, GameCharAgent 0xA9ECB95D -> GameCharInterface 0x8B87F791): the
    // cinematic conversation camera, body = [0xC350][npcHandle][0xC350][playerHandle][camera params]. No remoteformat
    // text, so nothing to structure; they (plus the interleaved quest-grant packets) replay from Data re-addressed.
    public static class ConversationScreenDecoder
    {
        public const uint AnswerList = 0xF164A4A5;
        public const uint AppendText = 0x4A3D899A;
        public const uint CloseChatWindow = 0xC4AF39EB;
        public const uint OpenChatWindow = 0x045B0B0F;
        public const uint SetDialogueCamera = 0x2015; // GameCharAgent -> GameCharInterface (a 16-bit opcode, not an IIR sub)
        private const uint Marker = 0x0000C350;

        public static Columns Decode(byte[] data)
        {
            if (data == null || data.Length < 12)
                return null;

            var headerLen = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8, 4));
            var start = 12 + headerLen;
            if (start + 8 > data.Length)
                return null;

            var sub = U32(4);
            if (sub is not (OpenChatWindow or AppendText or AnswerList or CloseChatWindow))
                return DecodeCamera(data, headerLen, start);
            if (start + 27 > data.Length || U32(8) != Marker)
                return null;

            var env = data[start + 16];
            var o = 27; // past [len][sub][marker][instance][env][ver][marker2][npc]
            uint? field = null;
            byte? flag = null;
            var screens = new List<string>();

            switch (sub)
            {
                case OpenChatWindow:
                {
                    flag = data[start + o];
                    break;
                }

                case AnswerList:
                {
                    var count = U32(o);
                    o += 4;
                    for (var i = 0; i < count; i++)
                        ReadScreen();
                    break;
                }

                case AppendText:
                {
                    field = U32(o);
                    o += 4;
                    ReadScreen();
                    flag = data[start + o];
                    break;
                }

                default: // CloseChatWindow
                {
                    field = U32(o);
                    o += 4;
                    ReadScreen();
                    break;
                }
            }

            return new Columns(sub, env, field, flag, screens);

            uint U32(int rel)
            {
                return BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(start + rel, 4));
            }

            ushort U16(int rel)
            {
                return BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(start + rel, 2));
            }

            void ReadScreen()
            {
                var ln = U16(o);
                o += 2;
                screens.Add(Encoding.Latin1.GetString(data, start + o, ln));
                o += ln;
            }
        }

        // DispatchSetDialogueCamera (opcode 0x2015 in the rpcHeader, not an IIR sub). Payload (after the rpcHeader)
        // = [u32 0xC350][u32 npc][u32 0xC350][u32 player][CameraParams]. We store the GameCharAgent sender instance
        // (varies per conversation) + the param bytes; the npc/player are re-addressed live at send time.
        private static Columns DecodeCamera(byte[] data, int headerLen, int start)
        {
            // opcode is the 16-bit word ending the rpcHeader (at headerLen-2 from the header start @ offset 12).
            if (10 + headerLen + 2 > data.Length || BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(10 + headerLen, 2)) != SetDialogueCamera)
                return null;

            // Body must be [0xC350][npc][0xC350][player][>=0 params].
            if (start + 16 > data.Length || BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(start, 4)) != Marker || BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(start + 8, 4)) != Marker)
                return null;

            var agentInstance = RpcCallerInstance(data);
            var paramsBytes = data.AsSpan(start + 16).ToArray();
            return new Columns(SetDialogueCamera, null, null, null, null, agentInstance, paramsBytes);
        }

        // The caller endpoint instance from the rpcHeader: [0x0A len][0x0D class:u32][0x10 instance:varint].
        private static uint RpcCallerInstance(byte[] data)
        {
            if (data.Length < 14 || data[12] != 0x0A)
                return 0;

            var i = 14; // past 0x0A, len
            if (data[i] != 0x0D)
                return 0;

            i += 5; // past 0x0D + class u32
            if (data[i] != 0x10)
                return 0;

            i++;
            uint inst = 0;
            var shift = 0;
            while (i < data.Length)
            {
                var x = data[i++];
                inst |= (uint)(x & 0x7F) << shift;
                if ((x & 0x80) == 0)
                    break;

                shift += 7;
            }

            return inst;
        }

        public sealed record Columns(uint SubOpcode, byte? Env, uint? Field, byte? Flag, IReadOnlyList<string> Screens, uint? CameraAgentInstance = null, byte[] CameraParams = null)
        {
            public string ScreensJson =>
                Screens == null
                    ? null
                    : JsonSerializer.Serialize(Screens);
        }
    }
}
