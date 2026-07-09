using System.Numerics;

namespace Faolan.Core.Protobuf
{
	public partial class ProtoQuat
	{
		public ProtoQuat()
		{
		}

		public ProtoQuat(float x, float y, float z, float w)
		{
			X = x;
			Y = y;
			Z = z;
			W = w;
		}

		public static implicit operator ProtoQuat(Quaternion value)
		{
			return new ProtoQuat(value.X, value.Y, value.Z, value.W);
		}

		public static implicit operator Quaternion(ProtoQuat value)
		{
			return new Quaternion(value.X, value.Y, value.Z, value.W);
		}
	}
}
