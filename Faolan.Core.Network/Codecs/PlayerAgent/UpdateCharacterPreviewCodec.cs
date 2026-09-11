using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.PlayerAgent
{
	/// <summary>
	///     <c>PlayerAgent.UpdateCharacterPreview(ID32 nCharacter, const IOBuffer&amp; data)</c> - the client uploading the
	///     character's select-screen preview, on the same mid-session socket as the settings. The body is
	///     <c>[u32 characterClass][u32 characterId][ClientSettingsBuffer]</c>.
	/// </summary>
	/// <remarks>
	///     There is no md5 trailer: unlike the two settings methods the preview has no checksum map to feed, so the client
	///     never advertises one. The buffer holds a single archive member, <c>preview.bin</c> (see
	///     <see cref="CharacterPreviewSetCodec" />); the server stores it verbatim and streams it straight back as
	///     <c>SendCharacterPreview</c>, one per character, before the character list. That is what draws the posed, geared
	///     character on the select screen without the client having to load the world.
	/// </remarks>
	public static class UpdateCharacterPreviewCodec
	{
		public const PlayerAgentMethods MethodOpcode = PlayerAgentMethods.UpdateCharacterPreview;

		// Canonical: the only method here that writes bytes.
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

	/// <summary>The character-select preview upload.</summary>
	public sealed record UpdateCharacterPreview(uint CharacterId, ClientSettingsBuffer Buffer);
}
