using System.ComponentModel.DataAnnotations.Schema;

namespace Faolan.Core.Data
{
    public class CharacterBody : BaseEntity
    {
        [ForeignKey(nameof(Character))]
        public uint CharacterId { get; set; }

        public virtual Character Character { get; set; }

        public float EyebrowScale { get; set; }
        public float CheekDepth { get; set; }
        public float CheekHeight { get; set; }
        public float CheekWidth { get; set; }
        public float ChinLength { get; set; }
        public float ChinWidth { get; set; }
        public float EarAngle { get; set; }
        public float Ears { get; set; }
        public float EyesAngle { get; set; }
        public float EyesVerticalPos { get; set; }
        public float EyesHorizontalPos { get; set; }
        public float EyesDepth { get; set; }
        public float JawWidth { get; set; }
        public float FaceLength { get; set; }
        public float MouthVerticalPos { get; set; }
        public float MouthWidth { get; set; }
        public float NoseAngle { get; set; }
        public float NoseCurve { get; set; }
        public float CrookedNose { get; set; }
        public float NoseLength { get; set; }
        public float NoseWidth { get; set; }

        public uint HeadMesh { get; set; }
        public byte Size { get; set; }
        public byte SkinColor { get; set; }
        public byte Voice { get; set; }

        public float Arm { get; set; }
        public float Chest { get; set; }
        public float Bosom { get; set; }
        public float Stomach { get; set; }
        public float Arse { get; set; }
        public float Thigh { get; set; }
        public float Leg { get; set; }
    }
}