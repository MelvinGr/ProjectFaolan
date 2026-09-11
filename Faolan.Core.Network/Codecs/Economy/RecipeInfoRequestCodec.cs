using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.Economy
{
	/// <summary>
	///     c2s <c>RequestRecipeInfo(uint32 levelId, uint32 templateId1, uint32 templateId2, DecryptionKey_c key1,
	///     DecryptionKey_c key2)</c>:
	///     <c>[u32 levelId][u32 templateId1][u32 templateId2][16 B key1][16 B key2]</c>.
	/// </summary>
	/// <remarks>
	///     4 + 4 + 4 + 16 + 16 = 44, which is exactly the captured body length, so the widths are pinned by the signature
	///     adding up rather than by variance - there is one frame.
	///     <para>
	///         The client sends the server two decryption keys: <c>DecryptionKey_c</c> is the 16-byte TEA key that opens a
	///         row of the encrypted template catalog (RDB type 1000020), so this is a message in which the client holds
	///         the key. In the single captured frame the two keys are identical and <c>templateId2</c> is 0, a
	///         one-ingredient query.
	///     </para>
	/// </remarks>
	public static class RecipeInfoRequestCodec
	{
		public const uint Method = (uint)GameCharAgentMethods.RequestRecipeInfo;

		// A `DecryptionKey_c` on the wire.
		public const int KeyLength = 16;

		public static byte[] Build(RecipeInfoRequest value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);

			return s.ToArray();
		}

		public static RecipeInfoRequest Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(RecipeInfoRequestCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();

			return value;
		}

		internal static RecipeInfoRequest Read(ref SpanReader r)
		{
			var levelId = r.ReadU32Be();
			var templateId1 = r.ReadU32Be();
			var templateId2 = r.ReadU32Be();
			var key1 = r.ReadBytes(KeyLength).ToArray();

			return new RecipeInfoRequest(levelId, templateId1, templateId2, key1, r.ReadBytes(KeyLength).ToArray());
		}

		internal static void Write(FaolanStream s, RecipeInfoRequest v)
		{
			s.WriteUInt32(v.LevelId).WriteUInt32(v.TemplateId1).WriteUInt32(v.TemplateId2).WriteArray(v.Key1).WriteArray(v.Key2);
		}
	}

	public sealed record RecipeInfoRequest(uint LevelId, uint TemplateId1, uint TemplateId2, byte[] Key1, byte[] Key2);
}
