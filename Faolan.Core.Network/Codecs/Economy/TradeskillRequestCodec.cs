using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Economy
{
	// 0x06B9CD94 TradeskillRequestIIR_c - the client asking for its recipe book (it opened Crafting -> Recipes).
	// The reply is TradeskillReplyIIR, which is where all the content is; this is the bare request.
	//
	//   [u32 subop][u32 0xC350][u32 handle][u8 env][u32 version == 1]
	//
	// It carries NO recipe, no craft, no selection - the client's `TradeskillRequestIIR_c::ReadSubClass`
	// (`FUN_007aa600`) reads one u32 and logs "version mismatch" unless it is 1, and that is the whole reader. The
	// captured request (NecroL80_mage-necromancer_shops_open-crrafting-receipies, the only tradeskill traffic in all
	// 120 dumps) is exactly 21 bytes and accounts for every one of them.
	//
	// That matters for #148: it rules this message OUT as the craft trigger. Combined with the c2s catalog - where
	// the only craft-related GameCharAgent methods are `CraftUntrain(int)` and
	// `RequestRecipeInfo(levelId, templateId1, templateId2, key1, key2)`, neither of which starts a craft - the
	// "create item" action does not go through any message we have seen. The server's own confirmation is
	// `DispatchTradeskillComplete(i_nRecepieID)` (GameCharInterface 0x200D), also uncaptured.
	//
	// The handler used to discard this body wholesale (`_ = body.ReadRemainingArray()`), which is how a request that
	// might have carried a recipe id would have gone unnoticed. It does not - but now that is asserted rather than
	// assumed.
	public static class TradeskillRequestCodec
	{
		public const uint Opcode = (uint)IirTypes.TradeskillRequestIIR;

		// The only version the client's reader accepts; anything else makes it log a version mismatch and stop.
		public const uint Version = 1;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(TradeskillRequestIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);

			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static TradeskillRequestIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(TradeskillRequestCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static TradeskillRequestIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "TradeskillRequestIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var handle = r.ReadU32Be();
			var env = r.ReadU8();
			r.ExpectU32Be(Version, "TradeskillRequestIIR version");

			return new TradeskillRequestIIR(handle, env);
		}

		internal static void Write(FaolanStream s, TradeskillRequestIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(v.Handle).WriteByte(v.Env).WriteUInt32(Version);
		}
	}

	// The whole request: who asked, and the dynel-style env byte (0 in the captured one).
	public sealed record TradeskillRequestIIR(uint Handle, byte Env);
}
