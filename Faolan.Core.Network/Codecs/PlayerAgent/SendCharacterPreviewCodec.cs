using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.PlayerAgent
{
	/// <summary>
	///     <c>PlayerInterface.SendCharacterPreview</c> - the s2c half of <c>UpdateCharacterPreview</c>: the stored
	///     <c>preview.bin</c> for one character, streamed back before the character list so the select screen can pose
	///     that character. The body is <c>[u32 characterClass][u32 characterId][ClientSettingsBuffer]</c>.
	/// </summary>
	/// <remarks>
	///     Retail sends one of these per character that has a preview, in reply to GetStartupData and ahead of
	///     <c>SendCharacterSettingsCheckSumMap</c>; characters with nothing stored are skipped rather than sent an empty
	///     buffer (229 sightings in the corpus, never an empty one). The body is byte-identical to the c2s upload's, which
	///     is the whole mechanism: the client authors its own preview and the server only keeps it.
	/// </remarks>
	public static class SendCharacterPreviewCodec
	{
		// The message identity. On PlayerAgent/PlayerInterface that is the method constant rather than an opcode: the
		// 16-bit view keeps 7 of its 32 bits and names two methods at once, so there is no opcode enum to declare against.
		public const uint Method = (uint)PlayerInterfaceMethods.SendCharacterPreview;

		// The archive's only member, on both directions of the message.
		public const string PreviewFileName = "preview.bin";

		// Canonical: the only method here that writes bytes.
		public static byte[] Build(CharacterPreview value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			s.WriteUInt32(EntityClass.Character).WriteUInt32(value.CharacterId);
			ClientSettingsBufferCodec.Write(s, value.Buffer);

			return s.ToArray();
		}

		// A preview authored from a character's own appearance rather than echoed back from an upload. Nothing in the server
		// calls this: retail sends no preview for a character that has never logged in, and a record the client rejects is
		// worse than the no-preview state retail itself produces. It exists so a constructed record can be put in front of
		// the client and checked.
		public static byte[] Build(uint characterId, CharacterPreviewSet preview)
		{
			ArgumentNullException.ThrowIfNull(preview);
			var buffer = ClientSettingsBufferCodec.Build([new ClientFile(PreviewFileName, CharacterPreviewSetCodec.Build(preview))]);

			return Build(new CharacterPreview(characterId, ClientSettingsBufferCodec.Parse(buffer)));
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

	/// <summary>A character's stored select-screen preview.</summary>
	public sealed record CharacterPreview(uint CharacterId, ClientSettingsBuffer Buffer);
}
