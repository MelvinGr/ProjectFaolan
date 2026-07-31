using System.Numerics;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Combat
{
	// Reads the ground-target position out of a CastSpell BehaviorControlCenterIIR's TAIL (BehaviorId 4, Magic:Attack).
	//
	// Not a codec: it reads a field out of ANOTHER message's opaque region, so there is no message here to Build and no
	// body to consume to the end - the same shape `PlayfieldTokenScan` has in World/. It is deliberately tolerant
	// (returns false rather than throwing) for exactly that reason: the caller is asking a question ABOUT a tail, not
	// decoding one.
	//
	// A ground-targeted AoE (e.g. Deathly Blizzard) is aimed at a RETICLE POSITION rather than a locked entity, so the
	// cast carries no target handle - instead the tail after the spell id is
	//   [u8 kind=0][f32 X][f32 Y][f32 Z][u32 0][u32 0][u32 0x41A][u8 1]   (raw-body floats = big-endian)
	// An ENTITY-targeted cast (e.g. Ice Strike) sends kind=1 and an UNUSED position (the target rides
	// CommandMessage.Target instead - its tail Z is a garbage denormal, so "position != 0" alone is NOT a safe
	// discriminator; the leading kind byte is). Decoded from NecroL80_..._actions-described-in-chat (the "Casting
	// Deathly Blizzard ... (AOE)" casts) vs Stickienecro ice-strike (#236). The caller also gates on Target == null.
	public static class CastGroundTargetScan
	{
		private const byte GroundTargetKind = 0; // tail[0]; 1 = entity-targeted (no reticle)

		// True (with the reticle position) when the tail carries a ground target (kind byte 0 + a non-zero position);
		// false for an entity-targeted / self cast (short tail, kind 1, or a zeroed position).
		public static bool TryReadPosition(byte[] tail, out Vector3 position)
		{
			position = default;
			if (tail == null)
				return false;

			try
			{
				var r = new SpanReader(tail, nameof(CastGroundTargetScan));
				if (r.ReadU8() != GroundTargetKind)
					return false;

				position = r.ReadVector3Be();
			}
			catch (CodecParseException)
			{
				return false; // too short to carry a reticle
			}

			return position != Vector3.Zero;
		}
	}
}
