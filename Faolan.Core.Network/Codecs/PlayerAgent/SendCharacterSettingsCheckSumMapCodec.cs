using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.PlayerAgent
{
	/// <summary>
	///     <c>PlayerInterface.SendCharacterSettingsCheckSumMap</c> - one md5 per character on the account, sent in reply
	///     to GetStartupData so the client can tell which of its cached settings archives are still current. The body is
	///     <c>[ListFrame count][ u32 characterId, u16 length, md5 Latin1 ]*</c>.
	/// </summary>
	/// <remarks>
	///     The md5 is the one the client itself sent with <c>UpdateCharacterSettings</c> - MD5 over the compressed buffer,
	///     see <c>ClientSettingsBuffer.CheckSum</c> - and it is the empty string for a character that has never uploaded
	///     any. Retail's maps show both in one message (a fresh character alongside four that carry a checksum, stable
	///     across a year of captures), so an empty entry is a normal value here rather than an omission.
	///     <para>
	///         It is not a checksum of the character: advertising anything but the stored settings' md5 leaves the
	///         client's cache unable to agree with the server.
	///     </para>
	/// </remarks>
	public static class SendCharacterSettingsCheckSumMapCodec
	{
		// The message identity. On PlayerAgent/PlayerInterface that is the method constant rather than an opcode: the
		// 16-bit view keeps 7 of its 32 bits and names two methods at once, so there is no opcode enum to declare against.
		public const uint Method = (uint)PlayerInterfaceMethods.SendCharacterSettingsCheckSumMap;

		// Canonical: the only method here that writes bytes.
		public static byte[] Build(CharacterSettingsCheckSumMap value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			s.WriteUInt32(ListFrame.ForCount(value.Entries.Count));
			foreach (var entry in value.Entries)
				s.WriteUInt32(entry.CharacterId).WriteString(entry.CheckSum);

			return s.ToArray();
		}

		public static CharacterSettingsCheckSumMap Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(SendCharacterSettingsCheckSumMapCodec));
			var count = r.ReadListCount();
			var entries = new List<CharacterSettingsCheckSum>(count);
			for (var i = 0; i < count; i++)
				entries.Add(new CharacterSettingsCheckSum(r.ReadU32Be(), r.ReadStringU16Length()));

			r.EnsureAtEnd();

			return new CharacterSettingsCheckSumMap(entries);
		}
	}

	/// <summary>The whole map: every character on the account, in list order.</summary>
	public sealed record CharacterSettingsCheckSumMap(IReadOnlyList<CharacterSettingsCheckSum> Entries);

	/// <summary>One character's stored-settings checksum; empty when nothing is stored for it.</summary>
	public readonly record struct CharacterSettingsCheckSum(uint CharacterId, string CheckSum);
}
