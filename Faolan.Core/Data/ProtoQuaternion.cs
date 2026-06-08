using System.Numerics;
using Faolan.Core.Network.Packets;
using ProtoBuf;

namespace Faolan.Core.Data
{
	[ProtoContract] // TODO: Obsolete, replace with generated ProtoQuat
	public sealed class ProtoQuaternion : FaolanProtobufMessage
	{
		[ProtoMember(4, IsRequired = true, DataFormat = DataFormat.FixedSize)]
		public float W { get; set; }

		[ProtoMember(1, IsRequired = true, DataFormat = DataFormat.FixedSize)]
		public float X { get; set; }

		[ProtoMember(2, IsRequired = true, DataFormat = DataFormat.FixedSize)]
		public float Y { get; set; }

		[ProtoMember(3, IsRequired = true, DataFormat = DataFormat.FixedSize)]
		public float Z { get; set; }

		public static implicit operator ProtoQuaternion(Quaternion value)
		{
			return new ProtoQuaternion
			{
				X = value.X,
				Y = value.Y,
				Z = value.Z,
				W = value.W
			};
		}

		public static implicit operator Quaternion(ProtoQuaternion value)
		{
			return new Quaternion(value.X, value.Y, value.Z, value.W);
		}
	}
}
