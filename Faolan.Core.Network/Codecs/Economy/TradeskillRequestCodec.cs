using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.Economy
{
	/// <summary>
	///     <c>TradeskillRequestIIR_c</c> - the client asking for its recipe book, having opened Crafting - Recipes. The
	///     reply is <c>TradeskillReplyIIR</c>, which is where all the content is; this is the bare request:
	///     <c>[u32 subop][u32 entityClass][u32 handle][u8 env][u32 version == 1]</c>.
	/// </summary>
	/// <remarks>
	///     It carries no recipe, no craft and no selection: the client's <c>TradeskillRequestIIR_c::ReadSubClass</c>
	///     (<c>FUN_007aa600</c>) reads one u32 and logs "version mismatch" unless it is 1, and that is the whole reader.
	///     The captured request (NecroL80_mage-necromancer_shops_open-crrafting-receipies, the only tradeskill traffic in
	///     all 120 dumps) is exactly 21 bytes and accounts for every one of them.
	///     <para>
	///         That rules this message out as the craft trigger. Combined with the c2s catalog - where the only
	///         craft-related GameCharAgent methods are <c>CraftUntrain(int)</c> and
	///         <c>RequestRecipeInfo(levelId, templateId1, templateId2, key1, key2)</c>, neither of which starts a craft -
	///         the "create item" action does not go through any message that has been seen. The server's own confirmation
	///         is <c>DispatchTradeskillComplete(i_nRecepieID)</c>, also uncaptured.
	///     </para>
	/// </remarks>
	public static class TradeskillRequestCodec
	{
		public const uint Opcode = (uint)IirTypes.TradeskillRequestIIR;

		// The only version the client's reader accepts; anything else makes it log a version mismatch and stop.
		public const uint Version = 1;

		// Canonical: the only method here that writes bytes.
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

	/// <summary>The whole request: who asked, and the dynel-style env byte (0 in the captured one).</summary>
	public sealed record TradeskillRequestIIR(uint Handle, byte Env);
}
