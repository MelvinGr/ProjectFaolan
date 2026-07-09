using System;
using System.Text.Json;

namespace Faolan.Core.Extensions
{
	public static class ObjectExtensions
	{
		public static T FromJson<T>(this string json)
		{
			if (string.IsNullOrEmpty(json))
				return default;

			return JsonSerializer.Deserialize<T>(json);
		}

		public static string ToJson(this object source, bool indented = false)
		{
			if (source == null)
				return null;

			return JsonSerializer.Serialize(source, new JsonSerializerOptions
			{
				WriteIndented = indented
			});
		}

		extension<TEnum>(TEnum value) where TEnum : struct, Enum
		{
			public static TEnum[] GetValues()
			{
				return Enum.GetValues<TEnum>();
			}
		}
	}
}
