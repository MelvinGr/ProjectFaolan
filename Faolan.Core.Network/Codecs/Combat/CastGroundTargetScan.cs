using System.Numerics;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Combat
{
	/// <summary>
	///     Reads the ground-target position out of a CastSpell BehaviorControlCenterIIR's tail (BehaviorId 4,
	///     Magic:Attack).
	/// </summary>
	/// <remarks>
	///     Not a codec: it reads a field out of another message's opaque region, so there is no message to build and no body
	///     to consume to the end - the shape <see cref="World.PlayfieldTokenScan" /> also has. It is deliberately tolerant,
	///     returning false rather than throwing, because the caller is asking a question about a tail rather than decoding
	///     one.
	///     <para>
	///         A ground-targeted AoE (Deathly Blizzard, say) is aimed at a reticle position rather than a locked entity, so
	///         the cast carries no target handle. The tail after the spell id is
	///         <c>[u8 kind=0][f32 X][f32 Y][f32 Z][u32 0][u32 0][u32 0x41A][u8 1]</c>, the floats big-endian as raw-body
	///         floats always are. An entity-targeted cast (Ice Strike) sends kind 1 and an unused position - the target rides
	///         CommandMessage.Target instead, and the unused tail Z is a garbage denormal, so the leading kind byte is the
	///         only safe discriminator. Decoded from NecroL80_..._actions-described-in-chat against the Stickienecro
	///         ice-strike capture. The caller also gates on Target being null.
	///     </para>
	/// </remarks>
	public static class CastGroundTargetScan
	{
		private const byte GroundTargetKind = 0; // tail[0]; 1 = entity-targeted (no reticle)

		/// <summary>
		///     True, with the reticle position, when the tail carries a ground target: kind byte 0 and a non-zero position.
		///     False for an entity-targeted or self cast - a short tail, kind 1, or a zeroed position.
		/// </summary>
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
