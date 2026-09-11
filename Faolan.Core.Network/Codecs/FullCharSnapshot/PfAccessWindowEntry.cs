using System.Collections.Generic;
using System.Linq;

namespace Faolan.Core.Network.Codecs.FullCharSnapshot
{
	/// <summary>
	///     One <c>PFAccessWindow_</c> playfield record out of the FullChar snapshot tails (see
	///     <see cref="Constants.AccessWindowCatalog" />).
	/// </summary>
	/// <remarks>
	///     Carved to the client's grammar: the table is <c>PlayfieldAccessHolder</c>'s count-framed list
	///     (<c>CoreCharacter\PlayfieldAccessHolder.cpp</c>, reader <c>FUN_0077d220</c>/<c>FUN_0077db70</c>). The
	///     in-memory record the holder stores is
	///     <c>{ bool@+0x00, u32@+0x04, u32@+0x08, bool@+0x0c, string@+0x10, children@+0x2c }</c> - its inserter
	///     <c>FUN_0077e9a0</c> copies exactly those - which is this class plus one extra bool at <c>+0x0c</c> that is not
	///     on the wire: a client-side "already warned" latch, see <see cref="SecondsRemaining" />.
	/// </remarks>
	public sealed class PfAccessWindowEntry
	{
		public uint PlayfieldId { get; init; }

		// Whether entering this playfield costs a teleport coin. A boolean on the client, whose reader normalises `!= 0`;
		// 0 or 1 on all 86 records.
		//
		// Named by the client's own refusal, three ways over: the access window's opener `FUN_014bd250` branches on this byte
		// (node `+0x10`) and, when it is set, queries token pool 0x32 = 50 - which RDB 1000520 names `TeleportCoins`
		// (`TokenPool`, recovered independently) - and refuses to open the window when the player holds none, showing LDB-100
		// `PlayfieldAccessNoTokens`:
		//
		//     "You do not have enough Teleport Coins to travel. Purchase more from the Item Shop."
		//
		// and its sibling string in the same GUI table, LDB-520 `RequiresToken`, is the confirmation prompt:
		//
		//     "This will cost 1 Teleport Coin."
		//
		// The field is therefore the client's `RequiresToken`. Retail's catalog carries 1 on every one of the 86 records,
		// which is not the same claim as "every playfield costs a coin": the window is the fast-travel list, and this is the
		// flag that puts the price on the entry.
		public byte RequiresToken { get; init; }

		// The access type, named from the client's own checker: `PlayfieldAccessHolder`'s `FUN_0077e200` looks the playfield
		// up in this list's map and switches on this word (the stored pair's +0x14): 0 and 2 pass, 1 and 3 log "Access
		// DENIED" (channel GameCode/PlayfieldAccess), and anything else asserts "Invalid access type %u for playfield %u".
		// Its UI caller (`FUN_014bd250`) opens the `playfield_access_window` off the same check. The catalog carries 2 on
		// every base-game playfield and 3 on the Khitai / Dragon's Spine ones, so this is the captured account's entitlement
		// state rather than immutable game data.
		public uint AccessType { get; init; }

		// How much longer this timed access lasts, in seconds. 0 in all 86 catalog records, the captured account having held
		// no timed playfield access.
		//
		// It reaches the node as the pair's third data word (`+0x18`), and neither the access checker `FUN_0077e200` nor the
		// UI opener `FUN_014bd250` touches it. Its consumer is the holder's own update slot `FUN_0113a390`, reached from a
		// callback table rather than a direct call, which is
		//
		//     if (!warned && remaining <= TWK_PlayfieldAccess_WarningTimer && remaining > 0 && here)
		//             warn(playfieldId, remaining); warned = true;
		//     if (warned && remaining > TWK_PlayfieldAccess_WarningTimer) warned = false;   // re-arm
		//
		// against `DAT_02d24664`, that tweak's own value slot (slot+8 of `0x02d2465c`), which reads 300. The warning it fires
		// is LDB-100 `EndOfPlayfieldAccessWarning`, "You will lose access to %s in %d minutes", and the formatter divides the
		// value by 60. A 300-value warning that prints as 5 minutes settles the unit: seconds.
		//
		// The `bool warned` the loop latches is the source record's `+0x0c`, and it is client-side state - not on the wire,
		// and with no field for it here.
		//
		// The sibling producer agrees: `DispatchPlayfieldAccessGranted(playfieldId, hours, allCharacters)` is how retail
		// grants one of these, so a grant states hours and the entry counts down in seconds. The same per-entry record
		// travels alone on `DispatchSendPlayfieldAccessData(playfieldId, accessInfo)`, whose handler `FUN_004cdb10` calls
		// `FUN_0077db70` - the very reader this table's own reader calls per element - which is what says the snapshot entry
		// and `accessInfo` are one type.
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

	/// <summary>
	///     One child element of a <see cref="PfAccessWindowEntry" />:
	///     <c>[u8 flag][u32 id][str displayName][str parentName]</c> on the wire. A flag-0 child carries a bare id and two
	///     empty strings; a flag-1 child names a window and its parent window.
	/// </summary>
	public sealed class PfAccessWindowChild
	{
		// A boolean on the client: 0 is id-only, 1 a named window, on every captured child.
		public byte Flag { get; init; }

		// One window id, running 5.03M to 5.06M across the catalog.
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
