using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.PlayerAgent
{
	// PlayerInterface.SendCharacterPreview - the s2c half of UpdateCharacterPreview: the stored `preview.bin` for ONE
	// character, streamed back before the character list so the select screen can pose that character. Gitea #611.
	//
	//   [u32 0xC350 (character class)][u32 characterId][ClientSettingsBuffer]
	//
	// Retail sends one of these per character that HAS a preview, in reply to GetStartupData and ahead of
	// SendCharacterSettingsCheckSumMap; characters with nothing stored are simply skipped rather than sent an empty
	// buffer (229 sightings in the corpus, never an empty one). The body is byte-identical to the c2s upload's, which
	// is the whole mechanism: the client authors its own preview and the server only keeps it.
	public static class SendCharacterPreviewCodec
	{
		public const ushort Opcode = (ushort)PlayerInterfaceOpcodes.SendCharacterPreview;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(CharacterPreview value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			s.WriteUInt32(EntityClass.Character).WriteUInt32(value.CharacterId);
			ClientSettingsBufferCodec.Write(s, value.Buffer);

			return s.ToArray();
		}

		public static CharacterPreview Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(SendCharacterPreviewCodec));
			r.ExpectU32Be(EntityClass.Character, "the character entity class");
			var value = new CharacterPreview(r.ReadU32Be(), ClientSettingsBufferCodec.Read(ref r));
			r.EnsureAtEnd();

			return value;
		}
	}

	// A character's stored select-screen preview.
	public sealed record CharacterPreview(uint CharacterId, ClientSettingsBuffer Buffer);
}
