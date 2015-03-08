using System;
using System.Linq;
using LibFaolan.Database;
using LibFaolan.Extentions;
using LibFaolan.Math;

namespace LibFaolan.Data
{
    public sealed class ConanMap
    {
        public UInt32 Id;
        public string Name;
        public Vector3 Position;
        public Vector3 Rotation;
        public static ConanMap[] AllMaps { get; private set; }

        public static void Init(IDatabase database)
        {
            AllMaps = database.ExecuteReader("SELECT * FROM maps").ToIEnumerable().Select(c => new ConanMap
            {
                Id = (UInt32) c["map_id"],
                Name = c["map_name"],
                Position = new Vector3((float) c["position_0"], (float) c["position_1"], (float) c["position_2"]),
                Rotation = new Vector3((float) c["rotation_0"], (float) c["rotation_1"], (float) c["rotation_2"])
            }).ToArray();
        }
    }
}