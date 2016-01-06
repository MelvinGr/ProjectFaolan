using System;
using LibFaolan.Database;
using LibFaolan.Extentions;
using LibFaolan.Math;
using LibFaolan.Network;

namespace LibFaolan.Data
{
    public sealed class Character
    {
        public uint AccountId;
        public byte Class;
        public uint Experience;
        public uint HeadMesh;
        public uint Id;
        public string Language;
        public uint LastConnection;
        public uint Lbinprv;
        public byte Level;
        public uint Map;
        public string Name;
        public Vector3 Position;
        public byte Race;
        public byte RealmId;
        public Vector3 Rotation;
        public uint SelectedTarget;
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

            AccountId = (uint) c["account_id"];
            Name = (string) c["name"];
            Race = (byte) c["race"];
            Class = (byte) c["class"];
            Level = (byte) c["level"];
            Experience = (uint) c["Experience"];
            Sex = (byte) c["sex"];
            RealmId = (byte) c["realm_id"];
            Map = (uint) c["map_id"];
            Language = (string) c["language"];
            HeadMesh = (uint) c["headmesh"];
            Size = (byte) c["size"];
            Voice = (byte) c["voice"];
            LastConnection = (uint) c["last_connection"];
            Position = new Vector3((float) c["pos_x"], (float) c["pos_y"], (float) c["pos_z"]);
            Rotation = new Vector3((float) c["rot_x"], (float) c["rot_y"], (float) c["rot_z"]);
            Lbinprv = (uint) c["lbinprv"];
        }

        public bool UpdateLastInfo(IDatabase database, NetworkClient client)
        {
            var t2 = 0;
            DllImport.Other.time(ref t2);

            return database.ExecuteNonQuery("UPDATE characters SET " +
                                            "last_connection=" + t2 + " " +
                                            "WHERE character_id=" + Id) == 1;
        }

        public bool SaveDataToDatabase(IDatabase database)
        {
            return database.ExecuteNonQuery(
                "UPDATE characters SET " +
                "level=" + Level + ", " +
                "map_id=" + Map + ", " +
                "pos_x=" + Position.X + ", " +
                "pos_y=" + Position.Y + ", " +
                "pos_z=" + Position.Z + ", " +
                "rot_x=" + Rotation.X + ", " +
                "rot_y=" + Rotation.Y + ", " +
                "rot_z=" + Rotation.Z + " " +
                "WHERE character_id=" + Id) == 1;
        }

        public override string ToString()
        {
            return Id + ": " + Name;
        }
    }
}