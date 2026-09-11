using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Economy
{
	/// <summary>
	///     The tail of a <c>Tradeskill</c> BehaviorControlCenter record (BehaviorId <c>0x27</c>) - what the player asked
	///     the tradeskill system to do.
	/// </summary>
	/// <remarks>
	///     <c>0x27</c> is the client's own registered command class, so it is one command carrying two operations,
	///     discriminated by the tail's leading byte:
	///     <code>
	///     0x01  craft from a recipe          [u32 recipeId][u32 quantity]
	///     0x02  combine two inventory items  [u32 container][u32 charInstance][u32 slot]  x2
	///     </code>
	///     <para>
	///         The args do not discriminate: <c>bcc_action_id</c> is 49340 on both captured forms and the field that
	///         differs is <c>commandqueue_client_id</c>, a sequence number. That matters because
	///         <c>MoveEquippedToFromInventory</c> does split its directions on <c>bcc_action_id</c>, so the obvious place
	///         to look is the wrong one here.
	///     </para>
	///     <para>
	///         On the socket form the first address is the item receiving the gem and the second is the gem. The replies
	///         say so: <c>DispatchAddToItemSlot</c> names the first address and <c>DispatchRemoveItem</c> the second.
	///         [V2: NecroL80_mage-necromancer_login_gameplay_annotated_5 fr17374 (craft, tail 9) and
	///         NecroL80_mage-necromancer_login_gameplay_annotated_8 fr8215 (socket, tail 25), each with the player's own
	///         annotation in the same capture]
	///     </para>
	/// </remarks>
	public static class TradeskillCommandCodec
	{
		// Canonical: the only method here that writes bytes.
		public static byte[] Build(TradeskillCommand value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			s.WriteByte((byte)value.Action);
			if (value.Action == TradeskillAction.Craft)
			{
				s.WriteUInt32(value.RecipeId).WriteUInt32(value.Count);
				return s.ToArray();
			}

			Write(s, value.Target);
			Write(s, value.Gem);

			return s.ToArray();

			static void Write(FaolanStream stream, InventoryAddress address)
			{
				stream.WriteUInt32((uint)address.Container).WriteUInt32(address.CharInstance).WriteUInt32(address.Slot);
			}
		}

		public static TradeskillCommand Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(TradeskillCommandCodec));
			var action = (TradeskillAction)r.ReadU8();
			var value = action switch
			{
				TradeskillAction.Craft => TradeskillCommands.Craft(r.ReadU32Be(), r.ReadU32Be()),
				TradeskillAction.Socket => TradeskillCommands.Socket(ReadAddress(ref r), ReadAddress(ref r)),
				_ => throw new CodecParseException($"{nameof(TradeskillCommandCodec)}: unknown tradeskill action 0x{(byte)action:X2}")
			};
			r.EnsureAtEnd();

			return value;
		}

		private static InventoryAddress ReadAddress(ref SpanReader r)
		{
			return new InventoryAddress((ContainerKind)r.ReadU32Be(), r.ReadU32Be(), r.ReadU32Be());
		}
	}

	/// <summary>Which of the tradeskill system's operations a <c>0x27</c> record is.</summary>
	public enum TradeskillAction : byte
	{
		Craft = 0x01,
		Socket = 0x02
	}

	/// <summary>One cell of one container, as the socket form addresses it.</summary>
	public readonly record struct InventoryAddress(ContainerKind Container, uint CharInstance, uint Slot);

	/// <summary>
	///     A <c>Tradeskill</c> request. The two operations share no fields, so the unused half is default, and they are
	///     built through the factories below rather than by picking arguments - which is what keeps a craft from being
	///     spelled as a socket with two empty addresses.
	/// </summary>
	public sealed record TradeskillCommand(TradeskillAction Action, uint RecipeId, uint Count, InventoryAddress Target, InventoryAddress Gem);

	public static class TradeskillCommands
	{
		public static TradeskillCommand Craft(uint recipeId, uint count)
		{
			return new TradeskillCommand(TradeskillAction.Craft, recipeId, count, default, default);
		}

		// `target` is the item receiving the gem; `gem` is the gem. The order is settled by the replies rather than by the
		// tail's reading order - see the codec's remarks.
		public static TradeskillCommand Socket(InventoryAddress target, InventoryAddress gem)
		{
			return new TradeskillCommand(TradeskillAction.Socket, 0, 0, target, gem);
		}
	}
}
