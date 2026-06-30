#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Faolan.GameServer.Packets.FullCharSnapshot;
using Xunit;

namespace Faolan.Tests
{
	// #214 - the DEAD-STATE field in the 0x737A6DF9 world-entry FullPlayerChar snapshot.
	//
	// The in-client release/resurrect window is gated on a client-side local-player dead-flag
	// (DAT_0297d374->vtable[0x10]()). Live gdb proved our death PACKETS are byte-correct yet the flag stays
	// false because our world-entry never sets the dead-state. The retail `login-while-dead` capture carries
	// the dead state with NO SimpleCharKilledBy / NO ApplySpell - so the dead-state lives in the SNAPSHOT.
	//
	// Decoded (Captures/scripts/decode_death_snapshot.py): the dead-state is the tails stat block's
	// current-health stat, statId 0x001B (LegacyCharacterStatId.HealthCurrent) = 0 (while HealthMax stays full).
	// That maps 1:1 onto TailsPostAccessBlock.CurrentHp (Values[23]/[54], +[202]). These tests lock both halves:
	// the captured dead snapshot really has HealthCurrent==0, and the builder can produce that dead shape.
	public class DeathSnapshotTests
	{
		private const string CapturesDir = "/home/melvin/Development/C#/Project Faolan/Captures";
		private const uint HealthCurrentStatId = 0x001B;
		private const uint HealthMaxStatId = 0x0001;

		[Fact]
		public void DeadLoginSnapshot_HasHealthCurrentZero_WhileMaxStaysFull()
		{
			var pairs = StatBlockPairs("retail_login-while-dead_resurrect-window.tshark.json");

			Assert.True(pairs.TryGetValue(HealthCurrentStatId, out var current), "HealthCurrent (0x1B) present");
			Assert.True(pairs.TryGetValue(HealthMaxStatId, out var max), "HealthMax (0x01) present");

			Assert.Equal(0u, current);   // dead: current health is exactly 0
			Assert.True(max > 0u, "max-health side stays populated when dead");
		}

		[Fact]
		public void AliveLoginSnapshots_AllHaveNonZeroHealthCurrent()
		{
			// Every normal login snapshot carries full current health - the dead==0 signal is unambiguous.
			foreach (var capture in new[]
			         {
				         "Strie_rogue-assassin_login.tshark.json",
				         "Stihetfv_soldier-conqueror_login.tshark.json",
				         "Fwfnwfjwk_soldier-guardian_login.tshark.json",
				         "Faolanx_mage-necromancer_login.tshark.json",
			         })
			{
				var pairs = StatBlockPairs(capture);
				Assert.True(pairs.TryGetValue(HealthCurrentStatId, out var current), $"{capture}: HealthCurrent present");
				Assert.True(current > 0u, $"{capture}: alive snapshot has non-zero HealthCurrent (got {current})");
			}
		}

		[Fact]
		public void PostAccessBlock_PatchedDead_ZeroesCurrentHpSlotsButKeepsMax()
		{
			// The builder-side of the fix: applying class vitals (full HP) then marking dead (CurrentHp = 0) must
			// zero the current-health slots [23]/[54]/[202] while leaving the base/max slots [21]/[56]/[204] full -
			// exactly the shape the retail dead snapshot carries.
			const uint hp = 196, hpBase = 196;
			var block = TailsStatBlock.CreateFreshBlock();
			block.ApplyClassVitals(hp, hpBase, mana: 0, manaBase: 0, stamina: 217, staminaBase: 197);

			Assert.Equal(hp, block.CurrentHp); // sanity: alive shape first

			block.CurrentHp = 0;               // the dead patch

			Assert.Equal(0u, block.Values[23]);                  // CurrentHpExtra
			Assert.Equal(0u, block.Values[54]);                  // CurrentHp (repeated-block slot)
			Assert.Equal(0u, block.Values[54 + 148]);            // [202] copy
			Assert.Equal(hpBase * 100, block.Values[21]);        // base/max-side stays full
			Assert.Equal(hpBase * 100, block.Values[56]);        // [56] (+[204])
			Assert.Equal(hpBase * 100, block.Values[56 + 148]);  // [204] copy
		}

		// --- helpers: read the tails stat block ([statId,value] pairs) straight from a tshark capture ---

		private static Dictionary<uint, uint> StatBlockPairs(string captureFileName)
		{
			var body = SnapshotBody(Path.Combine(CapturesDir, captureFileName));
			Assert.NotNull(body);

			// Stat-block frame: two empty 0x3F1 StatList counts then the 0x46F2 block-type word.
			ReadOnlySpan<byte> marker = [0x00, 0x00, 0x03, 0xF1, 0x00, 0x00, 0x03, 0xF1, 0x00, 0x00, 0x46, 0xF2];
			var idx = IndexOf(body, marker);
			Assert.True(idx >= 0, $"{captureFileName}: stat-block marker found");

			var pairs = new Dictionary<uint, uint>();
			for (var o = idx + marker.Length; o + 8 <= body!.Length; o += 8)
			{
				var statId = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(o, 4));
				var value = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(o + 4, 4));
				pairs.TryAdd(statId, value); // first id wins (mirrors the client's map insert)
			}

			return pairs;
		}

		private static byte[]? SnapshotBody(string path)
		{
			if (!File.Exists(path))
				return null;

			using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
			if (!doc.RootElement.TryGetProperty("Packets", out var packets))
				return null;

			foreach (var p in packets.EnumerateArray())
			{
				if (!p.TryGetProperty("BodyHex", out var bh))
					continue;
				var hex = bh.GetString() ?? "";
				if (hex.Length >= 16 && hex.Substring(8, 8).Equals("737a6df9", StringComparison.OrdinalIgnoreCase))
					return Convert.FromHexString(hex);
			}

			return null;
		}

		private static int IndexOf(byte[]? haystack, ReadOnlySpan<byte> needle)
		{
			if (haystack == null)
				return -1;
			for (var i = 0; i + needle.Length <= haystack.Length; i++)
				if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
					return i;
			return -1;
		}
	}
}
