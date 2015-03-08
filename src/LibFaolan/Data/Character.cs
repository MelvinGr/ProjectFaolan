using System;
using LibFaolan.Database;
using LibFaolan.Extentions;
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
        public byte SkinColour; //0x03 = female ; 0x04 = male ; 0x02 = male
        public byte Voice;

        public Character(uint id)
        {
            Id = id;
        }

        public void LoadDetailsFromDatabase(IDatabase database)
        {
            var c =
                database.ExecuteReader("SELECT * FROM characters WHERE character_id=" + Id).ToDictionary();
            if (c.Count == 0)
                throw new Exception("c.Count == 0");

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
            Position = new Vector3((float) c["pos_x"], (float) c["pos_y"], (float) c["pos_z"]);
            Rotation = new Vector3((float) c["rot_x"], (float) c["rot_y"], (float) c["rot_z"]);
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