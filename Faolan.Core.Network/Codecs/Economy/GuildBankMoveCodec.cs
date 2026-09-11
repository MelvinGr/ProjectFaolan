using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Economy
{
	/// <summary>
	///     <c>DispatchMoveInventoryItemToOrFromGuildBank</c> - the acknowledgement that an item moved between a member's
	///     bag and the guild bank vault:
	///     <c>[u32 fromType][u32 fromInstance][u32 fromSlot][u32 toType][u32 toInstance][u32 toSlot]</c>.
	/// </summary>
	/// <remarks>
	///     24 bytes, raw big-endian, no protobuf. It is an echo: the six values are exactly what the client asked for, so
	///     it says "it happened" rather than describing a result.
	///     <para>
	///         The request is not a message of its own. It is an ordinary <c>MoveInventoryItemIIR</c> - the same drag the
	///         client sends for any container-to-container move - with the guild vault (kind 109) on one end.
	///         <c>MoveInventoryItemCodec</c> already parses it, and the listener routes on the container rather than on a
	///         second opcode.
	///     </para>
	///     <para>
	///         The deposit and the withdraw are the same message with the two halves swapped; there is no direction flag,
	///         and the <c>DispatchMoveInventoryItemToGuildBank</c> / <c>...FromGuildBank</c> pair the method table also
	///         names never appears on the wire.
	///     </para>
	///     <para>
	///         A guild-bank end's <c>instance</c> is the guild id; a bag's is the character's. One message carries one of
	///         each, which is why each end names its own owner.
	///         [V2: demol80-demonologist_login_gameplay_annotated_3.tshark.json 09:46:04 (deposit) and 09:46:12
	///         (withdraw), each with the player's annotation]
	///     </para>
	/// </remarks>
	public static class GuildBankMoveCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchMoveInventoryItemToOrFromGuildBank;

		// Canonical: the only method here that writes bytes. The s2c echo form: six raw big-endian words.
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

	/// <summary>
	///     One end of the move: which container, and whose - a character id for a bag, a guild id for the vault.
	/// </summary>
	public readonly record struct GuildBankEnd(ContainerKind Container, uint Instance);

	/// <summary>The DispatchMoveInventoryItemToOrFromGuildBank body.</summary>
	public sealed record GuildBankMove(GuildBankEnd From, uint FromSlot, GuildBankEnd To, uint ToSlot);
}
