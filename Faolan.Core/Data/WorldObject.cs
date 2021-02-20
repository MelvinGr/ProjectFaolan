using System.ComponentModel.DataAnnotations.Schema;
using System.Numerics;

namespace Faolan.Core.Data
{
    public class WorldObject : BaseObject
    {
        public uint Data0 { get; set; }
        public uint Data1 { get; set; }
        public uint Data2 { get; set; }
        public uint Data3 { get; set; }
        public uint Data4 { get; set; }
        public uint Data5 { get; set; }
        public string Sdat1 { get; set; }
        public string Sdat2 { get; set; }
        public uint Unk0 { get; set; }
        public uint Unk1 { get; set; }

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
    }
}