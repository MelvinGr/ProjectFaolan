namespace LibFaolan.Data.Enums
{
    public enum AccountState : byte
    {
        Banned,
        Unactivated,
        Active
    }

    public enum AccountType : byte
    {
        Trial,
        Normal,
        GameMaster,
        Administrator
    }
}