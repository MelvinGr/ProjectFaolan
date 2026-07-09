namespace Faolan.Core.Network.Interfaces
{
	public interface IPacketLogger
	{
		void Log(byte[] bytes);
	}

	public sealed class NullPacketLogger : IPacketLogger
	{
		public static readonly NullPacketLogger Instance = new();

		private NullPacketLogger()
		{
		}

		public void Log(byte[] bytes)
		{
		}
	}
}
