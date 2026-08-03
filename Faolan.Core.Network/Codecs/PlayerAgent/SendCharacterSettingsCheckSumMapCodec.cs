using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.PlayerAgent
{
	// PlayerInterface.SendCharacterSettingsCheckSumMap - one md5 per character on the account, sent in reply to
	// GetStartupData so the client can tell which of its cached settings archives are still current. Gitea #611.
	//
	//   [ListFrame count][ u32 characterId, u16 length, md5 LATIN1 ]*
	//
	// The md5 is the one the client itself sent with UpdateCharacterSettings - MD5 over the COMPRESSED buffer, see
	// ClientSettingsBuffer.CheckSum - and it is the EMPTY string for a character that has never uploaded any. Retail's
	// maps show both in one message (a fresh character alongside four that carry a checksum, stable across a year of
	// captures), so an empty entry is a normal value here rather than an omission.
	//
	// ⚠️ Not a checksum of the CHARACTER: the map was previously filled with the account's auth challenge, which
	// advertised a checksum for settings the server did not have, so the client's cache could never agree with it.
	public static class SendCharacterSettingsCheckSumMapCodec
	{
		// The message identity. On PlayerAgent/PlayerInterface that is the method CONSTANT, not an opcode - the
		// 16-bit view keeps 7 of its 32 bits and names two methods at once, so there is no opcode enum to declare
		// against (#613).
		public const uint Method = (uint)PlayerInterfaceMethods.SendCharacterSettingsCheckSumMap;

		// CANONICAL - the only method here that writes bytes.
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

	// The whole map: every character on the account, in list order.
	public sealed record CharacterSettingsCheckSumMap(IReadOnlyList<CharacterSettingsCheckSum> Entries);

	// One character's stored-settings checksum; empty when nothing is stored for it.
	public readonly record struct CharacterSettingsCheckSum(uint CharacterId, string CheckSum);
}
