using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.World
{
	// RequestACGTeleport (GameCharAgent 0x60) - the player answered YES to the instance bind box (#821). Decoded
	// byte-exact from xxx_mage-necromancer_login_play3 frame 4732, 48 s after the box went up.
	//
	// The body is a bare `PlayfieldProxy` and nothing else - 34 bytes, the destination proxy the server put in
	// `DispatchACGTeleportChoice` echoed back verbatim. There is no answer field: SENDING it is the yes, and a decline
	// sends nothing at all (the capture shows the box up at t=476.4 and this at t=524.5, with no other ACG traffic in
	// between). So the server cannot tell "no" from "walked away", which is presumably why the box binds nothing until
	// this arrives.
	//
	// ⚠️ It carries no character id. The addressed instance rides the RPC header, and the proxy names the DESTINATION,
	// so a handler has to take the player from the connection rather than from the body.
	public static class AcgTeleportRequestCodec
	{
		public const uint Method = (uint)GameCharAgentMethods.RequestACGTeleport;

		// CANONICAL - the only method here that writes bytes.
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

	// RequestACGTeleport - "yes, put me in that instance". The proxy is the destination the choice box offered.
	public sealed record AcgTeleportRequest(PlayfieldProxy Proxy)
	{
		public uint MapId => Proxy.MapId;
	}
}
