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
		// The spawn transform's facing is a full quaternion (the FA015807 ProtoQuat). Defaults to W=1 so a row with
		// no captured facing (X=Y=Z=0) is the valid identity quaternion (0,0,0,1) rather than the degenerate
		// (0,0,0,0) the client cannot realize. The live sim/movement layer is Vector3-based - bridge with
		// RotationConvert at the DB<->sim boundary.
		public float RotationW { get; set; } = 1f;

		[NotMapped]
		public Quaternion Rotation
		{
			get => new Quaternion(RotationX, RotationY, RotationZ, RotationW);
			set
			{
				RotationX = value.X;
				RotationY = value.Y;
				RotationZ = value.Z;
				RotationW = value.W;
			}
		}

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

		public override string ToString()
		{
			return $"{Id} [{GetType().Name}] [{Position}, {Rotation}]";
		}
	}
}
