using System;
using System.IO;
using Faolan.Core.Network.Packets;
using ProtoBuf;

namespace Faolan.Core.Extensions
{
	public static class ProtobufExtensions
	{
		public static T FromProtobuf<T>(this ReadOnlySpan<byte> data)
		{
			return Serializer.Deserialize<T>(data);
		}

		public static byte[] ToProtobuf(this FaolanProtobufMessage obj)
		{
			using var ms = new MemoryStream();
			Serializer.NonGeneric.Serialize(ms, obj);
			return ms.ToArray();
		}
	}
}
