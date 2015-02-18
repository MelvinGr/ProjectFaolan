using System;
using System.Linq;
using System.Numerics;
using LibFaolan.Database;
using LibFaolan.Network.Shared;

namespace LibFaolan.Data
{
    public class ConanCharacter
    {
        public uint AccountId;
        public UInt32 Class;
        public uint HeadMesh;
        public UInt32 Id;
        public string Language;
        public uint LastConnection;
        public uint lbinprv;
        public UInt32 Level;
        public UInt32 Map;
        public string Name;
        public Vector3 Position;
        public UInt32 Race;
        public UInt32 RealmId;
        public byte Sex;
        public byte Size;
        public uint Voice;

        public ConanCharacter(uint id)
        {
            Id = id;
        }

        public void LoadDetailsFromDatabase(IDatabase database)
        {
            var c =
                database.ExecuteDynamic("SELECT * FROM characters WHERE character_id = '" + Id + "'").FirstOrDefault();
            if (c == null)
                throw new Exception("c == null");

            AccountId = (UInt32) c["account_id"];
            Name = c["name"];
            Race = (UInt32) c["race"];
            Class = (UInt32) c["class"];
            Level = (UInt32) c["level"];
            Sex = (byte) c["sex"];
            RealmId = (UInt32) c["realm_id"];
            Map = (UInt32) c["map_id"];
            Language = c["language"];
            HeadMesh = (UInt32) c["headmesh"];
            Size = (byte) c["size"];
            Voice = (UInt32) c["voice"];
            LastConnection = (UInt32) c["last_connection"];
            Position = new Vector3(c["pos_x"], c["pos_y"], c["pos_z"]);
            lbinprv = (UInt32) c["lbinprv"];
        }

        public bool UpdateLastInfo(IDatabase database, NetworkClient client)
        {
            return database.ExecuteNonQuery("UPDATE characters SET last_connection='" +
                                            Functions.SecondsSindsEpoch() + "' WHERE " + "character_id='" + Id + "'") ==
                   1;
        }
    }
}