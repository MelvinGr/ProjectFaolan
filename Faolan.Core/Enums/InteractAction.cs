namespace Faolan.Core.Enums
{
	// What a usable world object DOES when the player uses it (BCC UseObject / startobjectaction 18) - stored on the
	// WorldObject row so the interaction is data-driven, not inferred from the render subopcode (a DoorFullUpdateIIR
	// 0x33A56FB0 is a zone-line teleporter AND the class of "Lever"-style interactables; a SimpleDynel 0x66AEDD50 is
	// both inert decor AND animated doors). The client models this as a GameAction on the object's RDB template - a
	// zone-line runs a TeleportGameAction (same playfield) or ProxyTeleportGameAction (cross playfield, carrying a
	// PlayfieldProxy of model-type + instance, the same proxy our N3Teleport/IntendedTeleport wire uses).
	//
	// NB the non-zero values here are PLACEHOLDERS. The client's real GameAction type ids are 0xcf09+idx slots
	// (docs/re/gameactionlist-grammar.md); TeleportGameAction/ProxyTeleportGameAction's exact 0xcfXX slot needs a
	// live-client dump to pin (the static Ghidra catalog's per-type keyLists dumped as zeros). Swap these for the real
	// slot ids once dumped - tracked in Gitea. The recovered client anchors are the stat keys the actions read:
	// e_GameAction_TeleportDest = 0x2F1 (Teleport), proxy model instance/dest = 0x469/0x46A (ProxyTeleport).
	public enum InteractAction : uint
	{
		// No interactive action driven by this column: decor, or a door whose behaviour is handled by the subop-based
		// path (SimpleDynel animated-open / loot-bag window) or an interactable we haven't modelled yet (a lever).
		None = 0,

		// TeleportGameAction: reposition the player on the SAME playfield (no loading screen).
		Teleport = 1,

		// ProxyTeleportGameAction: hand the player off to ANOTHER playfield - our cross-map zone-lines.
		ProxyTeleport = 2
	}
}
