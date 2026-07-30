using System;
using System.Numerics;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Progression;
using Xunit;

namespace Faolan.Tests
{
	// Knockback (#496). The wire form is capture-backed and gated byte-exact; the gameplay around it is one
	// measurement, and these tests are careful to assert only what each half actually supports.
	public class KnockbackTests
	{
		// The ONE knockback in the corpus: NecroL80_mage-necromancer_login_gameplay_chat-annotated, 18:49:54.284, the
		// packet the player's own `knockbacked` annotation names. A 69-byte MoveTaskIIR addressed to the player -
		// 1 of only 11 s2c MoveTasks in 1,219 that name the player as SUBJECT, and the only long form among them.
		//
		// from (298.83, 95.40, 337.74) -> to (300.71, 95.17, 335.40): 3.01 units. The `to` is exactly the position the
		// SetPosIIR pins 1.7 s later, and SetPosIIR occurs twice in the entire capture (world entry, and here).
		[Fact]
		public void ForcedMove_IsByteIdenticalToTheCapturedKnockback()
		{
			var captured = Convert.FromHexString(
				"96c467400000c3500802e5d40000" + // subop, class, the player's handle, u16 0
				"439569db42becc9243a8de22" + // from
				"0500" + // mode 0x05 (forced move) + its trailing byte
				"3f8000003f80000000000000" + // the three undecoded floats: 1.0, 1.0, 0.0
				"439569db42becc9243a8de22" + // anchor (== from in this sample)
				"43965a5b42be571c43a7b2c6" + // to
				"00");

			var built = MoveTaskCodec.BuildForcedMoveBody(0x0802E5D4,
				new Vector3(298.826996f, 95.3995514f, 337.735413f),
				new Vector3(300.705902f, 95.1701355f, 335.396667f));

			Assert.Equal(captured, built);
		}

		// The mode byte is what distinguishes this from every other MoveTask form, so it is pinned explicitly rather
		// than only implicitly through the golden above.
		[Fact]
		public void ForcedMove_UsesModeFive()
		{
			var body = MoveTaskCodec.BuildForcedMoveBody(1, Vector3.Zero, Vector3.One);
			Assert.Equal(MoveTaskCodec.ModeForcedMove, body[26]);
			Assert.Equal(0x05, body[26]);
			Assert.Equal(65, body.Length); // 69-byte frame once the length prefix is added
		}

		// `anchor` equals `from` in the one sample, so the default must reproduce that - but the parameter stays
		// separate because nothing tests whether they can differ.
		[Fact]
		public void ForcedMove_AnchorDefaultsToFrom()
		{
			var from = new Vector3(10f, 20f, 30f);
			var to = new Vector3(13f, 20f, 30f);
			Assert.Equal(MoveTaskCodec.BuildForcedMoveBody(1, from, to), MoveTaskCodec.BuildForcedMoveBody(1, from, to, from));
			Assert.NotEqual(MoveTaskCodec.BuildForcedMoveBody(1, from, to), MoveTaskCodec.BuildForcedMoveBody(1, from, to, Vector3.Zero));
		}

		// The push is directly away from the source, and lands at the requested distance.
		[Fact]
		public void Resolve_PushesDirectlyAwayFromTheSource()
		{
			var source = new Vector3(0f, 0f, 0f);
			var target = new Vector3(10f, 0f, 0f);

			var to = Knockback.Resolve(source, target, 3f);

			Assert.Equal(13f, to.X, 3);
			Assert.Equal(0f, to.Z, 3);
			Assert.Equal(3f, Vector3.Distance(target, to), 3);
		}

		// Horizontal only: an attacker standing well above or below must not launch the victim vertically, which is
		// what dropping the Y before normalising buys. The caller ground-snaps afterwards.
		[Fact]
		public void Resolve_IsHorizontal_EvenWhenTheSourceIsAboveOrBelow()
		{
			var target = new Vector3(10f, 50f, 0f);

			foreach (var sourceY in new[] { -400f, 0f, 400f })
			{
				var to = Knockback.Resolve(new Vector3(0f, sourceY, 0f), target, 3f);
				Assert.Equal(target.Y, to.Y, 3); // Y untouched
				Assert.Equal(13f, to.X, 3); // full distance still travelled horizontally
			}
		}

		// Degenerate geometry: attacker and victim on the same spot. There is no "away", so nothing happens - better
		// than picking an arbitrary direction and teleporting the victim somewhere surprising.
		[Fact]
		public void Resolve_OnTheSameSpot_DoesNothing()
		{
			var p = new Vector3(5f, 1f, 5f);
			Assert.Equal(p, Knockback.Resolve(p, p));
		}

		// Resistance reduces the distance monotonically and never inverts it or overshoots. The CURVE is invented
		// (see Knockback's remarks) so this asserts the properties that must hold rather than specific values.
		[Fact]
		public void Resistance_ReducesTheDistance_MonotonicallyAndNeverInverts()
		{
			Assert.Equal(1f, Knockback.ResistanceScale(0), 4); // no resistance stat = full distance

			var previous = 1f;
			foreach (var resistance in new uint[] { 1, 100, 500, 1000, 5000, 100000 })
			{
				var scale = Knockback.ResistanceScale(resistance);
				Assert.InRange(scale, 0f, 1f);
				Assert.True(scale <= previous, $"scale must not grow with resistance: {resistance} gave {scale} after {previous}");
				previous = scale;
			}

			// The documented half-way point, so a change to the divisor is a deliberate edit rather than a silent drift.
			Assert.Equal(0.5f, Knockback.ResistanceScale((uint)Knockback.ResistanceDivisor), 4);
		}

		// A resisted push still moves the victim TOWARDS the same side - resistance shortens, it never reverses.
		[Fact]
		public void Resistance_ShortensButKeepsTheDirection()
		{
			var source = Vector3.Zero;
			var target = new Vector3(10f, 0f, 0f);

			var full = Knockback.Resolve(source, target, 3f);
			var resisted = Knockback.Resolve(source, target, 3f, (uint)Knockback.ResistanceDivisor);

			Assert.True(resisted.X > target.X, "still pushed away from the source");
			Assert.True(resisted.X < full.X, "but not as far");
			Assert.Equal(1.5f, Vector3.Distance(target, resisted), 3);
		}

		// The immunity spell retail applies alongside the displacement. Pinned by id because it is the mechanic naming
		// itself - the DB calls 4784832 "Knockback immunity" - and it is the strongest evidence in that window.
		[Fact]
		public void ImmunitySpell_IsTheCapturedId()
		{
			Assert.Equal(4784832u, Knockback.ImmunitySpellId);
		}
	}
}
