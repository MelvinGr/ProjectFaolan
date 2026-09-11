using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.PlayerAgent
{
	/// <summary>
	///     <c>PlayerInterface.SendCharacterSettings</c> - the s2c half of <c>UpdateCharacterSettings</c>: the stored
	///     per-character UI archive handed back once the player has picked a character. Retail sends it immediately after
	///     LoginCharacter, and the client answers <c>ReceivedCharacterSettings</c> when it has applied them. The body is
	///     <c>[u32 characterClass][u32 characterId][ClientSettingsBuffer]</c>.
	/// </summary>
	/// <remarks>
	///     There is no md5: the client already learned that from <c>SendCharacterSettingsCheckSumMap</c> before it chose.
	///     All 118 captured sightings are the 12-byte empty form, since every character logged in during the captures had
	///     nothing stored, which is exactly what an empty buffer encodes.
	/// </remarks>
	public static class SendCharacterSettingsCodec
	{
		// The message identity. On PlayerAgent/PlayerInterface that is the method constant rather than an opcode: the
		// 16-bit view keeps 7 of its 32 bits and names two methods at once, so there is no opcode enum to declare against.
		public const uint Method = (uint)PlayerInterfaceMethods.SendCharacterSettings;

		// Canonical: the only method here that writes bytes.
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

	/// <summary>A character's stored settings archive, as served back to the client.</summary>
	public sealed record CharacterSettings(uint CharacterId, ClientSettingsBuffer Buffer);
}
