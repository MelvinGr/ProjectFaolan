namespace Faolan.GameServer.World
{
	/// <summary>
	///     The realm's game clock as the GM commands can bend it.
	/// </summary>
	/// <remarks>
	///     The clock itself runs off the wall clock (<c>GameTimeCodec</c>); these two fields are what <c>.time</c> and
	///     <c>/gametime</c> move, so a tester can jump to dusk without waiting out a 4.8 h game day, and they are read
	///     by every GameTime send - world entry's and the GM replies alike. Its own class because two owners read it:
	///     the world-entry service builds the entry's GameTime record from it, and the listener's GM commands write it.
	/// </remarks>
	public sealed class RealmClock
	{
		/// <summary>Phase shift, in game seconds.</summary>
		public long OffsetGameSeconds { get; set; }

		/// <summary>Speed multiplier, sent on the wire.</summary>
		public float Rate { get; set; } = 1.0f;
	}
}
