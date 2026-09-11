using System.ComponentModel.DataAnnotations;
using Faolan.Core.Database.Interfaces;

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

	public abstract class Base3DEntity : BaseEntity, IPositionProvider, IRotationProvider
	{
		public float PositionX { get; set; }
		public float PositionY { get; set; }
		public float PositionZ { get; set; }

		/// <summary>
		///     The scalar component of the spawn transform's facing, which is a full quaternion (the FA015807
		///     ProtoQuat).
		/// </summary>
		/// <remarks>
		///     Defaults to 1 so that a row with no recorded facing (X=Y=Z=0) is the identity quaternion (0,0,0,1)
		///     rather than the degenerate (0,0,0,0) the client cannot realize. The live simulation and movement layer
		///     is Vector3-based; bridge with RotationConvert at the database/simulation boundary.
		/// </remarks>
		public float RotationW { get; set; } = 1f;
		public float RotationX { get; set; }
		public float RotationY { get; set; }
		public float RotationZ { get; set; }

		public override string ToString()
		{
			return $"{Id} [{GetType().Name}] [{this.Position}, {this.Rotation}]";
		}
	}
}
