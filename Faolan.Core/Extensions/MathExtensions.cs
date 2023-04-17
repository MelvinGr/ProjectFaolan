using System;
using System.Numerics;

namespace Faolan.Core.Extensions
{
	public static class QuaternionExtensions
	{
		extension(Vector3 degrees)
		{
			public Quaternion ToQuaternion()
			{
				var radians = degrees * (MathF.PI / 180.0f);

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

	public static class MathExtensions
	{
		extension(MathF)
		{
			public static float DegreesToRadians(float degrees)
			{
				return degrees * (MathF.PI / 180f);
			}
		}
	}
}
