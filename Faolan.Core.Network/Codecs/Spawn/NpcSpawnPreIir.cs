using System.Collections.Generic;
using System.Text;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// Typed, mutable view of the NPC FA015807 (SimpleCharFullUpdate) PRE-IIR scaffold - the "last un-lifted part"
	// of the from-DB NPC spawn (Gitea #185). The scaffold is the fixed read sequence
	// SimpleCharFullUpdateIIR_t::ReadSubClass (FUN_01121c00) runs after the transform and before the embedded
	// CoreCharacterFullUpdateIIR: a u8 version (0x05), then 6 u8 + 53 u32 interleaved with one count-framed
	// sublist (docs/re/fa015807-spawn-reader.md, recovered from Ghidra and validated over 4558 captured records).
	//
	// The 53 u32 are read in WIRE order but scattered into NON-CONTIGUOUS C++ struct fields 0x184..0x264; the
	// inline char buffer over that region holds the NPC's RDB script-binding diagnostic string (ScriptGroup /
	// Script / Command names + <font color=...> markup), null-padded. So the block carries NO session-volatile
	// instance handles - the only stale reference in an NPC spawn is the playfield token in the transform
	// (SpawnPlayfieldRewriter), which is why a carried-over scaffold re-addresses cleanly once the token is fixed.
	//
	// The per-spawn sublist is EMPTY on 11,394 of the 11,400 captured NPC spawns, which is why it was held here as
	// the raw 4-byte `0x3F1` framing word and why the codec used to REJECT any spawn whose list was not empty. Six
	// captured spawns (all of one NPC, Viriden) carry a one-entry list - the same key-0/flag-0/unnamed entry a fresh
	// player carries - and those six could not be decoded at all. It is the same list the player frame models, so it
	// is typed and shared now: SpawnPreFrameSublist.
	public sealed class NpcSpawnPreIir
	{
		public const byte ExpectedVersion = 0x05;
		// How many of the scaffold's fields are single BYTES (the other 53 are u32). Named for what it counts:
		// it used to be `ByteCount`, which reads as a width and collided with the scaffold's actual width.
		public const int FlagCount = 6;

		// The wire index of the one word that is reliably non-zero on a freshly-spawned NPC. The cross-capture
		// sweep (tools/capture/classify_fa015807_fields.py over 5026 scaffolds) showed every other word is per-instance
		// runtime state (target/owner handles, timers, action counters) that is 0 on an idle fresh spawn, and that
		// the most-minimal captured scaffolds carry exactly ONE non-zero word here. Its struct offset is 0x1fc.
		public const int CategoryWordIndex = 17;

		// Scaffold size on the wire for the EMPTY-sublist form: u8 version + 6 u8 + 53 u32 + the sublist's own
		// framing word. 1 + 6 + 53*4 + 4 = 223. A scaffold whose sublist has entries is wider - use ByteCount.
		public const int FixedLength = 1 + FlagCount + WordCount * 4 + 4;
		public const int WordCount = 53;

		// The modal value of the category word across the corpus (111 = 0x6f, ~49% of all NPCs; it is a 5-state
		// per-instance enum {3, 0x6f, 0x78, 0xc9, 0x12c}, NOT identity-bound). The from-scratch minimal scaffold
		// defaults to it - byte-identical to the most-minimal captured NPCs (e.g. Kier-Dheed's Panther).
		public const uint DefaultCategory = 0x6f;

		internal static readonly int[] WordStructOffsets =
		[
			0x184, 0x188, 0x18c, 0x190, 0x194, 0x198, 0x19c, 0x1a8, 0x1b0, 0x1c0, 0x1c8, 0x1d0, 0x1d8, 0x1e0, 0x1e8, 0x1f0, 0x1b8, 0x1fc, 0x200, 0x204, 0x208, 0x210, 0x214, 0x218, 0x21c, 0x220,
			0x1a0, 0x1a4, 0x1ac, 0x1b4, 0x1c4, 0x1cc, 0x1d4, 0x1dc, 0x1e4, 0x1ec, 0x1f4, 0x1bc, 0x224, 0x230, 0x228, 0x234, 0x238, 0x23c, 0x240, 0x244, 0x248, 0x24c, 0x254, 0x258, 0x25c, 0x260,
			0x264
		];

		private static readonly bool[] IsByteField =
		[
			true, // wi0
			false, false, false, false, false, false, false, false, // wi1..8
			false, false, false, false, false, false, false, false, // wi9..16
			false, // wi17
			true, true, true, // wi18..20
			false, false, false, false, // wi21..24
			true, true, // wi25..26
			false, false, false, false, false // wi27..31
			// SUBLIST here (wi32)
			// + 27 u32 (wi33..59) appended below in BuildLayout
		];

		internal static readonly (bool IsByte, bool IsSublist)[] Layout = BuildLayout();

		// The full wire layout: the 32 pre-sublist fields (IsByteField), the sublist, then 27 trailing u32.
		// Entry == (isByte, isSublist).

		// The C++ struct offset each of the 53 u32 is stored to, in WIRE order. Only used to reassemble the
		// script-binding char buffer (DecodeScriptText); irrelevant to byte-exact wire round-trip.

		// The wire read order (after the version byte): true == u8 field, false == u32 field. The single sublist is
		// held out of this table - it sits between the 32nd and 33rd entry and is variable-width.

		// version byte (0x05; client rejects any other value).
		public byte Version { get; set; } = ExpectedVersion;

		// The 6 single-byte fields, in wire order (read positions 0, 18, 19, 20, 25, 26).
		public byte[] Flags { get; set; } = new byte[FlagCount];

		// The per-spawn sublist, shared with the player frame (SpawnPreFrameSublist). Empty on all but six captured
		// spawns, which is exactly why it used to be four bytes of "the 0x3F1 constant".
		public List<SpawnPreFrameSublistEntry> Sublist { get; } = [];

		// The scaffold's width on the wire: the fixed grid plus whatever the sublist costs.
		public int ByteCount => FixedLength - 4 + SpawnPreFrameSublist.ByteCount(Sublist);

		// The 53 u32 fields, in wire order, each held as the big-endian value read off the wire (the value the
		// client byteswaps into its struct). Re-emitted big-endian, so the round-trip is byte-exact regardless of
		// what the words mean. DecodeScriptText re-interprets them as the little-endian char buffer.
		public uint[] Words { get; set; } = new uint[WordCount];

		// Reassembles the 53 u32 in C++ struct-offset order, little-endian (the stream byteswaps on read, so the
		// wire is LE), into the contiguous inline char buffer, and returns its printable runs joined by ' | '.
		// This is the NPC's script-binding diagnostic string - a per-instance text view, for diagnostics/tests.
		public string DecodeScriptText()
		{
			const int baseOffset = 0x184;
			var span = WordStructOffsets[^1] - baseOffset + 4; // 0x264 - 0x184 + 4 = 228
			var buffer = new byte[span];
			for (var i = 0; i < WordCount; i++)
			{
				// Word is stored big-endian; the struct char buffer holds the bytes little-endian (reversed).
				var at = WordStructOffsets[i] - baseOffset;
				buffer[at + 0] = (byte)(Words[i] & 0xFF);
				buffer[at + 1] = (byte)((Words[i] >> 8) & 0xFF);
				buffer[at + 2] = (byte)((Words[i] >> 16) & 0xFF);
				buffer[at + 3] = (byte)((Words[i] >> 24) & 0xFF);
			}

			var sb = new StringBuilder();
			var run = new StringBuilder();
			foreach (var c in buffer)
			{
				if (c is >= 0x20 and < 0x7F)
					run.Append((char)c);
				else
				{
					if (run.Length >= 6)
					{
						sb
							.Append(sb.Length > 0
								? " | "
								: "")
							.Append(run);
					}

					run.Clear();
				}
			}

			if (run.Length >= 6)
			{
				sb
					.Append(sb.Length > 0
						? " | "
						: "")
					.Append(run);
			}

			return sb.ToString();
		}

		// The from-scratch pre-IIR scaffold for a freshly-spawned NPC: version 0x05, all flags 0, all 53 words 0
		// except the category word (defaulted to 0x6f), and the empty 0x3F1 sublist. This is the "last un-lifted
		// part" of the from-DB NPC spawn (Gitea #185), sourced from NOTHING - the sweep proved the scaffold carries
		// no per-NPC config and no session-volatile reference, only per-instance runtime state that is 0 on a fresh
		// spawn (so there is nothing to lift from the captured Data blob, nor to re-address). Encode() of this is
		// byte-identical to the most-minimal captured NPC scaffolds (NpcSpawnPreIirTests.PreIir_BuildMinimal_*).
		public static NpcSpawnPreIir BuildMinimal(uint category = DefaultCategory)
		{
			var scaffold = new NpcSpawnPreIir
			{
				Words =
				{
					[CategoryWordIndex] = category
				}
			};
			return scaffold;
		}

		private static (bool, bool)[] BuildLayout()
		{
			var layout = new (bool, bool)[FlagCount + WordCount + 1]; // 6 u8 + 53 u32 + 1 sublist = 60 entries
			var k = 0;
			foreach (var isByte in IsByteField)
				layout[k++] = (isByte, false);
			layout[k++] = (false, true); // the sublist (wi32)
			for (var i = 0; i < 27; i++) // the 27 trailing u32 (wi33..59)
				layout[k++] = (false, false);
			return layout;
		}
	}
}
