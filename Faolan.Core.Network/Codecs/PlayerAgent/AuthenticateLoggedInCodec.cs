using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.PlayerAgent
{
	/// <summary>
	///     <c>PlayerAgent.AuthenticateLoggedIn(ID32 nAccount, uint32 nCookie)</c> - how the client bootstraps the second,
	///     short-lived PlayerAgent socket it opens mid-session, while in world, to push its settings and character
	///     preview. The body is <c>[u32 accountId][u32 cookie]</c>.
	/// </summary>
	/// <remarks>
	///     It is <c>Authenticate</c> minus the trailing auth-status word, and the two words it does carry are
	///     byte-identical to the ones the login connection sent - the same account id and the same session cookie, in all
	///     three captured sightings. The name is the client's: "already logged in", meaning do not re-run the login but
	///     attach this socket to the session. The server's answer is the same <c>AuthenticateAck</c> the plain
	///     <c>Authenticate</c> gets.
	///     <para>
	///         Its 16-bit opcode is shared with <c>ReceivedCharacterSettings</c>. On this interface pair that opcode is
	///         only the method varint's first group, so it names nothing on its own, which is why the identity declared
	///         below is the registration constant and why dispatch must key on that.
	///     </para>
	/// </remarks>
	public static class AuthenticateLoggedInCodec
	{
		public const PlayerAgentMethods MethodOpcode = PlayerAgentMethods.AuthenticateLoggedIn;

		// Canonical: the only method here that writes bytes.
		public static byte[] Build(AuthenticateLoggedIn value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			s.WriteUInt32(value.AccountId).WriteUInt32(value.Cookie);

			return s.ToArray();
		}

		public static AuthenticateLoggedIn Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(AuthenticateLoggedInCodec));
			var value = new AuthenticateLoggedIn(r.ReadU32Be(), r.ReadU32Be());
			r.EnsureAtEnd();

			return value;
		}
	}

	/// <summary>The mid-session socket's bootstrap: which account, and the cookie the login connection was issued.</summary>
	public sealed record AuthenticateLoggedIn(uint AccountId, uint Cookie);
}
