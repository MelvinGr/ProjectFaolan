using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.PlayerAgent
{
	// PlayerInterface.SendCharacterSettings - the s2c half of UpdateCharacterSettings: the stored per-character UI
	// archive handed back once the player has picked a character (retail sends it immediately after LoginCharacter, and
	// the client answers `ReceivedCharacterSettings` when it has applied them). Gitea #611.
	//
	//   [u32 0xC350 (character class)][u32 characterId][ClientSettingsBuffer]
	//
	// No md5: the client already learned that from SendCharacterSettingsCheckSumMap before it chose. All 118 captured
	// sightings are the 12-byte EMPTY form - every character logged in during the captures had nothing stored - which is
	// exactly what an empty buffer encodes, so the empty case is byte-exact against retail.
	public static class SendCharacterSettingsCodec
	{
		public const ushort Opcode = (ushort)PlayerInterfaceOpcodes.SendCharacterSettings;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(CharacterSettings value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			s.WriteUInt32(EntityClass.Character).WriteUInt32(value.CharacterId);
			ClientSettingsBufferCodec.Write(s, value.Buffer);

			return s.ToArray();
		}

		public static CharacterSettings Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(SendCharacterSettingsCodec));
			r.ExpectU32Be(EntityClass.Character, "the character entity class");
			var value = new CharacterSettings(r.ReadU32Be(), ClientSettingsBufferCodec.Read(ref r));
			r.EnsureAtEnd();

			return value;
		}
	}

	// A character's stored settings archive, as served back to the client.
	public sealed record CharacterSettings(uint CharacterId, ClientSettingsBuffer Buffer);
}
