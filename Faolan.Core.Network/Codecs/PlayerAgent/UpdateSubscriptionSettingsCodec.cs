using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.PlayerAgent
{
	/// <summary>
	///     <c>PlayerAgent.UpdateSubscriptionSettings(const IOBuffer&amp; data, const String&amp; md5)</c> - the client
	///     uploading its account-wide settings on the mid-session socket. The body is
	///     <c>[ClientSettingsBuffer][u16 length][md5, Latin1]</c>.
	/// </summary>
	/// <remarks>
	///     There is no id: the settings belong to whatever account the socket authenticated as, which is what makes these
	///     subscription rather than character settings. The archive inside carries <c>hotkeys.xml</c> and the account-wide
	///     <c>Prefs_2.xml</c> (audio, video, chat-bubble and camera preferences - 60 named values in the captures).
	///     <para>
	///         The md5 is over the compressed payload, marker and size included - see <c>ClientSettingsBuffer.CheckSum</c>,
	///         which reproduces both captured sightings exactly. Its purpose is the checksum map the server hands back at
	///         login, so a client can tell whether the copy it cached is still current.
	///     </para>
	/// </remarks>
	public static class UpdateSubscriptionSettingsCodec
	{
		public const PlayerAgentMethods MethodOpcode = PlayerAgentMethods.UpdateSubscriptionSettings;

		// Canonical: the only method here that writes bytes.
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

	/// <summary>
	///     The account-wide settings upload. <c>CheckSum</c> is the client's own md5 of <c>Buffer.Payload</c>, kept as sent
	///     rather than recomputed so a disagreement with <c>Buffer.CheckSum</c> is visible instead of silently papered
	///     over.
	/// </summary>
	public sealed record UpdateSubscriptionSettings(ClientSettingsBuffer Buffer, string CheckSum);
}
