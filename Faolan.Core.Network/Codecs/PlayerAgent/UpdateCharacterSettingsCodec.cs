using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.PlayerAgent
{
	// PlayerAgent.UpdateCharacterSettings(ID32 nCharacter, const IOBuffer& data, const String& md5) - the PER-CHARACTER
	// half of the settings upload, sent on the same mid-session socket as UpdateSubscriptionSettings. Gitea #611.
	//
	//   [u32 0xC350 (character class)][u32 characterId][ClientSettingsBuffer][u16 length][md5, LATIN1]
	//
	// The archive inside is the UI layout the player arranged for THIS character: `Chat\Windows.xml` (chat tabs and
	// their placement), `DockAreas\SideBarLeft.xml` / `SideBarRight.xml`, and a per-character `Prefs_2.xml` (404 named
	// values in the capture - shortcut bars, auto-sheathe, block flags, camera distance/pitch/yaw...). 32 KB inflated,
	// 4.6 KB on the wire.
	//
	// The server's job is to hand it back at the next login (SendCharacterSettings) and to advertise its md5 in
	// SendCharacterSettingsCheckSumMap; it never needs to read the files.
	public static class UpdateCharacterSettingsCodec
	{
		public const PlayerAgentMethods MethodOpcode = PlayerAgentMethods.UpdateCharacterSettings;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(UpdateCharacterSettings value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			s.WriteUInt32(EntityClass.Character).WriteUInt32(value.CharacterId);
			ClientSettingsBufferCodec.Write(s, value.Buffer);
			s.WriteString(value.CheckSum);

			return s.ToArray();
		}

		public static UpdateCharacterSettings Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(UpdateCharacterSettingsCodec));
			r.ExpectU32Be(EntityClass.Character, "the character entity class");
			var value = new UpdateCharacterSettings(r.ReadU32Be(), ClientSettingsBufferCodec.Read(ref r), r.ReadStringU16Length());
			r.EnsureAtEnd();

			return value;
		}
	}

	// The per-character settings upload. `CheckSum` is the client's own md5 of `Buffer.Payload` - see
	// UpdateSubscriptionSettings for why it is carried as sent rather than recomputed.
	public sealed record UpdateCharacterSettings(uint CharacterId, ClientSettingsBuffer Buffer, string CheckSum);
}
