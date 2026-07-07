namespace Faolan.Core.Enums
{
    public enum ContainerKind : uint
    {
        Equipped = 101,
        Bag = 104,
        Bank = 105, // personal bank storage - delivered on-demand via BankIIR (protocol_bank_storage), same codec
        Resource = 112
    }
}
