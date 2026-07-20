using System;
using System.IO;
using ProtoBuf;

namespace Faolan.Core.Extensions
{
	public static class ProtobufExtensions
	{
		public static T FromProtobuf<T>(this ReadOnlySpan<byte> data) where T : IExtensible
		{
			return Serializer.Deserialize<T>(data);
		}

		public static T FromProtobuf<T>(this byte[] data) where T : IExtensible
		{
			return Serializer.Deserialize<T>(data);
		}

		public static byte[] ToProtobuf(this IExtensible obj)
		{
			using var ms = new MemoryStream();
			Serializer.Serialize(ms, obj);
			return ms.ToArray();
		}
	}
}
