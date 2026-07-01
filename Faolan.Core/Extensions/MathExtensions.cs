using System;
using System.Numerics;

namespace Faolan.Core.Extensions
{
	public static class MathExtensions
	{
		extension(MathF)
		{
			public static float DegreesToRadians(float degrees)
			{
				return degrees * (MathF.PI / 180f);
			}
		}

		extension(Vector3 vector3)
		{
			// A yaw-only facing quaternion (rotation about the Y/up axis) that points `from` at `target` - the
			// (0, qy, 0, qw) shape the capture's MoveTask facings all use. Returns identity (facing +Z) when the two
			// points coincide. Yaw measured from +Z toward +X (atan2(dx, dz)), matching the captured Y-axis quaternions.
			public Quaternion FaceTowards(Vector3 target)
			{
				var dx = target.X - vector3.X;
				var dz = target.Z - vector3.Z;
				if (dx == 0f && dz == 0f)
					return Quaternion.Identity;

				var yaw = MathF.Atan2(dx, dz);
				return new Quaternion(0f, MathF.Sin(yaw / 2f), 0f, MathF.Cos(yaw / 2f));
			}

			public Quaternion ToQuaternion()
			{
				var radians = vector3 * (MathF.PI / 180.0f);
				return Quaternion.CreateFromYawPitchRoll(radians.Y, radians.X, radians.Z);
			}
		}

		extension(Quaternion quaternion)
		{
			public Vector3 ToVector3() // ToEulerDegrees
			{
				var x = quaternion.X;
				var y = quaternion.Y;
				var z = quaternion.Z;
				var w = quaternion.W;

				var sinrCosp = 2.0f * (w * x + y * z);
				var cosrCosp = 1.0f - 2.0f * (x * x + y * y);
				var roll = MathF.Atan2(sinrCosp, cosrCosp);

				var sinp = 2.0f * (w * y - z * x);
				var pitch = MathF.Abs(sinp) >= 1.0f
					? MathF.CopySign(MathF.PI / 2.0f, sinp)
					: MathF.Asin(sinp);

				var sinyCosp = 2.0f * (w * z + x * y);
				var cosyCosp = 1.0f - 2.0f * (y * y + z * z);
				var yaw = MathF.Atan2(sinyCosp, cosyCosp);

				var degrees = new Vector3(roll * 180.0f / MathF.PI, pitch * 180.0f / MathF.PI, yaw * 180.0f / MathF.PI);
				return degrees;
			}

			public static Quaternion CreateFromYawPitchRollDegrees(float yawDegrees, float pitchDegrees, float rollDegrees)
			{
				return Quaternion.CreateFromYawPitchRoll(MathF.DegreesToRadians(yawDegrees), MathF.DegreesToRadians(pitchDegrees), MathF.DegreesToRadians(rollDegrees));
			}
		}
	}
}
