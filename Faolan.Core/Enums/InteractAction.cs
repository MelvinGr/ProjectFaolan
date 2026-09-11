namespace Faolan.Core.Enums
{
	// What a usable world object does when the player uses it (BCC UseObject / startobjectaction 18) - stored on the
	// WorldObject row so the interaction is data-driven, not inferred from the render subopcode (a DoorFullUpdateIIR
	// 0x33A56FB0 is a zone-line teleporter and the class of "Lever"-style interactables; a SimpleDynel 0x66AEDD50 is
	// both inert decor and animated doors). The client models this as a GameAction on the object's RDB template - a
	// zone-line runs a TeleportGameAction (same playfield) or ProxyTeleportGameAction (cross playfield, carrying a
	// PlayfieldProxy of model-type + instance, the same proxy our N3Teleport/IntendedTeleport wire uses).
	//
	// InteractAction is a server-side DB discriminator, not a wire value: its only consumer is the two `case`
	// labels in GameServerListener.Inbound (both teleport actions take the same branch; same-map vs cross-map is
	// decided from dest.DestMapId == currentMap, not the enum id). So the numeric values are arbitrary and never
	// reach the client - kept as small placeholders so a change never needs a WorldObject.InteractAction data
	// migration.
	//
	// The client's real GameAction type-ids (Ghidra descriptor table FUN_008bdf40, docs/re/gameaction_types.json):
	// Teleport = e_Type_GC_GameAction_Teleport 0xCF45 (keyList ..0x2F1 TeleportDest), ProxyTeleport = 0xD04D (6
	// params, cross-playfield, "playfield > 65535 needs proxy"), plus the family LineTeleport 0xCFF1 /
	// ProxyTeleportLine 0xD051 / TeleportToBindLocation 0xCF1D / BindAtLocation 0xCF19. (ProxyTeleport is absent
	// from the 68-type JSON only because the dump scanned 0xCF09..0xD026 and it sits past the end.) These would only
	// matter if a real GameAction were emitted for a zone-line, which none is.
	public enum InteractAction : uint
	{
		// No interactive action driven by this column: decor, or a door whose behaviour is handled by the subop-based
		// path (SimpleDynel animated-open / loot-bag window) or an interactable that is not modelled (a lever).
		None = 0,

		// TeleportGameAction: reposition the player on the same playfield (no loading screen).
		Teleport = 1,

		// ProxyTeleportGameAction: hand the player off to another playfield - our cross-map zone-lines.
		ProxyTeleport = 2
	}
}
