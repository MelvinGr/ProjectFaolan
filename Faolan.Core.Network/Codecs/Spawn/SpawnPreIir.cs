using System.Collections.Generic;
using System.Text;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// The FA015807 (SimpleCharFullUpdate) PRE-IIR scaffold - the fixed read sequence
	// SimpleCharFullUpdateIIR_t::ReadSubClass (FUN_01121c00) runs after the transform and before the embedded
	// CoreCharacterFullUpdateIIR: a u8 version (0x05), then 6 u8 + 53 u32 interleaved with one count-framed sublist
	// (docs/re/fa015807-spawn-reader.md, recovered from Ghidra and validated over 4558 captured records).
	//
	// ONE type for BOTH families (#530 wave 5b). It used to be two - `NpcSpawnPreIir` and `PlayerSpawnPreFrame` -
	// that walked the SAME layout table, because it is the same client reader; the only apparent difference was that
	// the player's model also carried the 5 bytes of the embedded CoreCharacter header that its parse happened to cut
	// into. Those two fields belong to the message, not to the scaffold, and now live on SimpleCharFullUpdateIIR.
	//
	// The 53 u32 are read in WIRE order but scattered into NON-CONTIGUOUS C++ struct fields 0x184..0x264, and the
	// client applies each to the dynel as a STAT (WordStatIds). On an NPC the server fills only THREE of the 53
	// (w17, w18, w38) and the rest goes out carrying unrelated server-side data - script-engine diagnostics, LDB
	// <localized> fragments - which is why an NPC's block reads as text and why 591 captured Pillagers carry 551
	// distinct blocks (#603). So the block carries NO session-volatile instance handles either way: the only stale
	// reference in a spawn is the playfield token in the transform (SpawnTransformPatch), which is why a
	// carried-over scaffold re-addresses cleanly once the token is fixed.
	//
	// The per-spawn sublist is EMPTY on 11,394 of the 11,400 captured NPC spawns, which is why it was long held as
	// the raw 4-byte `0x3F1` framing word; on a PLAYER it is the character's saved FEAT SPECS - named, with a
	// trained-feat list each (#466 / #474), which is what put a name to the whole structure.
	public sealed record SpawnPreIir
	{
		public const byte ExpectedVersion = 0x05;

		// How many of the scaffold's fields are single BYTES (the other 53 are u32). Named for what it counts: it
		// used to be `ByteCount`, which reads as a width and collided with the scaffold's actual width.
		public const int FlagCount = 6;

		public const int WordCount = 53;

		// Scaffold size on the wire for the EMPTY-sublist form: u8 version + 6 u8 + 53 u32 + the sublist's own
		// framing word. 1 + 6 + 53*4 + 4 = 223. A scaffold whose sublist has entries is wider - use ByteCount.
		public const int FixedLength = 1 + FlagCount + WordCount * 4 + 4;

		// The wire index of the one word the retail server reliably WRITES on an NPC spawn - one of the three
		// (w17, w18, w38) the leaked-text sweep found it never leaves alone, and the only one of the three that is
		// non-zero (#603). The most-minimal captured scaffolds carry exactly ONE non-zero word, here. Its struct
		// offset is 0x1fc. A captured PLAYER frame carries the same modal value in the same slot.
		public const int CategoryWordIndex = 17;

		// The modal value of the category word across the corpus (111 = 0x6f, ~49% of all NPCs; it is a 5-state
		// per-instance enum {3, 0x6f, 0x78, 0xc9, 0x12c}, NOT identity-bound). The from-scratch minimal scaffold
		// defaults to it - byte-identical to the most-minimal captured NPCs (e.g. Kier-Dheed's Panther).
		public const uint DefaultCategory = 0x6f;

		// Flags index of the frame's archetype byte (Soldier 1 / Priest 2 / Rogue 3 / Mage 4), abs @135 in the spawn
		// body. Applied to the dynel as stat 0x01F `Archetype`.
		public const int ArchetypeFlagIndex = 1;

		// Flags index of the class byte (0x12 Barbarian .. 0x2c Demonologist), abs @136. Applied as stat 0x043
		// `Class` - which is the corroboration for the whole FlagStatIds/WordStatIds mapping below: 0x043 is what
		// #239 named the class stat from an entirely different direction (the in-client character sheet).
		public const int ClassFlagIndex = 2;

		// Flags index of the PRESTIGE class byte, abs @137 - stat 0x12E. Named by the apply, not by a capture: it is
		// 0 in every captured frame, because prestige classes are Rise of the Godslayer content nothing in the corpus
		// had.
		public const int PrestigeClassFlagIndex = 3;

		// The stat id each of the 6 flag bytes is applied to, in flag order. 0 = the byte is not applied as a stat
		// (flag 0 is consumed as a plain field). See WordStatIds for where these come from.
		public static readonly uint[] FlagStatIds = [0, 0x01F, 0x043, 0x12E, 0xF4261, 0xF4260];

		/// <summary>
		///     The dynel STAT ID each of the 53 u32 is applied to, in WIRE order - parallel to
		///     <see cref="WordStructOffsets" />. 0 where the slot is not applied as a stat (the six trailing words,
		///     which carry the sublist's own state, and the two byte-packed words whose individual bytes go to
		///     <see cref="FlagStatIds" /> instead).
		///     <para>
		///         Recovered from <c>SimpleCharFullUpdateIIR_t::Apply</c> (<c>FUN_011215e0</c>, found by
		///         <c>tools/ghidra/DumpScaffoldConsumers.java</c>), which walks the struct region the reader filled and
		///         pushes every slot into the dynel as <c>SetStat(statId, value)</c> - an explicit id per slot, not a
		///         positional copy. So the scaffold is not the opaque per-instance state it was filed as: it is a STAT
		///         BLOCK, and this is its key.
		///     </para>
		///     <para>
		///         What the ids say it holds is the character's PvP / MINIGAME CAREER: the LDB stat table names
		///         `Fatalities` (0x564), `Minigames Abandoned` (0x560), and the `Annihilation` / `Capture the Skull` /
		///         `Siege` win-loss pairs (0x29A..0x29F), sitting beside `Kills` (0x290) and `Deaths` (0x292). A fresh
		///         character has no career yet, which is why a fresh player's block is near-empty.
		///     </para>
		///     <para>
		///         ⚠️ An NPC has no career either, but its block is NOT zero and must not be read as one (#603). The
		///         retail server writes only w17, w18 and w38 on an NPC spawn and sends the other 50 words carrying
		///         data it never wrote for that NPC - script-engine diagnostics, LDB `&lt;localized&gt;` fragments,
		///         binary. 591 captured Pillagers carry 551 distinct blocks. Ours sends zeros
		///         (<see cref="SpawnPreIirs.Minimal" />), which is the shape retail's three written slots describe.
		///     </para>
		///     <para>
		///         Two slots are FLOATS: words 14 (0x546) and 35 (0x545) go out through a different setter on the
		///         stat vtable (+0x1c rather than +0xc), which is the one place the apply distinguishes a type. That
		///         is also what <see cref="ConstantWordIndex" /> is about - a word reading 0x41700000 is 15.0f, and a
		///         float slot is where a float belongs.
		///     </para>
		///     <para>
		///         ⚠️ Most of the 0x545..0x593 run is NOT named by the LDB table (5 of ~80 ids are), so a slot here
		///         carries a stat id we can address but not yet name. Naming them is LDB/in-client work, not more RE.
		///     </para>
		/// </summary>
		public static readonly uint[] WordStatIds =
		[
			0x564, 0x569, 0x29A, 0x56A, 0x29B, 0x570, 0x560, 0x56B, 0x571, 0x572, 0x56D, 0x56E, 0x573, 0x0A5, 0x546, 0, 0x56C, 0xF428F, 0x1E86AE, 0x579, 0x57F, 0x58C, 0x589, 0x584, 0x585, 0x586,
			0x575, 0x29C, 0x29D, 0xF425A, 0xF4259, 0x29E, 0x29F, 0, 0x090, 0x545, 0xF425C, 0xF425B, 0x587, 0x58D, 0x588, 0x58E, 0x58F, 0x590, 0x591, 0x592, 0x593, 0, 0, 0, 0, 0,
			0
		];

		// Word index of a word that is 0x41700000 (float 15.0) in every captured player frame and 0 on NPCs. Named
		// for what it is - a constant we can reproduce but cannot yet attribute.
		public const int ConstantWordIndex = 19;

		// ...and that value. 0x41700000 is the IEEE754 encoding of 15.0f, so the word is very likely a float, but
		// nothing has been found that varies it, so it is reproduced rather than derived.
		public const uint ConstantWordValue = 0x41700000;

		// Word index of a per-SESSION value: it differs between characters and between sessions of the SAME
		// character (most captured frames carry 6..44; the outliers are round - 1005, 1586, 9000, 11185, 14000).
		// Nothing has been found that predicts it, so the generated frame reproduces the value the captured fresh
		// character carried, which the client accepts.
		public const int SessionWordIndex = 21;

		// ...that value.
		public const uint FreshSessionWordValue = 8;

		// Word index of the vanity SHOWN-slots bitmask (4 bytes big-endian, bit = base equip slot id, SET = that
		// slot's vanity piece is shown; #336). Frame offset 18 - the offset #336 found independently, which is
		// corroboration that this u32 grid is the client's and not a coincidence.
		public const int VanityShownMaskWordIndex = 4;

		// The C++ struct offset each of the 53 u32 is stored to, in WIRE order. Only used to reassemble the
		// leaked-server-text view (DecodeScriptText); irrelevant to byte-exact wire round-trip.
		internal static readonly int[] WordStructOffsets =
		[
			0x184, 0x188, 0x18c, 0x190, 0x194, 0x198, 0x19c, 0x1a8, 0x1b0, 0x1c0, 0x1c8, 0x1d0, 0x1d8, 0x1e0, 0x1e8, 0x1f0, 0x1b8, 0x1fc, 0x200, 0x204, 0x208, 0x210, 0x214, 0x218, 0x21c, 0x220,
			0x1a0, 0x1a4, 0x1ac, 0x1b4, 0x1c4, 0x1cc, 0x1d4, 0x1dc, 0x1e4, 0x1ec, 0x1f4, 0x1bc, 0x224, 0x230, 0x228, 0x234, 0x238, 0x23c, 0x240, 0x244, 0x248, 0x24c, 0x254, 0x258, 0x25c, 0x260,
			0x264
		];

		// The wire read order (after the version byte): true == u8 field, false == u32 field. The single sublist is
		// held out of this table - it sits between the 32nd and 33rd entry and is variable-width.
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

		// The full wire layout: the 32 pre-sublist fields (IsByteField), the sublist, then 27 trailing u32.
		internal static readonly (bool IsByte, bool IsSublist)[] Layout = BuildLayout();

		// version byte (0x05; client rejects any other value).
		public byte Version { get; set; } = ExpectedVersion;

		// The 6 single-byte fields, in wire order (read positions 0, 18, 19, 20, 25, 26).
		public byte[] Flags { get; set; } = new byte[FlagCount];

		// The count-framed sublist between the 32nd and 33rd scaffold field. On a PLAYER it is the character's saved
		// FEAT SPECS; empty on the create-editor frame and on all but six captured NPCs.
		public List<SpawnPreFrameSublistEntry> Sublist { get; } = [];

		// The 53 u32 fields, in wire order, each held as the big-endian value read off the wire (the value the
		// client byteswaps into its struct). Re-emitted big-endian, so the round-trip is byte-exact regardless of
		// what the words mean. DecodeScriptText re-interprets them as the little-endian char buffer.
		public uint[] Words { get; set; } = new uint[WordCount];

		// The scaffold's width on the wire: the fixed grid plus whatever the sublist costs.
		public int ByteCount => FixedLength - 4 + SpawnPreFrameSublistCodec.ByteCount(Sublist);

		// Reassembles the 53 u32 into the contiguous 228-byte region 0x184..0x267 they are scattered across in the
		// client struct, little-endian (the stream byteswaps on read, so the wire is LE). This is the buffer BOTH
		// readings of the region are taken over: the stat block the apply describes (WordStatIds) and - on an NPC,
		// where retail writes only three of the 53 slots - whatever server-side data the other 50 happen to carry.
		//
		// ⚠️ It is a DIAGNOSTIC view, never a source of content. What it holds on an NPC is a snapshot of the retail
		// server's own heap, not anything about that NPC (#603); `--report scaffold-heap` is the harvest that mines
		// it (Gitea #604).
		public byte[] DecodeScriptBuffer()
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

			return buffer;
		}

		// The printable runs (>= 6 chars) of that buffer, joined by ' | ' - the human-readable form of the same view.
		public string DecodeScriptText()
		{
			var buffer = DecodeScriptBuffer();

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

	// The from-scratch pre-IIR scaffolds. Value factories, not codec surface (#530 §2 rule 1 / the wave-3 precedent).
	public static class SpawnPreIirs
	{
		// The from-scratch pre-IIR scaffold for a freshly-spawned NPC: version 0x05, all flags 0, all 53 words 0
		// except the category word (defaulted to 0x6f), and the empty 0x3F1 sublist. This is the "last un-lifted
		// part" of the from-DB NPC spawn (Gitea #185), sourced from NOTHING - and #603 showed that is the CORRECT
		// shape rather than a safe fallback: over the captured NPC spawns the retail server fills only w17, w18 and
		// w38, and whatever the other 50 words carry is data it never wrote for this NPC. Reproducing a captured
		// block would be reproducing retail's leak. Its encoding is byte-identical to the most-minimal captured
		// NPC scaffolds.
		public static SpawnPreIir Minimal(uint category = SpawnPreIir.DefaultCategory)
		{
			return new SpawnPreIir
			{
				Words =
				{
					[SpawnPreIir.CategoryWordIndex] = category
				}
			};
		}
	}
}
