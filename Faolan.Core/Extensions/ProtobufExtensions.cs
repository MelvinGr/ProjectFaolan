using System;
using System.IO;
using ProtoBuf;

namespace Faolan.Core.Extensions
{
	public static class ProtobufExtensions
	{
		public static T FromProtobuf<T>(this ReadOnlySpan<byte> data)
		{
			return Serializer.Deserialize<T>(data);
		}

		// Serializes any protobuf-net contract (e.g. the generated client schema types in
		// Faolan.Core.Network.Protobuf) to bytes.
		public static byte[] ToProtobuf(this object obj)
		{
			using var ms = new MemoryStream();
			Serializer.Serialize(ms, obj);
			return ms.ToArray();
		}
	}
}
