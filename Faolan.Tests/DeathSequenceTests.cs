using System;
using System.Numerics;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.World;
using Xunit;

namespace Faolan.Tests
{
	// Byte-exact golden for the SimpleCharKilledByUpdateIIR (0xB066AFC2) kill record decoded in #214/#204 - the
	// explicit "this entity died" trigger the client needs to play the death anim / show the release screen.
	// Reproduces the record from AssassinL23_..._resurect (the player-death sequence before the 0x2024 release screen).
	public class DeathSequenceTests
	{
		// The behaviorId (protobuf field 1) is the creature's death animation, harvested per NpcTemplate (#362), and
		// field12 follows it: the animation's own length in centiseconds, INVARIANT across every captured record of
		// three of the four animations (#742). It used to be hardcoded at 240 for all of them, which gave a beast's
		// 1.0 s death animation 2.4x its own timing. field5=8 and field6=0 are the only real constants - 272 of 272.
		[Theory]
		[InlineData(41877u, "0895c702", "60f001")] // humanoid biped (Picts, Nemedians, pirates, ...)  240 on 166/166
		[InlineData(17036u, "088c8501", "609802")] // demon / large caster (Arcanist, Bat Demon, ...)   280, modal 30/52
		[InlineData(215333u, "08a5920d", "6064")] // quadruped / beast (Ape King, Jungle Ape, Panther)  100 on 32/32
		[InlineData(128031u, "089fe807", "60ac02")] // the environmental one - 300 on 2/2, PLAYER-only
		public void BuildDeathBcc_EncodesTheDeathAnimation_AndItsOwnTiming(uint behaviorId, string field1Hex, string field12Hex)
		{
			var body = BehaviorControlCenterCodec.Build(DeathSequence.Bcc(0x00040000, behaviorId, DeathSequence.CapturedNpcDeathCounter));
			var args = $"{field1Hex}18052808300060{field12Hex[2..]}";

			Assert.Equal($"642cd3d60000c3500004000000040000001f{args.Length / 2:x8}{args}", Convert.ToHexString(body).ToLowerInvariant());
		}

		[Fact]
		public void BuildDeathBcc_ReproducesCapturedNpcDeath_ByteExact()
		{
			// The death-behavior BccIIR (0x642CD3D6, behaviorId 41877 = humanoid biped death) retail sends for a dying
			// NPC from NecroL80_..._casting-combat pkt#5162: dying handle 0x0004EFE7, form 0x0004, slot 0x1F, protobuf
			// with field3 = 5 - which is that NPC's BEHAVIOUR COUNTER at the moment it died, not a "death variant".
			// The carrier prepends the u32 length 0x00000023.
			var body = BehaviorControlCenterCodec.Build(DeathSequence.Bcc(0x0004EFE7, DeathSequence.HumanoidDeathBehaviorId, DeathSequence.CapturedNpcDeathCounter));
			Assert.Equal("642cd3d60000c3500004efe700040000001f0000000d0895c70218052808300060f001", Convert.ToHexString(body).ToLowerInvariant());
		}

		// ⚠️ field3 is the dying entity's own behaviour counter (#742): 40 distinct values from 2 to 373 across the
		// 272 captured death records, and +1 against the same actor's previous BCC record on 265 of 267. So it has to
		// be whatever the caller's entity is up to, and a codec that pinned it would be pinning a capture's accident.
		[Theory, InlineData(1u, "1801"), InlineData(7u, "1807"), InlineData(373u, "18f502")]
		public void BuildDeathBcc_CarriesTheEntitysOwnBehaviourCounter(uint counter, string field3Hex)
		{
			var body = BehaviorControlCenterCodec.Build(DeathSequence.Bcc(0x0004EFE7, DeathSequence.HumanoidDeathBehaviorId, counter));

			Assert.Contains($"0895c702{field3Hex}2808", Convert.ToHexString(body).ToLowerInvariant());
		}

		[Fact]
		public void BuildDeathEffect_ReproducesCapturedRecord_ByteExact()
		{
			// The self-applied death-state ApplySpell (0x6B87E316, spell 0x0009F000) from AssassinL23_..._resurect pkt#4051,
			// applyTo == applyFrom == the dying player 0x08006F31 (the carrier prepends the u32 length 0x00000050).
			var body = MagicBuffCodec.Build(DeathSequence.Effect(0x08006F31));
			Assert.Equal("6b87e3160000c35008006f3100000009f0000000c35008006f31" + "00000000000000000002bf2000010000000100000000000009f000000017000000000000000000000000000000170000000014ef177d",
				Convert.ToHexString(body).ToLowerInvariant());
		}

		[Fact]
		public void BuildDeathPose_ReproducesCapturedRecord_ByteExact()
		{
			// The minimal death-pose MoveTask (0x96C46740, movement-type 0x06) from AssassinL23_..._resurect pkt#4048,
			// dying player 0x08006F31 at the captured death position (big-endian floats; carrier prepends u32 len 0x1B).
			var pos = new Vector3(BitConverter.UInt32BitsToSingle(0x445C182F), BitConverter.UInt32BitsToSingle(0x433237B8), BitConverter.UInt32BitsToSingle(0x447E44D5));
			var body = MoveTaskCodec.Build(DeathSequence.Pose(0x08006F31, pos));
			Assert.Equal("96c467400000c35008006f310000445c182f433237b8447e44d506", Convert.ToHexString(body).ToLowerInvariant());
		}

		[Fact]
		public void BuildKilledBy_ReproducesCapturedRecord_ByteExact()
		{
			// The IIR record (the carrier prepends the u32 length): dying handle 0x08006F31, killer endpoint 0x000406F9.
			var body = SimpleCharKilledByUpdateCodec.Build(0x08006F31, 0x000406F9);
			Assert.Equal("b066afc20000c35008006f3100010000c350000406f9", Convert.ToHexString(body).ToLowerInvariant());
		}
	}
}
