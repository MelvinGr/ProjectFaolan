using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.PlayerAgent
{
	// OverrideGameTweaks (PlayerInterface 0x1A07) - the server's "dynamic game tweaks", which the client applies on
	// login ("ApplyDynamicGameTweaks: Applying 19 gametweaks"). Reverse-engineered from the retail capture
	// (logs-test/retail pkt#3); see docs/protocol/02-rpc.md.
	//
	// Wire format of the `overrides` collection (raw stream, NOT protobuf; every field big-endian):
	//   [u32 tableId][ per tweak: [u16 nameLen][name ASCII][f64 value] ] *
	//
	// The tweak NAMES are plaintext on the wire, length-prefixed - not name hashes - and map 1:1 onto the client's
	// RDBGameTweaks DataTable keys. Every value is an 8-byte double regardless of the tweak's logical type
	// (bool/int/float). The collection carries NO count: the client reads to end-of-stream, which is why the record
	// has to be exact - a single stray byte makes the client log "stream bad after 'overrides'" and drop the whole
	// PlayerInterface route (it applied 0 of 19 tweaks; fixed in 40d4bc0). Byte-exact in GameTweaksTests.
	//
	// Moved in from `PlayerAgentListener`'s inline stream build in #530 wave 7 - the listener keeps the send and the
	// 13-byte session header-extra this method rides with.
	public static class GameTweaksCodec
	{
		public const ushort Opcode = (ushort)PlayerInterfaceOpcodes.OverrideGameTweaks;

		// The 4-byte prefix retail sends, replayed verbatim: a tweak-table id/version. NOT the entry count - the
		// client infers that by reading to the end of the stream.
		public const uint RetailTableId = 0x00004ED4;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(GameTweaks value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static GameTweaks Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(GameTweaksCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static GameTweaks Read(ref SpanReader r)
		{
			var tableId = r.ReadU32Be();

			var tweaks = new List<GameTweak>();
			while (!r.AtEnd)
				tweaks.Add(new GameTweak(r.ReadStringU16Length(), r.ReadF64Be()));

			return new GameTweaks(tableId, tweaks);
		}

		internal static void Write(FaolanStream s, GameTweaks v)
		{
			s.WriteUInt32(v.TableId);
			foreach (var tweak in v.Tweaks)
				s.WriteString(tweak.Name).WriteDouble(tweak.Value);
		}
	}

	// The OverrideGameTweaks payload.
	public sealed record GameTweaks(uint TableId, IReadOnlyList<GameTweak> Tweaks);

	// One tweak: an RDBGameTweaks key and its value, always encoded as a double.
	public readonly record struct GameTweak(string Name, double Value);

	// The tweak SETS we serve, as VALUE factories - content, not encoding. The list below is byte-for-byte what retail
	// sends, in wire order (the client sorts nothing; it applies them as they arrive).
	public static class GameTweakSets
	{
		public static GameTweaks Defaults()
		{
			return new GameTweaks(GameTweaksCodec.RetailTableId, [
				new GameTweak("AAPointsPerLevel", 1.0),
				new GameTweak("AlternateAdvancement_GenericTokensAwarded", 1.0),
				new GameTweak("AlternateAdvancement_SpecificTokensAwarded", 3.0),
				new GameTweak("Anniversary2020", 0.0),
				new GameTweak("Anniversary2020_CanGetArmor", 0.0),
				new GameTweak("Anniversary_5_SpawnBosses", 0.0),
				new GameTweak("CurrentSeason", 3.0),
				new GameTweak("DoubleRaidFinderTokens", 0.0),
				new GameTweak("EventAnniversary_2014", 0.0),
				new GameTweak("EventDoubleXP", 0.0),
				new GameTweak("EventHalloween", 1.0),
				new GameTweak("ExpandInventories_MaxBankSlots", 300.0),
				new GameTweak("IsPvPServer", 0.0),
				new GameTweak("PvEXPFromPvPMultiplier", 0.1),
				new GameTweak("RaidFinderEventChest", 0.0),
				new GameTweak("WorldBoss_SpawnDay_Boss_11", 0.0),
				new GameTweak("WorldBoss_SpawnDay_Boss_4", 0.0),
				new GameTweak("WorldBoss_SpawnWeek_Boss_11", 0.0),
				new GameTweak("WorldBoss_SpawnWeek_Boss_3", 3.0)
			]);
		}
	}
}
