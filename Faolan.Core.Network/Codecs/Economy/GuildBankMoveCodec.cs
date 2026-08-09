using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Economy
{
	// DispatchMoveInventoryItemToOrFromGuildBank (s2c GameCharInterface 0x07) - the acknowledgement that an item
	// moved between a member's bag and the GUILD BANK vault (#654).
	//
	//   [u32 fromType][u32 fromInstance][u32 fromSlot][u32 toType][u32 toInstance][u32 toSlot]
	//
	// 24 bytes, raw big-endian, no protobuf. An ECHO: the six values are exactly what the client asked for, so
	// this says "it happened" rather than describing a result.
	//
	// ⚠️ **The REQUEST is not a message of its own.** It is an ordinary `MoveInventoryItemIIR` (`0x2A8A1B49`) -
	// the same drag the client sends for any container-to-container move - with the guild vault (kind 109) on one
	// end. That is worth stating because the id looks novel in a dump and is not: `MoveInventoryItemCodec` already
	// parses it, and the listener routes on the CONTAINER rather than on a second opcode.
	//
	// The deposit and the withdraw are the same message with the two halves swapped; there is no direction flag,
	// and the `DispatchMoveInventoryItemToGuildBank` (0x21) / `...FromGuildBank` (0x22) pair the method table also
	// names never appears on the wire.
	//
	// ⚠️ A guild-bank end's `instance` is the GUILD id; a bag's is the character's. One message carries one of
	// each, which is why each end names its own owner.
	//
	// [V2: demol80-demonologist_login_gameplay_annotated_3.tshark.json 09:46:04 (deposit) and 09:46:12 (withdraw),
	//  each with the player's annotation - "deposidet an item", "withdrew it"]
	public static class GuildBankMoveCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchMoveInventoryItemToOrFromGuildBank;

		// CANONICAL - the only method here that writes bytes. The s2c ECHO form: six raw big-endian words.
		public static byte[] Build(GuildBankMove value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			s.WriteUInt32((uint)value.From.Container).WriteUInt32(value.From.Instance).WriteUInt32(value.FromSlot);
			s.WriteUInt32((uint)value.To.Container).WriteUInt32(value.To.Instance).WriteUInt32(value.ToSlot);

			return s.ToArray();
		}

		// The s2c echo.
		public static GuildBankMove Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(GuildBankMoveCodec));
			var value = new GuildBankMove(
				new GuildBankEnd((ContainerKind)r.ReadU32Be(), r.ReadU32Be()), r.ReadU32Be(),
				new GuildBankEnd((ContainerKind)r.ReadU32Be(), r.ReadU32Be()), r.ReadU32Be());
			r.EnsureAtEnd();

			return value;
		}

	}

	// One end of the move: which container, and WHOSE - a character id for a bag, a GUILD id for the vault.
	public readonly record struct GuildBankEnd(ContainerKind Container, uint Instance);

	// DispatchMoveInventoryItemToOrFromGuildBank (0x07).
	public sealed record GuildBankMove(GuildBankEnd From, uint FromSlot, GuildBankEnd To, uint ToSlot);
}
