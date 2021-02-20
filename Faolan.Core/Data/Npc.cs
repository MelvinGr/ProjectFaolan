using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Numerics;

namespace Faolan.Core.Data
{
    public class Npc : BaseObject
    {
        [MaxLength(255)]
        public string Name { get; set; }

        //public uint NameId { get; set; }
        public uint Action { get; set; }
        public ushort Level { get; set; }
        public uint Fraction { get; set; }
        public uint Health { get; set; }
        public uint Stamina { get; set; }
        public uint Mana { get; set; }
        public uint GetExp { get; set; }
        public string Extras { get; set; }

        [ForeignKey(nameof(Map))]
        public uint MapId { get; set; }

        public virtual WorldMap Map { get; set; }

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

        public virtual NpcBody Body { get; set; }
        public virtual NpcEquipment Equipment { get; set; }
    }
}