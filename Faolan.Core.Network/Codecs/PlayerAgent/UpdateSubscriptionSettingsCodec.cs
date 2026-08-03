using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.PlayerAgent
{
	// PlayerAgent.UpdateSubscriptionSettings(const IOBuffer& data, const String& md5) - the client uploading its
	// ACCOUNT-wide settings on the mid-session socket. Gitea #611.
	//
	//   [ClientSettingsBuffer][u16 length][md5, LATIN1]
	//
	// No id: the settings belong to whatever account the socket authenticated as, which is what makes these
	// "subscription" rather than character settings. The archive inside carries `hotkeys.xml` and the account-wide
	// `Prefs_2.xml` (audio/video/chat-bubble/camera preferences - 60 named values in the captures).
	//
	// The md5 is over the COMPRESSED payload, marker and size included - see ClientSettingsBuffer.CheckSum, which
	// reproduces both captured sightings exactly. Its purpose is the checksum map the server hands back at login, so a
	// client can tell whether the copy it cached is still current.
	public static class UpdateSubscriptionSettingsCodec
	{
		public const PlayerAgentMethods MethodOpcode = PlayerAgentMethods.UpdateSubscriptionSettings;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(UpdateSubscriptionSettings value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			ClientSettingsBufferCodec.Write(s, value.Buffer);
			s.WriteString(value.CheckSum);

			return s.ToArray();
		}

		public static UpdateSubscriptionSettings Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(UpdateSubscriptionSettingsCodec));
			var value = new UpdateSubscriptionSettings(ClientSettingsBufferCodec.Read(ref r), r.ReadStringU16Length());
			r.EnsureAtEnd();

			return value;
		}
	}

	// The account-wide settings upload. `CheckSum` is the client's own md5 of `Buffer.Payload`; it is kept as SENT
	// rather than recomputed so a disagreement with `Buffer.CheckSum` is visible instead of silently papered over.
	public sealed record UpdateSubscriptionSettings(ClientSettingsBuffer Buffer, string CheckSum);
}
