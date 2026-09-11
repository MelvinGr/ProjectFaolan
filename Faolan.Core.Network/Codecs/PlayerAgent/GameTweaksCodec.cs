using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.PlayerAgent
{
	/// <summary>
	///     <c>OverrideGameTweaks</c> - the server's dynamic game tweaks, which the client applies on login
	///     ("ApplyDynamicGameTweaks: Applying 19 gametweaks"). See docs/protocol/02-rpc.md.
	/// </summary>
	/// <remarks>
	///     The <c>overrides</c> collection is a raw stream rather than protobuf, every field big-endian:
	///     <code>
	///     [u32 tableId][ per tweak: [u16 nameLen][name ASCII][f64 value] ] *
	///     </code>
	///     <para>
	///         The tweak names are plaintext on the wire, length-prefixed rather than hashed, and map 1:1 onto the
	///         client's RDBGameTweaks DataTable keys. Every value is an 8-byte double regardless of the tweak's logical
	///         type. The collection carries no count: the client reads to end-of-stream, so the record has to be exact -
	///         a single stray byte makes the client log "stream bad after 'overrides'" and drop the whole PlayerInterface
	///         route. Byte-exact in <c>GameTweaksTests</c>.
	///     </para>
	/// </remarks>
	public static class GameTweaksCodec
	{
		// The message identity. On PlayerAgent/PlayerInterface that is the method constant rather than an opcode: the
		// 16-bit view keeps 7 of its 32 bits and names two methods at once, so there is no opcode enum to declare against.
		public const uint Method = (uint)PlayerInterfaceMethods.OverrideGameTweaks;

		// The 4-byte prefix retail sends, replayed verbatim: a tweak-table id or version. It is not the entry count - the
		// client infers that by reading to the end of the stream.
		public const uint RetailTableId = 0x00004ED4;

		// Canonical: the only method here that writes bytes.
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

	/// <summary>The OverrideGameTweaks payload.</summary>
	public sealed record GameTweaks(uint TableId, IReadOnlyList<GameTweak> Tweaks);

	/// <summary>One tweak: an RDBGameTweaks key and its value, always encoded as a double.</summary>
	public readonly record struct GameTweak(string Name, double Value);

	/// <summary>
	///     The tweak sets the server serves, as value factories - content rather than encoding. The list below is
	///     byte-for-byte what retail sends, in wire order; the client applies them as they arrive.
	/// </summary>
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
