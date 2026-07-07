using System;
using System.Buffers.Binary;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Protobuf;

namespace Faolan.Core.Network.Codecs.GameServer
{
	// Locates and decodes the ProtoCoreCharacterIIR (name / stats / player_stats / bcc / ... / inventory_entry) inside
	// an arbitrary SimpleCharFullUpdate (FA015807) body - unlike SpawnPacketBuilder's fixed Strie-layout offsets,
	// this works for any character's spawn (different name length, different amount of equipment -> different size).
	// It is the read side of player-spawn generation: decoding a captured spawn (e.g. necrol80's, which is in the DB
	// burst) yields the worn equipment (InventoryEntries) + stats to extract into CharacterEquipment/CharacterInventory.
	//
	// The message is a length-delimited block: a 4-byte big-endian length immediately precedes its first protobuf
	// field, which is field 1 (name) = 0x0A <len> <utf8>. We scan for that signature (the character name is the first
	// printable string in the body; the equipment/item strings live inside the message, after the name), verify the
	// preceding length matches a decodable ProtoCoreCharacterIIR, and return it.
	public static class PlayerSpawnReader
	{
		public static bool TryReadCharacterMessage(byte[] body, out Result result)
		{
			result = default;
			if (body == null || body.Length < 8)
				return false;

			for (var i = 4; i < body.Length - 2; i++)
			{
				if (body[i] != 0x0A) // field 1 (name), wiretype 2
					continue;

				int nameLen = body[i + 1];
				if (nameLen is < 1 or > 63 || i + 2 + nameLen > body.Length)
					continue;
				if (!IsPrintableAscii(body, i + 2, nameLen))
					continue;

				var messageLength = (int)BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(i - 4, 4));
				if (messageLength < nameLen + 2 || i + messageLength > body.Length)
					continue;

				try
				{
					var message = body.AsSpan(i, messageLength).FromProtobuf<ProtoCoreCharacterIIR>();
					if (!string.IsNullOrEmpty(message.Name) && message.Name.Length == nameLen)
					{
						result = new Result(i, messageLength, message);
						return true;
					}
				}
				catch
				{
					// not the character message - keep scanning
				}
			}

			return false;
		}

		private static bool IsPrintableAscii(byte[] data, int start, int length)
		{
			for (var i = start; i < start + length; i++)
			{
				if (data[i] < 0x20 || data[i] > 0x7E)
					return false;
			}

			return true;
		}

		public readonly record struct Result(int Offset, int Length, ProtoCoreCharacterIIR Message);
	}
}
