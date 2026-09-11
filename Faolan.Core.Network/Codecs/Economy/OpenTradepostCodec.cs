using System;
using System.Numerics;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Economy
{
	/// <summary>
	///     <c>DispatchOpenTradepost</c> - the auction-house access point: where the auctioneer stands and how far the
	///     client lets the player wander before the window closes.
	///     <c>[f32 x][f32 y][f32 z][f32 radius]</c>, raw-body floats, big-endian.
	/// </summary>
	/// <remarks>
	///     The client runs a within-radius check against this, which is why a cold open needs a real position - the
	///     capture shows DispatchOpenTradepost preceding TradepostOpen. Byte-exact in <c>TradepostResponseTests</c>.
	/// </remarks>
	public static class OpenTradepostCodec
	{
		public const uint Method = (uint)TradepostInterfaceMethods.DispatchOpenTradepost;

		// The radius retail sends with the Tortage auctioneer.
		public const float AccessRadius = 15.0f;

		// Canonical: the only method here that writes bytes.
		public static byte[] Build(OpenTradepost value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static byte[] Build(Vector3 position, float radius = AccessRadius)
		{
			return Build(new OpenTradepost(position, radius));
		}

		public static OpenTradepost Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(OpenTradepostCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static OpenTradepost Read(ref SpanReader r)
		{
			return new OpenTradepost(r.ReadVector3Be(), r.ReadF32Be());
		}

		internal static void Write(FaolanStream s, OpenTradepost v)
		{
			s.WriteVector3(v.Position).WriteFloat(v.Radius);
		}
	}

	/// <summary>The DispatchOpenTradepost body.</summary>
	public sealed record OpenTradepost(Vector3 Position, float Radius);
}
