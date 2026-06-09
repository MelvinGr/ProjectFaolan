using System.Numerics;

namespace Faolan.Core.Network.Protobuf
{
	public partial class ProtoVec3
	{
		public static implicit operator ProtoVec3(Vector3 value)
		{
			return new ProtoVec3
			{
				X = value.X,
				Y = value.Y,
				Z = value.Z
			};
		}

		public static implicit operator Vector3(ProtoVec3 value)
		{
			return new Vector3(value.X, value.Y, value.Z);
		}
	}

	public partial class ProtoQuat
	{
		public static implicit operator ProtoQuat(Quaternion value)
		{
			return new ProtoQuat
			{
				X = value.X,
				Y = value.Y,
				Z = value.Z,
				W = value.W
			};
		}

		public static implicit operator Quaternion(ProtoQuat value)
		{
			return new Quaternion(value.X, value.Y, value.Z, value.W);
		}
	}
}
