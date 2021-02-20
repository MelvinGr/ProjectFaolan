using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Faolan.Core.Enums;

namespace Faolan.Core.Data
{
    public class Account : BaseObject
    {
        public const string AuthChallenge = "2bac5cd78ee0e5a37395991bfbeeeab8";

        [MaxLength(128)]
        public string UserName { get; set; }

        [MaxLength(128)]
        public string Password { get; set; }

        public uint AuthStatus { get; set; }
        public uint ClientInstance { get; set; } // use this as current character id
        public int CreateCounter { get; set; }
        public int CreateState { get; set; }
        public AccountState State { get; set; }
        public AccountType Type { get; set; }
        public DateTime? LastConnection { get; set; }
        public DateTime CreationDate { get; set; }

        [MaxLength(45)] // INET6_ADDRSTRLEN 46
        public string LastIpAddress { get; set; }

        [NotMapped]
        public ulong LongId => 0x0000271200000000u + Id; // As used by the client

        public virtual List<Character> Characters { get; set; }

        public override string ToString()
        {
            return $"{base.ToString()}: {UserName}";
        }
    }
}