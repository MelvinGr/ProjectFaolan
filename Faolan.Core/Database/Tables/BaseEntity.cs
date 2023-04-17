using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Numerics;

namespace Faolan.Core.Database.Tables
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
		public float PositionX { get; set; }
		public float PositionY { get; set; }
		public float PositionZ { get; set; }

		public float RotationX { get; set; }
		public float RotationY { get; set; }
		public float RotationZ { get; set; }

		[NotMapped]
		public Vector3 Position
		{
			get => new Vector3(PositionX, PositionY, PositionZ);
			set
			{
				PositionX = value.X;
				PositionY = value.Y;
				PositionZ = value.Z;
			}
		}

		[NotMapped]
		public Vector3 Rotation
		{
			get => new Vector3(RotationX, RotationY, RotationZ);
			set
			{
				RotationX = value.X;
				RotationY = value.Y;
				RotationZ = value.Z;
			}
		}

		public override string ToString()
		{
			return $"{Id} [{GetType().Name}] [{Position}, {Rotation}]";
		}
	}
}
