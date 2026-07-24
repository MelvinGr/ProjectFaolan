using System.Numerics;

namespace Faolan.Core.Database.Interfaces
{
	public interface IPositionProvider
	{
		float PositionX { get; set; }
		float PositionY { get; set; }
		float PositionZ { get; set; }
	}

	public static class PositionProviderExtensions
	{
		extension(IPositionProvider position)
		{
			public Vector3 Position
			{
				get => new Vector3(position.PositionX, position.PositionY, position.PositionZ);
				set
				{
					position.PositionX = value.X;
					position.PositionY = value.Y;
					position.PositionZ = value.Z;
				}
			}
		}
	}
}
