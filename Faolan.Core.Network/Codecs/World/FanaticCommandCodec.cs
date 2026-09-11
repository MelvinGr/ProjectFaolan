using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	/// <summary>
	///     FanaticCommandIIR - what the client sends when a GM runs one of its roughly 133 GM commands. "Fanatic" is the
	///     client's own name for its GM and admin subsystem - it also names the Fanatic debug channel and the tweak table's
	///     TWK_karma_Fanatic entries - and has nothing to do with the player-facing game.
	/// </summary>
	/// <remarks>
	///     <c>/gm</c> is FanaticGMLevelCommand_c, the command that reads and sets the privilege mask the whole surface is
	///     gated on, and the one command registered without a permission record so anyone can reach it. The mask, the
	///     command catalog and each command's required bit are in docs/subsystems/gm-commands.md.
	///     <para>
	///         The grammar is measured against real bytes from a live GM session. The body starts at the sub-opcode; the
	///         leading u32 record length belongs to the framing:
	///         <code>
	///         [u32 subop][u32 class][u32 handle]      the IIR carrier
	///         [u8 flag]                               the carrier's trailing flag byte, 0 in everything observed
	///         [u32 word0][u32 targetA][u32 targetB]   ReadSubClass's obj+0x2C / +0x30 / +0x34
	///         [u32 textLength][textLength bytes]      the command string, Latin-1
	///         </code>
	///         So <c>/cloak</c> sends flag 0, word0 2, targets 0/0, length 6, and <c>/teleport gm island</c> the same with
	///         length 19.
	///     </para>
	///     <para>
	///         The flag byte belongs to the carrier - GameActionListCodec writes the same byte after its handle - not to
	///         this record, so a reader that starts the record at the handle is off by one and every field after it slides.
	///         The symptom is plausible garbage rather than a parse failure. ReadSubClass (slot 8, FUN_00663860) is the
	///         authority: three u32 then BinaryStream::ReadString (FUN_00663c20).
	///     </para>
	///     <para>
	///         <c>word0</c> (obj+0x2C) is 2 for every command observed and is not identified; the command itself is entirely
	///         in the text, so dispatch is a string parse, exactly as the client's own log ("Received command string :
	///         &lt;text&gt;") says. ReadSubClass writes that line on the Fanatic channel at level 2, which is the cheapest
	///         way to see the client's view of what it sent.
	///     </para>
	///     <para>
	///         The target pair is an (entityClass, handle) dynel reference rather than two opaque words: running the same
	///         command with and without a target selected sends 0/0 in the first case and the Character class plus the
	///         sender's own handle in the second. A pair of zeros means no explicit target, and the client's reader treats
	///         it as such by substituting its own obj+8 pair.
	///     </para>
	///     <para>
	///         The text's length prefix is a u32, not the u16 our wire strings normally use: BinaryStream::ReadString reads
	///         four bytes and rejects anything over 0xFFFFFF with "string size is too long". Reading it as a u16 takes the
	///         high half of the length, which is 0 for every real command, so the argument parses as empty.
	///     </para>
	///     <para>
	///         Parse only, deliberately: what the server should send back is not decoded, since no capture in the corpus
	///         contains one of these - the retail traces are ordinary play, not GM sessions - so any reply would be invented.
	///         Parsing at least turns an unknown sub-opcode into the command the client asked for, which is what a reply has
	///         to be built from. A malformed body throws rather than degrading to a half-read record; the inbound handler
	///         catches that and tells the GM the body did not parse. Open work tracked in Gitea #448.
	///     </para>
	/// </remarks>
	public static class FanaticCommandCodec
	{
		public const uint Opcode = (uint)IirTypes.FanaticCommandIIR;

		public static FanaticCommandIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(FanaticCommandCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static FanaticCommandIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "FanaticCommandIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var handle = r.ReadU32Be();
			var flag = r.ReadU8();
			var word0 = r.ReadU32Be();
			var targetA = r.ReadU32Be();
			var targetB = r.ReadU32Be();
			return new FanaticCommandIIR(handle, flag, word0, targetA, targetB, r.ReadStringU32Length());
		}
	}

	/// <summary>The FanaticCommandIIR body.</summary>
	public sealed record FanaticCommandIIR(uint Handle, byte Flag, uint Word0, uint TargetA, uint TargetB, string Text);
}
