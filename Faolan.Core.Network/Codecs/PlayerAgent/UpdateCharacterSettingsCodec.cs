using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.PlayerAgent
{
	/// <summary>
	///     <c>PlayerAgent.UpdateCharacterSettings(ID32 nCharacter, const IOBuffer&amp; data, const String&amp; md5)</c> -
	///     the per-character half of the settings upload, sent on the same mid-session socket as
	///     <c>UpdateSubscriptionSettings</c>. The body is
	///     <c>[u32 characterClass][u32 characterId][ClientSettingsBuffer][u16 length][md5, Latin1]</c>.
	/// </summary>
	/// <remarks>
	///     The archive inside is the UI layout the player arranged for this character: <c>Chat\Windows.xml</c> (chat tabs
	///     and their placement), <c>DockAreas\SideBarLeft.xml</c> / <c>SideBarRight.xml</c>, and a per-character
	///     <c>Prefs_2.xml</c> (404 named values in the capture - shortcut bars, auto-sheathe, block flags, camera
	///     distance/pitch/yaw). 32 KB inflated, 4.6 KB on the wire.
	///     <para>
	///         The server's job is to hand it back at the next login (<c>SendCharacterSettings</c>) and to advertise its
	///         md5 in <c>SendCharacterSettingsCheckSumMap</c>; it never needs to read the files.
	///     </para>
	/// </remarks>
	public static class UpdateCharacterSettingsCodec
	{
		public const uint Method = (uint)PlayerAgentMethods.UpdateCharacterSettings;

		// Canonical: the only method here that writes bytes.
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

	/// <summary>
	///     The per-character settings upload. <c>CheckSum</c> is the client's own md5 of <c>Buffer.Payload</c>; see
	///     <c>UpdateSubscriptionSettingsCodec</c> for why it is carried as sent rather than recomputed.
	/// </summary>
	public sealed record UpdateCharacterSettings(uint CharacterId, ClientSettingsBuffer Buffer, string CheckSum);
}
