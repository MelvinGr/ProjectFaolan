using System;
using System.Linq;
using LibFaolan.Database;
using LibFaolan.Math;
using LibFaolan.Network;
using LibFaolan.Other;

namespace LibFaolan.Data
{
    public sealed class Character
    {
        public uint AccountId;
        public byte Class;
        public uint HeadMesh;
        public UInt32 Id;
        public string Language;
        public uint LastConnection;
        public uint Lbinprv;
        public byte Level;
        public UInt32 Map;
        public string Name;
        public Vector3 Position;
        public byte Race;
        public byte RealmId;
        public Vector3 Rotation;
        public byte Sex;
        public byte Size;
        public byte Voice;

        public Character(uint id)
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
            Race = (byte) c["race"];
            Class = (byte) c["class"];
            Level = (byte) c["level"];
            Sex = (byte) c["sex"];
            RealmId = (byte) c["realm_id"];
            Map = (UInt32) c["map_id"];
            Language = c["language"];
            HeadMesh = (UInt32) c["headmesh"];
            Size = (byte) c["size"];
            Voice = (byte) c["voice"];
            LastConnection = (UInt32) c["last_connection"];
            Position = new Vector3(float.Parse(c["pos_x"]), float.Parse(c["pos_y"]), float.Parse(c["pos_z"]));
            Rotation = new Vector3(float.Parse(c["rot_x"]), float.Parse(c["rot_y"]), float.Parse(c["rot_z"]));
            Lbinprv = (UInt32) c["lbinprv"];
        }

        public bool UpdateLastInfo(IDatabase database, NetworkClient client)
        {
            return database.ExecuteNonQuery("UPDATE characters SET last_connection='" +
                                            Functions.SecondsSindsEpoch() + "' WHERE " + "character_id='" + Id + "'") ==
                   1;
        }
    }
}