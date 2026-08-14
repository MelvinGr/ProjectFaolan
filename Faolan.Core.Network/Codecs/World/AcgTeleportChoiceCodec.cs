using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	// DispatchACGTeleportChoice (GameCharInterface 0x56) - the confirmation box retail puts up before it moves the
	// player into an INSTANCED playfield (#821). Decoded byte-exact from xxx_mage-necromancer_login_play3 frame 4555,
	// the Tortage harbour rowboat to White Sands Night, which is the only capture of the ACG flow in the corpus.
	//
	//   [u32 destMapId][u32 0xC350 Character][u32 clientInstance][PlayfieldProxy][u8 trailing]
	//
	// The player's own annotation names what the box says: "there is a confirmation to enter (saying bound to
	// <charname>)" - it is the "this instance will be bound to you" prompt, and the arrival is followed by an
	// `InstanceOwnerAndResetTime` feedback token. So the proxy here is the DESTINATION's instance, and its owner slots
	// are what make the instance personal.
	//
	// ⚠️ The destination map is stated TWICE - once as the leading word and once inside the proxy - and both read
	// 10803 in the capture. Parse checks they agree rather than reading one and trusting it, because a disagreement
	// would mean the record is not what this codec thinks it is.
	//
	// The client answers with `AcgTeleportRequestCodec`, which echoes the proxy back verbatim.
	public static class AcgTeleportChoiceCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchACGTeleportChoice;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(AcgTeleportChoice value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static byte[] Build(uint clientInstance, uint destMapId, uint instanceToken)
		{
			return Build(new AcgTeleportChoice(clientInstance, PlayfieldProxy.ForInstance(destMapId, instanceToken)));
		}

		public static AcgTeleportChoice Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(AcgTeleportChoiceCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static AcgTeleportChoice Read(ref SpanReader r)
		{
			var mapOffset = r.Position;
			var destMapId = r.ReadU32Be();
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var clientInstance = r.ReadU32Be();
			var proxy = PlayfieldProxy.Read(ref r);
			var trailing = r.ReadU8();

			if (proxy.MapId != destMapId)
				throw CodecParseException.Unexpected(nameof(AcgTeleportChoiceCodec), mapOffset, $"the proxy to name the same map as the leading word ({destMapId})", proxy.MapId.ToString());

			return new AcgTeleportChoice(clientInstance, proxy, trailing);
		}

		internal static void Write(FaolanStream s, AcgTeleportChoice v)
		{
			s.WriteUInt32(v.Proxy.MapId).WriteUInt32(EntityClass.Character).WriteUInt32(v.ClientInstance);
			v.Proxy.Write(s);
			s.WriteByte(v.Trailing);
		}
	}

	// DispatchACGTeleportChoice - "do you want to enter this instance?". The destination lives in the proxy; MapId is
	// exposed for the caller that only wants to know where the boat goes.
	public sealed record AcgTeleportChoice(uint ClientInstance, PlayfieldProxy Proxy, byte Trailing = 0)
	{
		public uint MapId => Proxy.MapId;
	}
}
