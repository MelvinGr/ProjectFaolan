using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Numerics;

namespace Faolan.Core.Data
{
	public abstract class BaseEntity
	{
		[Key]
		public uint Id { get; set; }

		public override string ToString()
		{
			return $"{Id} [{GetType().Name}]";
		}
	}

	public abstract class Base3DEntity : BaseEntity
	{
		[NotMapped]
		public Vector3 Position
		{
			get => new(PositionX, PositionY, PositionZ);
			set
			{
				PositionX = value.X;
				PositionY = value.Y;
				PositionZ = value.Z;
			}
		}

		public float PositionX { get; set; }
		public float PositionY { get; set; }
		public float PositionZ { get; set; }

		[NotMapped]
		public Vector3 Rotation
		{
			get => new(RotationX, RotationY, RotationZ);
			set
			{
				RotationX = value.X;
				RotationY = value.Y;
				RotationZ = value.Z;
			}
		}

		public float RotationX { get; set; }
		public float RotationY { get; set; }
		public float RotationZ { get; set; }

		public override string ToString()
		{
			return $"{Id} [{GetType().Name}] [{Position}, {Rotation}]";
		}
	}
}
