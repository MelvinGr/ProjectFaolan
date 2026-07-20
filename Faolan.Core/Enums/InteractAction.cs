namespace Faolan.Core.Enums
{
	// What a usable world object DOES when the player uses it (BCC UseObject / startobjectaction 18) - stored on the
	// WorldObject row so the interaction is data-driven, not inferred from the render subopcode (a DoorFullUpdateIIR
	// 0x33A56FB0 is a zone-line teleporter AND the class of "Lever"-style interactables; a SimpleDynel 0x66AEDD50 is
	// both inert decor AND animated doors). The client models this as a GameAction on the object's RDB template - a
	// zone-line runs a TeleportGameAction (same playfield) or ProxyTeleportGameAction (cross playfield, carrying a
	// PlayfieldProxy of model-type + instance, the same proxy our N3Teleport/IntendedTeleport wire uses).
	//
	// NB InteractAction is a SERVER-SIDE DB discriminator, NOT a wire value: its only consumer is the two `case`
	// labels in GameServerListener.Inbound (both teleport actions take the same branch; same-map vs cross-map is
	// decided from dest.DestMapId == currentMap, not the enum id). So the numeric values are arbitrary and never
	// reach the client - kept as small placeholders to avoid a WorldObject.InteractAction data migration.
	//
	// The client's REAL GameAction type-ids were recovered (#338, Ghidra descriptor table FUN_008bdf40, verified
	// against docs/re/gameaction_types.json): Teleport = e_Type_GC_GameAction_Teleport 0xCF45 (keyList ..0x2F1
	// TeleportDest), ProxyTeleport = 0xD04D (6 params, cross-playfield, "playfield > 65535 needs proxy"), plus the
	// family LineTeleport 0xCFF1 / ProxyTeleportLine 0xD051 / TeleportToBindLocation 0xCF1D / BindAtLocation 0xCF19.
	// (ProxyTeleport was absent from the 68-type JSON only because the dump scanned 0xCF09..0xD026 and it sits past
	// the end.) These would only matter if we ever emitted a real GameAction for a zone-line; today we don't.
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
