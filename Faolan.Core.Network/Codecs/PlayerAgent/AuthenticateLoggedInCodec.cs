using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.PlayerAgent
{
	// PlayerAgent.AuthenticateLoggedIn(ID32 nAccount, uint32 nCookie) - how the client bootstraps the SECOND, short-lived
	// PlayerAgent socket it opens mid-session (while in world) to push its settings and character preview. Gitea #611.
	//
	//   [u32 accountId][u32 cookie]
	//
	// It is `Authenticate` minus the trailing auth-status word, and the two it does carry are byte-identical to the ones
	// the login connection sent: the same account id and the same session cookie, in all three captured sightings. The
	// name is the client's - "already logged in", i.e. do not re-run the login, just attach this socket to the session -
	// and the server's answer is the same `AuthenticateAck` the plain `Authenticate` gets.
	//
	// ⚠️ Its opcode 0x20EC is ALSO `ReceivedCharacterSettings`'. On this interface pair the 16-bit opcode is only the
	// method varint's first group (`0x2080 | (constant & 0x7F)`), so it names nothing on its own - which is why the
	// identity recorded below is the registration CONSTANT (#608 / #610), and why dispatch must key on that.
	public static class AuthenticateLoggedInCodec
	{
		public const PlayerAgentMethods MethodOpcode = PlayerAgentMethods.AuthenticateLoggedIn;

		// CANONICAL - the only method here that writes bytes.
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

	// The mid-session socket's bootstrap: which account, and the cookie the login connection was issued.
	public sealed record AuthenticateLoggedIn(uint AccountId, uint Cookie);
}
