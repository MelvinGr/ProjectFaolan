using System.Numerics;

namespace Faolan.Core.Protobuf
{
	public partial class ProtoVec3
	{
		public ProtoVec3()
		{
		}

		public ProtoVec3(float x, float y, float z)
		{
			X = x;
			Y = y;
			Z = z;
		}

		public static implicit operator ProtoVec3(Vector3 value)
		{
			return new ProtoVec3(value.X, value.Y, value.Z);
		}

		public static implicit operator Vector3(ProtoVec3 value)
		{
			return new Vector3(value.X, value.Y, value.Z);
		}
	}
}
