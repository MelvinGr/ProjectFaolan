using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.World
{
	/// <summary>
	///     RequestACGTeleport - the player answered yes to the instance bind box. Decoded byte-exact from
	///     xxx_mage-necromancer_login_play3 frame 4732, 48 seconds after the box went up.
	/// </summary>
	/// <remarks>
	///     The body is a bare <see cref="PlayfieldProxy" /> and nothing else - 34 bytes, the destination proxy
	///     DispatchACGTeleportChoice put up, echoed back verbatim. There is no answer field: sending it is the yes, and a
	///     decline sends nothing at all, so the server cannot tell a no from walking away, which is presumably why the box
	///     binds nothing until this arrives.
	///     <para>
	///         It carries no character id: the addressed instance rides the RPC header and the proxy names the destination,
	///         so a handler has to take the player from the connection rather than from the body.
	///     </para>
	/// </remarks>
	public static class AcgTeleportRequestCodec
	{
		public const uint Method = (uint)GameCharAgentMethods.RequestACGTeleport;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
		public static byte[] Build(AcgTeleportRequest value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static byte[] Build(PlayfieldProxy proxy)
		{
			return Build(new AcgTeleportRequest(proxy));
		}

		public static AcgTeleportRequest Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(AcgTeleportRequestCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static AcgTeleportRequest Read(ref SpanReader r)
		{
			return new AcgTeleportRequest(PlayfieldProxy.Read(ref r));
		}

		internal static void Write(FaolanStream s, AcgTeleportRequest v)
		{
			v.Proxy.Write(s);
		}
	}

	/// <summary>
	///     The RequestACGTeleport body - "yes, put me in that instance". The proxy is the destination the choice box
	///     offered.
	/// </summary>
	public sealed record AcgTeleportRequest(PlayfieldProxy Proxy)
	{
		public uint MapId => Proxy.MapId;
	}
}
