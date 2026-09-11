using System.Collections.Generic;
using System.Text;

namespace Faolan.Core.Network.Codecs.Spawn
{
	/// <summary>
	///     The spawn pre-IIR scaffold - the fixed read sequence SimpleCharFullUpdateIIR_t::ReadSubClass (FUN_01121c00)
	///     runs after the transform and before the embedded CoreCharacterFullUpdateIIR: a u8 version, then 6 u8 and 53 u32
	///     interleaved with one count-framed sublist. See docs/re/fa015807-spawn-reader.md, recovered from the client and
	///     validated over 4,558 captured records. One type for both families, because it is one client reader.
	/// </summary>
	/// <remarks>
	///     The 53 u32 are read in wire order but scattered into non-contiguous C++ struct fields 0x184..0x264, and the
	///     client applies each to the dynel as a stat (<see cref="WordStatIds" />). On an NPC the retail server fills only
	///     three of the 53 (w17, w18, w38) and the rest goes out carrying unrelated server-side data - script-engine
	///     diagnostics, LDB fragments - which is why an NPC's block reads as text and why 591 captured Pillagers carry 551
	///     distinct blocks.
	///     <para>
	///         The block carries no session-volatile instance handles either way: the only stale reference in a spawn is the
	///         playfield token in the transform (<see cref="SpawnTransformPatch" />), which is why a carried-over scaffold
	///         re-addresses cleanly once the token is fixed.
	///     </para>
	///     <para>
	///         The per-spawn sublist is empty on 11,394 of the 11,400 captured NPC spawns, which makes its framing word look
	///         like a constant; on a player it is the character's saved feat specs, named, each with its own trained-feat
	///         list.
	///     </para>
	/// </remarks>
	public sealed record SpawnPreIir
	{
		public const byte ExpectedVersion = 0x05;

		/// <summary>How many of the scaffold's fields are single bytes; the other 53 are u32.</summary>
		public const int FlagCount = 6;

		public const int WordCount = 53;

		/// <summary>
		///     The scaffold's size on the wire for the empty-sublist form: the version byte, 6 u8, 53 u32 and the sublist's
		///     own framing word, 223 bytes in all. A scaffold whose sublist has entries is wider - use
		///     <see cref="ByteCount" />.
		/// </summary>
		public const int FixedLength = 1 + FlagCount + WordCount * 4 + 4;

		/// <summary>
		///     The wire index of the one word the retail server reliably writes on an NPC spawn: one of the three (w17, w18,
		///     w38) the leaked-text sweep found it never leaves alone, and the only one of the three that is non-zero. The
		///     most minimal captured scaffolds carry exactly one non-zero word, here, at struct offset 0x1fc, and a captured
		///     player frame carries the same modal value in the same slot.
		/// </summary>
		public const int CategoryWordIndex = 17;

		/// <summary>
		///     The modal value of the category word across the corpus - 111, about 49% of all NPCs. It is a five-state
		///     per-instance enum {3, 0x6f, 0x78, 0xc9, 0x12c} rather than something bound to identity. The from-scratch
		///     minimal scaffold defaults to it, which is byte-identical to the most minimal captured NPCs.
		/// </summary>
		public const uint DefaultCategory = 0x6f;

		/// <summary>
		///     Flags index of the frame's archetype byte - Soldier 1, Priest 2, Rogue 3, Mage 4 - at offset 135 in the spawn
		///     body. Applied to the dynel as stat 0x01F, Archetype.
		/// </summary>
		public const int ArchetypeFlagIndex = 1;

		/// <summary>
		///     Flags index of the class byte (0x12 Barbarian to 0x2c Demonologist), at offset 136. Applied as stat 0x043,
		///     Class - which corroborates the whole <see cref="FlagStatIds" /> and <see cref="WordStatIds" /> mapping, since
		///     0x043 is the class stat named independently from the in-client character sheet.
		/// </summary>
		public const int ClassFlagIndex = 2;

		/// <summary>
		///     Flags index of the prestige class byte, at offset 137 - stat 0x12E. Named by the apply rather than by a
		///     capture: it is 0 in every captured frame, prestige classes being Rise of the Godslayer content nothing in the
		///     corpus had.
		/// </summary>
		public const int PrestigeClassFlagIndex = 3;

		/// <summary>
		///     The stat id each of the 6 flag bytes is applied to, in flag order. Zero means the byte is not applied as a
		///     stat; flag 0 is consumed as a plain field. See <see cref="WordStatIds" /> for where these come from.
		/// </summary>
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
		///         positional copy. So the scaffold is a stat block rather than opaque per-instance state, and this is
		///         its key.
		///     </para>
		///     <para>
		///         What the ids say it holds is the character's PvP and minigame career: the LDB stat table names
		///         Fatalities (0x564), Minigames Abandoned (0x560) and the Annihilation, Capture the Skull and Siege
		///         win-loss pairs (0x29A..0x29F), beside Kills (0x290) and Deaths (0x292). A fresh character has no
		///         career yet, which is why a fresh player's block is near-empty.
		///     </para>
		///     <para>
		///         An NPC has no career either, but its block is not zero and must not be read as one: the retail
		///         server writes only w17, w18 and w38 on an NPC spawn and sends the other 50 words carrying data it
		///         never wrote for that NPC - script-engine diagnostics, LDB fragments, binary - so 591 captured
		///         Pillagers carry 551 distinct blocks. We send zeros (<see cref="SpawnPreIirs.Minimal" />), which is
		///         the shape retail's three written slots describe.
		///     </para>
		///     <para>
		///         Two slots are floats: words 14 (0x546) and 35 (0x545) go out through a different setter on the
		///         stat vtable (+0x1c rather than +0xc), which is the one place the apply distinguishes a type. That
		///         is also what <see cref="ConstantWordIndex" /> is about - a word reading 0x41700000 is 15.0f, and a
		///         float slot is where a float belongs.
		///     </para>
		///     <para>
		///         Most of the 0x545..0x593 run is not named by the LDB table - five of about eighty ids are - so a
		///         slot here carries a stat id that can be addressed but not yet named. Naming them is LDB and
		///         in-client work rather than more reverse engineering.
		///     </para>
		/// </summary>
		public static readonly uint[] WordStatIds =
		[
			0x564, 0x569, 0x29A, 0x56A, 0x29B, 0x570, 0x560, 0x56B, 0x571, 0x572, 0x56D, 0x56E, 0x573, 0x0A5, 0x546, 0, 0x56C, 0xF428F, 0x1E86AE, 0x579, 0x57F, 0x58C, 0x589, 0x584, 0x585, 0x586,
			0x575, 0x29C, 0x29D, 0xF425A, 0xF4259, 0x29E, 0x29F, 0, 0x090, 0x545, 0xF425C, 0xF425B, 0x587, 0x58D, 0x588, 0x58E, 0x58F, 0x590, 0x591, 0x592, 0x593, 0, 0, 0, 0, 0,
			0
		];

		/// <summary>
		///     Word index of a word that is 0x41700000 - the float 15.0 - in every captured player frame and 0 on NPCs. A
		///     constant that can be reproduced but not yet attributed.
		/// </summary>
		public const int ConstantWordIndex = 19;

		/// <summary>
		///     That value. It is the IEEE 754 encoding of 15.0f, so the word is very likely a float, but nothing has been
		///     found that varies it, so it is reproduced rather than derived.
		/// </summary>
		public const uint ConstantWordValue = 0x41700000;

		/// <summary>
		///     Word index of a per-session value: it differs between characters and between sessions of the same character,
		///     most captured frames carrying 6 to 44 with round outliers (1005, 1586, 9000, 11185, 14000). Nothing has been
		///     found that predicts it, so the generated frame reproduces the value a captured fresh character carried, which
		///     the client accepts.
		/// </summary>
		public const int SessionWordIndex = 21;

		/// <summary>That value.</summary>
		public const uint FreshSessionWordValue = 8;

		/// <summary>
		///     Word index of the vanity shown-slots bitmask: four bytes big-endian, each bit a base equip slot id, set
		///     meaning that slot's vanity piece is shown. Its frame offset was found independently of this grid, which
		///     corroborates that the grid is the client's.
		/// </summary>
		public const int VanityShownMaskWordIndex = 4;

		/// <summary>
		///     The C++ struct offset each of the 53 u32 is stored to, in wire order. Only used to reassemble the
		///     server-text view (<see cref="DecodeScriptText" />); irrelevant to the byte-exact wire round-trip.
		/// </summary>
		internal static readonly int[] WordStructOffsets =
		[
			0x184, 0x188, 0x18c, 0x190, 0x194, 0x198, 0x19c, 0x1a8, 0x1b0, 0x1c0, 0x1c8, 0x1d0, 0x1d8, 0x1e0, 0x1e8, 0x1f0, 0x1b8, 0x1fc, 0x200, 0x204, 0x208, 0x210, 0x214, 0x218, 0x21c, 0x220,
			0x1a0, 0x1a4, 0x1ac, 0x1b4, 0x1c4, 0x1cc, 0x1d4, 0x1dc, 0x1e4, 0x1ec, 0x1f4, 0x1bc, 0x224, 0x230, 0x228, 0x234, 0x238, 0x23c, 0x240, 0x244, 0x248, 0x24c, 0x254, 0x258, 0x25c, 0x260,
			0x264
		];

		/// <summary>
		///     The wire read order after the version byte: true for a u8 field, false for a u32 one. The single sublist is
		///     held out of this table - it sits between the 32nd and 33rd entry and is variable-width.
		/// </summary>
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

		/// <summary>
		///     The full wire layout: the 32 pre-sublist fields, the sublist, then 27 trailing u32.
		/// </summary>
		internal static readonly (bool IsByte, bool IsSublist)[] Layout = BuildLayout();

		/// <summary>The version byte; the client rejects any other value.</summary>
		public byte Version { get; set; } = ExpectedVersion;

		/// <summary>The 6 single-byte fields, in wire order - read positions 0, 18, 19, 20, 25 and 26.</summary>
		public byte[] Flags { get; set; } = new byte[FlagCount];

		/// <summary>
		///     The count-framed sublist between the 32nd and 33rd scaffold field. On a player it is the character's saved
		///     feat specs; it is empty on the create-editor frame and on all but six captured NPCs.
		/// </summary>
		public List<SpawnPreFrameSublistEntry> Sublist { get; } = [];

		/// <summary>
		///     The 53 u32 fields, in wire order, each held as the big-endian value read off the wire - the value the client
		///     byteswaps into its struct. They are re-emitted big-endian, so the round-trip is byte-exact whatever the words
		///     mean; <see cref="DecodeScriptText" /> re-interprets them as the little-endian char buffer.
		/// </summary>
		public uint[] Words { get; set; } = new uint[WordCount];

		/// <summary>The scaffold's width on the wire: the fixed grid plus whatever the sublist costs.</summary>
		public int ByteCount => FixedLength - 4 + SpawnPreFrameSublistCodec.ByteCount(Sublist);

		/// <summary>
		///     Reassembles the 53 u32 into the contiguous 228-byte region 0x184..0x267 they are scattered across in the
		///     client struct, little-endian. This is the buffer both readings of the region are taken over: the stat block
		///     the apply describes (<see cref="WordStatIds" />) and, on an NPC where retail writes only three of the 53
		///     slots, whatever server-side data the other 50 happen to carry.
		/// </summary>
		/// <remarks>
		///     It is a diagnostic view, never a source of content: what it holds on an NPC is a snapshot of the retail
		///     server's own heap rather than anything about that NPC. <c>--report scaffold-heap</c> is the harvest that mines
		///     it.
		/// </remarks>
		public byte[] DecodeScriptBuffer()
		{
			const int baseOffset = 0x184;
			var span = WordStructOffsets[^1] - baseOffset + 4; // 0x264 - 0x184 + 4 = 228
			var buffer = new byte[span];
			for (var i = 0; i < WordCount; i++)
			{
				// The word is stored big-endian; the struct char buffer holds the bytes little-endian, i.e. reversed.
				var at = WordStructOffsets[i] - baseOffset;
				buffer[at + 0] = (byte)(Words[i] & 0xFF);
				buffer[at + 1] = (byte)((Words[i] >> 8) & 0xFF);
				buffer[at + 2] = (byte)((Words[i] >> 16) & 0xFF);
				buffer[at + 3] = (byte)((Words[i] >> 24) & 0xFF);
			}

			return buffer;
		}

		/// <summary>
		///     The printable runs of at least six characters in that buffer, joined by " | " - the human-readable form of the
		///     same view.
		/// </summary>
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

	/// <summary>The from-scratch pre-IIR scaffolds. Value factories, not codec surface.</summary>
	public static class SpawnPreIirs
	{
		/// <summary>
		///     The from-scratch pre-IIR scaffold for a freshly spawned NPC: the version byte, all flags 0, all 53 words 0
		///     except the category word, and an empty sublist.
		/// </summary>
		/// <remarks>
		///     Sourcing it from nothing is the correct shape rather than a safe fallback: across the captured NPC spawns the
		///     retail server fills only w17, w18 and w38, and whatever the other 50 words carry is data it never wrote for
		///     that NPC, so reproducing a captured block would reproduce retail's leak. Its encoding is byte-identical to the
		///     most minimal captured NPC scaffolds.
		/// </remarks>
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
