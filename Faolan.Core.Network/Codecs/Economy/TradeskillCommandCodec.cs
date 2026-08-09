using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Economy
{
	// The TAIL of a `Tradeskill` BehaviorControlCenter record (BehaviorId `0x27`) - what the player asked the
	// tradeskill system to do (#754).
	//
	// `0x27` is the client's own registered command class (#549 enumerated every caller of the Command base ctor),
	// so it is one command carrying TWO operations, discriminated by the tail's leading byte:
	//
	//     0x01  craft from a recipe          [u32 recipeId][u32 quantity]
	//     0x02  combine two inventory items  [u32 container][u32 charInstance][u32 slot]  x2
	//
	// ⚠️ The ARGS do not discriminate. `bcc_action_id` is 49340 on both captured forms and the field that differs is
	// `commandqueue_client_id`, a sequence number - which matters because that IS how `MoveEquippedToFromInventory`
	// splits its directions, so the obvious place to look is the wrong one here.
	//
	// ⚠️ On the socket form the FIRST address is the item RECEIVING the gem and the second is the gem. That is not a
	// reading-order guess: the replies say so - `DispatchAddToItemSlot` names the first address and
	// `DispatchRemoveItem` the second.
	//
	// [V2: NecroL80_mage-necromancer_login_gameplay_annotated_5 fr17374 (craft, tail 9) and
	//  NecroL80_mage-necromancer_login_gameplay_annotated_8 fr8215 (socket, tail 25), each with the player's own
	//  annotation in the same capture - "Crafting a [Bloodsilver Khopesh]" and "after [Fine Steelsilk Armbands]"]
	public static class TradeskillCommandCodec
	{
		// CANONICAL - the only method here that writes bytes.
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

	// Which of the tradeskill system's operations a `0x27` record is.
	public enum TradeskillAction : byte
	{
		Craft = 0x01,
		Socket = 0x02
	}

	// One cell of one container, as the socket form addresses it.
	public readonly record struct InventoryAddress(ContainerKind Container, uint CharInstance, uint Slot);

	// A `Tradeskill` request. The two operations share no fields, so the unused half is default - and they are built
	// through the factories below rather than by picking arguments, which is what keeps a craft from being spelled
	// as a socket with two empty addresses.
	public sealed record TradeskillCommand(TradeskillAction Action, uint RecipeId, uint Count, InventoryAddress Target, InventoryAddress Gem);

	public static class TradeskillCommands
	{
		public static TradeskillCommand Craft(uint recipeId, uint count)
		{
			return new TradeskillCommand(TradeskillAction.Craft, recipeId, count, default, default);
		}

		// `target` is the item RECEIVING the gem; `gem` is the gem. See the codec's warning - the order is settled by
		// the replies, not by the tail's reading order.
		public static TradeskillCommand Socket(InventoryAddress target, InventoryAddress gem)
		{
			return new TradeskillCommand(TradeskillAction.Socket, 0, 0, target, gem);
		}
	}
}
