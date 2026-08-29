using System.Collections.Generic;
using System.Linq;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	// One `PFAccessWindow_` playfield record out of the FullChar snapshot tails (see AccessWindowCatalog).
	//
	// ⭐ Re-carved to the CLIENT's grammar (#464): the table is `PlayfieldAccessHolder`'s ListFrame-framed list
	// (`CoreCharacter\PlayfieldAccessHolder.cpp`, reader `FUN_0077d220`/`FUN_0077db70`), and under that carve every
	// one of the old model's four `Unknown*` fields DISSOLVED:
	//   - `UnknownBeforeChildMode` (u16) + the name's "trailing NUL" + `ChildMode` were ONE big-endian u32 - the
	//     CHILD COUNT, whose high bytes are zero because no entry has more than 3 children;
	//   - `PfAccessWindowChild.Unknown` was the two u16 length words of a child's two EMPTY strings;
	//   - `PfAccessWindowChildDetail.DetailUnknown` + the 3-char "ShortCode" were one big-endian u32 window id;
	//   - the childMode 1/2/3 "variants", "detail modes" and both lookahead heuristics were artifacts of walking
	//     a plain `count × child` list through a shifted frame.
	// ⭐ And the last two - the head's bool and its u32 - are named as of #464: every field of the record now is.
	// The in-memory record the holder stores is `{ bool@+0x00, u32@+0x04, u32@+0x08, bool@+0x0c, string@+0x10,
	// children@+0x2c }` (its inserter `FUN_0077e9a0` copies exactly those), which is this class plus ONE extra
	// bool at `+0x0c` that is not on the wire: a client-side "already warned" latch, see SecondsRemaining.
	public sealed class PfAccessWindowEntry
	{
		public uint PlayfieldId { get; init; }

		// ⭐ Whether entering this playfield COSTS A TELEPORT COIN (#464). A boolean on the client (its reader
		// normalises `!= 0`); 0 or 1 on all 86 records.
		//
		// Named by the client's own refusal, three ways over: the access window's opener `FUN_014bd250` branches on
		// this byte (node `+0x10`) and, when it is set, queries token pool **0x32 = 50** - which RDB 1000520 names
		// `TeleportCoins` (`TokenPool`, recovered independently) - and refuses to open the window when the player
		// holds none, showing LDB-100 `PlayfieldAccessNoTokens`:
		//
		//     "You do not have enough Teleport Coins to travel. Purchase more from the Item Shop."
		//
		// and its sibling string in the same GUI table, LDB-520 `RequiresToken`, is the confirmation prompt:
		//
		//     "This will cost 1 Teleport Coin."
		//
		// So the field IS the client's `RequiresToken`. ⚠️ Retail's catalog carries 1 on every one of the 86
		// records, which is not the same claim as "every playfield costs a coin" - the window is the fast-travel
		// list, and this is the flag that puts the price on the entry.
		public byte RequiresToken { get; init; }

		// ⭐ The ACCESS TYPE - named from the client's own checker (#464): `PlayfieldAccessHolder`'s
		// `FUN_0077e200` looks the playfield up in this list's map and switches on this word (the stored pair's
		// +0x14): 0 and 2 pass, 1 and 3 log "Access DENIED" (channel GameCode/PlayfieldAccess), anything else
		// asserts "Invalid access type %u for playfield %u". Its UI caller (`FUN_014bd250`) opens the
		// `playfield_access_window` off the same check. The catalog carries 2 on every base-game playfield and 3
		// on the Khitai / Dragon's Spine ones - i.e. this is the captured ACCOUNT's entitlement state (it did not
		// own those expansions), not immutable game data; retail presumably serves 2s to an entitled account.
		public uint AccessType { get; init; }

		// ⭐ How much longer this TIMED access lasts, in SECONDS (#464). 0 in all 86 catalog records - the captured
		// account held no timed playfield access, which is why the field looked dead.
		//
		// It reached the node as the pair's third data word (`+0x18`) and neither the access checker
		// `FUN_0077e200` nor the UI opener `FUN_014bd250` touches it, which is what made it read as
		// stored-but-unconsumed. Its consumer is neither: it is the holder's own update slot `FUN_0113a390`
		// (reached from a callback table, not a direct call), which is exactly
		//
		//     if (!warned && remaining <= TWK_PlayfieldAccess_WarningTimer && remaining > 0 && here)
		//             warn(playfieldId, remaining); warned = true;
		//     if (warned && remaining > TWK_PlayfieldAccess_WarningTimer) warned = false;   // re-arm
		//
		// against `DAT_02d24664`, which is that tweak's own value slot (slot+8 of `0x02d2465c`) and reads **300**.
		// The warning it fires is LDB-100 `EndOfPlayfieldAccessWarning`, *"You will lose access to %s in %d
		// minutes"*, and the formatter divides the value by 60 (`0x88888889` / `SHR 5`, the unsigned divide-by-60).
		// A 300-value warning that prints as 5 minutes settles the UNIT: seconds, not minutes and not hours.
		//
		// The `bool warned` the loop latches is the source record's `+0x0c`, and it is client-side state - it is
		// not on the wire and this class has no field for it.
		//
		// The sibling PRODUCER agrees: `DispatchPlayfieldAccessGranted(playfieldId, hours, allCharacters)` is how
		// retail grants one of these, so a grant states HOURS and the entry counts down in seconds. The same
		// per-entry record travels alone on `DispatchSendPlayfieldAccessData(playfieldId, accessInfo)` - its
		// handler `FUN_004cdb10` calls `FUN_0077db70`, the very reader this table's own reader calls per element,
		// which is what says the snapshot entry and `accessInfo` are one type.
		public uint SecondsRemaining { get; init; }

		public string InternalName { get; init; } = null!;

		public List<PfAccessWindowChild> Children { get; init; } = [];

		public override string ToString()
		{
			var children = Children.Count == 0
				? ""
				: $" | children: {string.Join(", ", Children.Select(c => c.ToString()))}";

			return $"{PlayfieldId} {InternalName} requiresToken={RequiresToken} accessType={AccessType} secondsRemaining={SecondsRemaining}{children}";
		}
	}

	// One child element of a PfAccessWindowEntry: `[u8 flag][u32 id][str displayName][str parentName]` on the wire.
	// A flag-0 child carries a bare id and two empty strings; a flag-1 child names a window and its parent window.
	public sealed class PfAccessWindowChild
	{
		// A BOOLEAN on the client; 0 = id-only, 1 = named window, on every captured child.
		public byte Flag { get; init; }

		// The old model read this u32's bytes as `[u8 DetailUnknown][3-char ShortCode]` ("M2W"); it is one id
		// (5.03M-5.06M across the catalog).
		public uint Id { get; init; }

		public string DisplayName { get; init; } = "";
		public string ParentName { get; init; } = "";

		public override string ToString()
		{
			return DisplayName.Length == 0
				? $"[{Flag}] {Id}"
				: $"[{Flag}] {Id} {DisplayName} < {ParentName}";
		}
	}
}
