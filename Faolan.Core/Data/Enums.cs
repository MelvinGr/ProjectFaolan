using System;

namespace Faolan.Core.Data
{
    [Flags]
    public enum AccountState : byte
    {
        Banned,
        Unactivated,
        Active
    }

    [Flags]
    public enum AccountType : byte
    {
        Trial,
        Normal,
        GameMaster,
        Administrator
    }
}