using System;
using System.Numerics;

namespace Faolan.Core.Extensions
{
	public static class MathExtensions
	{
		extension(Vector3 vector3)
		{
			/// <summary>
			///     A yaw-only facing quaternion (rotation about the Y/up axis) that points this point at
			///     <paramref name="target" /> - the (0, qy, 0, qw) shape the captured MoveTask facings all use.
			/// </summary>
			/// <returns>Identity (facing +Z) when the two points coincide.</returns>
			/// <remarks>Yaw is measured from +Z toward +X (atan2(dx, dz)), matching the captured Y-axis quaternions.</remarks>
			public Quaternion FaceTowards(Vector3 target)
			{
				var dx = target.X - vector3.X;
				var dz = target.Z - vector3.Z;
				if (dx == 0f && dz == 0f)
					return Quaternion.Identity;

				var yaw = MathF.Atan2(dx, dz);
				return new Quaternion(0f, MathF.Sin(yaw / 2f), 0f, MathF.Cos(yaw / 2f));
			}
		}
	}
}
