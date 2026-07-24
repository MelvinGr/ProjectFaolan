using System.Numerics;

namespace Faolan.Core.Database.Interfaces
{
	public interface IRotationProvider
	{
		float RotationX { get; set; }
		float RotationY { get; set; }
		float RotationZ { get; set; }
		float RotationW { get; set; }
	}

	public static class RotationProviderExtensions
	{
		extension(IRotationProvider rotation)
		{
			public Quaternion Rotation
			{
				get => new Quaternion(rotation.RotationX, rotation.RotationY, rotation.RotationZ,  rotation.RotationW);
				set
				{
					rotation.RotationX = value.X;
					rotation.RotationY = value.Y;
					rotation.RotationZ = value.Z;
					rotation.RotationW = value.W;
				}
			}
		}
	}
}
