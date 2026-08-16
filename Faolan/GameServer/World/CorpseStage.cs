namespace Faolan.GameServer.World
{
	// The stages a killed NPC's corpse passes through, in order, driven by WorldEntity.TickCorpse. Ordered so a
	// comparison ("has this death already got past Fade?") is the natural test - the sequence never runs backwards
	// within one death, and a revive resets it to None.
	//
	// The timings are retail's, measured over 128 NPC deaths in the capture corpus; see WorldEntity.CorpseFadeSeconds
	// for the numbers and what they overturned (the bag used to BE the corpse, three seconds later).
	public enum CorpseStage
	{
		None = 0, // alive, or dead but not yet ticked
		Drop, // the loot bag has been spawned beside the body - immediately, in retail's case in the death's own burst
		Fade, // the body has been faded out for viewers (n3ToClientFade, a 2.0 s dissolve) ~30.8 s after the kill
		Quit, // ...and its handle dropped (n3ToClientQuit) 2.5 s after that; nothing renders the corpse any more
	}
}
