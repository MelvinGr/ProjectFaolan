using System;
using System.Buffers.Binary;
using System.Numerics;

namespace Faolan.Core.Network.Codecs.Combat
{
	// Parses the ground-target position out of a CastSpell (BehaviorControlCenter 0x642CD3D6, ActionType 4) trailer.
	// A ground-targeted AoE (e.g. Deathly Blizzard) is aimed at a RETICLE POSITION rather than a locked entity, so the
	// cast carries no SpellTarget handle - instead the trailer after the spell id is
	//   [u8 kind=0][f32 X][f32 Y][f32 Z][u32 0][u32 0][u32 0x41A][u8 1]   (raw-body floats = big-endian)
	// An ENTITY-targeted cast (e.g. Ice Strike) sends kind=1 and an UNUSED position (the target rides
	// SpellTarget.TargetHandle instead - its trailer Z is a garbage denormal, so "position != 0" alone is NOT a safe
	// discriminator; the leading kind byte is). Decoded from NecroL80_..._actions-described-in-chat (the "Casting Deathly
	// Blizzard ... (AOE)" casts) vs Stickienecro ice-strike (#236). The caller also gates on SpellTarget == null.
	public static class CastGroundTarget
	{
		private const byte GroundTargetKind = 0; // trailer[0]; 1 = entity-targeted (no reticle)
		private const int MinLength = PositionOffset + 3 * sizeof(float);

		// The three floats start one byte into the trailer (after the kind byte).
		private const int PositionOffset = 1;

		// True (with the reticle position) when the trailer carries a ground target (kind byte 0 + a non-zero
		// position); false for an entity-targeted / self cast (short trailer, kind 1, or a zeroed position).
		public static bool TryParse(byte[] trailer, out Vector3 position)
		{
			position = default;
			if (trailer == null || trailer.Length < MinLength || trailer[0] != GroundTargetKind)
				return false;

			var span = trailer.AsSpan(PositionOffset);
			position = new Vector3(BinaryPrimitives.ReadSingleBigEndian(span), BinaryPrimitives.ReadSingleBigEndian(span[sizeof(float)..]),
				BinaryPrimitives.ReadSingleBigEndian(span[(2 * sizeof(float))..]));

			return position != Vector3.Zero;
		}
	}
}
