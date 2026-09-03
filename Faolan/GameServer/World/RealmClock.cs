namespace Faolan.GameServer.World
{
	// The realm's game clock as the GM commands can bend it (#351 / #914). The clock itself runs off the wall clock
	// (GameTimeCodec); these two are what `.time` and `/gametime` move so a tester can jump to dusk without waiting
	// out a 4.8 h game day, and they are read by every GameTime send - world entry's and the GM replies alike.
	//
	// Its own class because two owners read it: the world-entry service builds the entry's GameTime record from it
	// and the listener's GM commands write it. Keeping the two fields on the listener made the entry a listener
	// method, which is the cycle #826 was about.
	public sealed class RealmClock
	{
		// Phase shift, in game seconds.
		public long OffsetGameSeconds { get; set; }

		// Speed multiplier, sent on the wire.
		public float Rate { get; set; } = 1.0f;
	}
}
