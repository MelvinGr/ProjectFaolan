using System;
using System.Collections.Generic;
using System.Linq;
using LibFaolan.Data.Enums;
using LibFaolan.Database;
using LibFaolan.Extentions;
using LibFaolan.Network;

namespace LibFaolan.Data
{
    public sealed class Account
    {
        public static readonly string AuthChallenge = "2bac5cd78ee0e5a37395991bfbeeeab8";
        public uint AuthStatus;
        public Character Character;
        public uint ClientInstance;
        public int CreateCounter;
        public int CreateState;
        public uint Id; // PlayerInstance
        public string Name;
        public AccountState State;
        public AccountType Type;
        public ulong LongId => 0x0000271200000000u + Id; // As used by the client (why?)

        public void LoadDetailsFromDatabase(IDatabase database)
        {
            Dictionary<string, dynamic> obj;
            if (Id != 0)
                obj = database.ExecuteReader("SELECT * FROM accounts WHERE account_id=" + Id).ToDictionary();
            else if (Name != null)
                obj = database.ExecuteReader("SELECT * FROM accounts WHERE username='" + Name + "'").ToDictionary();
            else
                throw new Exception("Id == 0 && Username == null");

            if (obj.Count == 0)
                throw new Exception("obj.Count == 0");

            Id = (uint) obj["account_id"];
            Name = obj["username"];
            Type = (AccountType) obj["type"];
            State = (AccountState) obj["state"];
        }

        public bool CheckLogin(IDatabase database, string password)
        {
            return database.ExecuteScalar<object>("SELECT account_id FROM accounts WHERE username = '" + Name +
                                                  "' AND password = '" + password + "'") != null;
        }

        public bool IsBanned(IDatabase database)
        {
            return database.ExecuteScalar<dynamic>("SELECT state FROM accounts WHERE account_id=" + Id) == 1;
        }

        public Character[] GetCharacters(IDatabase database)
        {
            var chars = database.ExecuteReader("SELECT character_id FROM characters WHERE account_id=" + Id)
                .ToIEnumerable().Select(c => new Character((uint) c["character_id"])).ToArray();

            foreach (var c in chars)
                c.LoadDetailsFromDatabase(database);

            return chars;
        }

        public bool UpdateLastInfo(IDatabase database, NetworkClient client)
        {
            var t2 = 0;
            DllImport.Other.time(ref t2);

            return database.ExecuteNonQuery("UPDATE accounts SET " +
                                            "last_connection=" + t2 +
                                            ", last_ip='" + client.IpAddress + "'" +
                                            "WHERE account_id=" + Id) == 1;
        }

        public override string ToString()
        {
            return Id + ": " + Name;
        }
    }
}