namespace Faolan.Core.Enums
{
	public enum InvalidLoginReasons : byte
	{
		InternalSystemError = 0x04,
		InvalidLogin = 0x0e,
		AccountFrozen = 0x17
	}
}
