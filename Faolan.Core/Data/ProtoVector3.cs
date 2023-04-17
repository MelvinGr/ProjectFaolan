using System.Numerics;
using Faolan.Core.Network.Packets;
using ProtoBuf;

namespace Faolan.Core.Data
{
	[ProtoContract]
	public sealed class ProtoVector3 : FaolanProtobufMessage
	{
		[ProtoMember(1, IsRequired = true, DataFormat = DataFormat.FixedSize)]
		public float X { get; set; }

		[ProtoMember(2, IsRequired = true, DataFormat = DataFormat.FixedSize)]
		public float Y { get; set; }

		[ProtoMember(3, IsRequired = true, DataFormat = DataFormat.FixedSize)]
		public float Z { get; set; }

		public static implicit operator ProtoVector3(Vector3 value)
		{
			return new ProtoVector3
			{
				X = value.X,
				Y = value.Y,
				Z = value.Z
			};
		}

		public static implicit operator Vector3(ProtoVector3 value)
		{
			return new Vector3(value.X, value.Y, value.Z);
		}
	}
}
