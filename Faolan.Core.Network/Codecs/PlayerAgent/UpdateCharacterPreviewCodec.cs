using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.PlayerAgent
{
	// PlayerAgent.UpdateCharacterPreview(ID32 nCharacter, const IOBuffer& data) - the client uploading the character's
	// SELECT-SCREEN preview, on the same mid-session socket as the settings. Gitea #611.
	//
	//   [u32 0xC350 (character class)][u32 characterId][ClientSettingsBuffer]
	//
	// No md5 trailer: unlike the two settings methods the preview has no checksum map to feed, so the client never
	// advertises one. The buffer holds a single archive member, `preview.bin` - a binary appearance record (id/float
	// triples plus texscript names such as `*texscript_1108153`) whose internals are not decoded; the server stores it
	// verbatim and streams it straight back as SendCharacterPreview, one per character, before the character list.
	// That is what draws the posed, geared character on the select screen without the client having to load the world.
	public static class UpdateCharacterPreviewCodec
	{
		public const PlayerAgentMethods MethodOpcode = PlayerAgentMethods.UpdateCharacterPreview;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(UpdateCharacterPreview value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			s.WriteUInt32(EntityClass.Character).WriteUInt32(value.CharacterId);
			ClientSettingsBufferCodec.Write(s, value.Buffer);

			return s.ToArray();
		}

		public static UpdateCharacterPreview Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(UpdateCharacterPreviewCodec));
			r.ExpectU32Be(EntityClass.Character, "the character entity class");
			var value = new UpdateCharacterPreview(r.ReadU32Be(), ClientSettingsBufferCodec.Read(ref r));
			r.EnsureAtEnd();

			return value;
		}
	}

	// The character-select preview upload.
	public sealed record UpdateCharacterPreview(uint CharacterId, ClientSettingsBuffer Buffer);
}
