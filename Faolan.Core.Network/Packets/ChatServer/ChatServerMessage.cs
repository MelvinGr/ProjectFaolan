using System;
using Faolan.Core.Enums;

namespace Faolan.Core.Network.Packets.ChatServer
{
	// Builds a chat wire frame - [u16 opcode][u16 bodyLen][body] - the counterpart to ChatServerPacket, which parses
	// one. This is the chat tier's PacketSerializer: its protocol carries no Conan RpcHeader, so it has no Rpc model,
	// but a send should still read the same everywhere - client.SendAsync(ChatServerMessage.Build(opcode, body)) - and
	// the body-length framing belongs in ONE place rather than being re-spelled at every call site.
	public static class ChatServerMessage
	{
		public static byte[] Build(ChatServerInterfaceOpcodes opcode, Action<FaolanStream>? body = null)
		{
			return Build((ushort)opcode, body);
		}

		// The ushort overload is for the frames whose opcode isn't recovered yet (the 0x0005 chat-connect beat), so they
		// stay honest hex instead of being given an invented name in the enum.
		public static byte[] Build(ushort opcode, Action<FaolanStream>? body = null)
		{
			using var stream = new FaolanStream();
			stream.WriteUInt16(opcode).WriteArrayUInt16Length(s => body?.Invoke(s));

			return stream.ToArray();
		}
	}
}
