using System.Collections.Immutable;

namespace Faolan.Core.Progression
{
	/// <summary>
	///     The achievement vista: a world object the player uses to play a client-side camera fly-through and be
	///     awarded an achievement tag.
	/// </summary>
	/// <remarks>
	///     The client names the mechanism itself - the object-action feedback carries
	///     <c>startobjectaction(7)|interaction(2)|GenericAchievementVista</c> - so retail has a whole content pattern
	///     for "interact with a viewpoint, watch the fly-through, get the achievement", and the achievement catalog is
	///     full of the places it applies (711 "Conan's Tarantia", 712 "Avenue of Roses", ...).
	///     <para>
	///         Exactly one instance is captured, and the rest are not derivable. Every value below is read off that
	///         one 50-second window and nothing generalises it: the camera path name, its duration, the scry script,
	///         the cinematic buff and the tag are per-object content, and the corpus holds one of each. A second row
	///         means a second capture, not an extrapolation - which is why this is a catalog of measured rows rather
	///         than a rule, in the shape <see cref="HarvestCatalog" /> carries its one proven feat gate.
	///         [V2: NecroL80_mage-necromancer_login_gameplay_annotated_2.tshark.json frames 4021-4299]
	///     </para>
	///     <para>
	///         Keyed by the object's LDB <see cref="DisplayCategory" /> display id, the same content identity
	///         <see cref="HarvestCatalog" /> keys nodes by: a world object's own <c>Id</c> is derived from its map,
	///         position and display reference, so the display id is the part of it the client states.
	///     </para>
	/// </remarks>
	public static class VistaCatalog
	{
		/// <summary>The LDB category a world object's displayed name resolves through - the dynel/object name space.</summary>
		public const uint DisplayCategory = 55000;

		/// <summary>
		///     Every vista we hold measured data for. One row, and the gap is the point - see the class remarks.
		/// </summary>
		public static readonly ImmutableDictionary<uint, AchievementVista> Vistas = new AchievementVista[]
		{
			// Old Tarantia Docks (map 2000, the object at 676.58 / 92.69 / 655.83). The three RDB ids sit in one
			// contiguous content block with the display string - script 4998682, buff 4998650, display 4998751 - which
			// is what a second vista's block would look like too.
			new(4998751, "Old Tarantia Docks", 0x004C461A, "Vista_Path_6", 36, 0x004C45FA, 4000, 716, 7)
		}.ToImmutableDictionary(v => v.DisplayId);

		/// <summary>
		///     The vista a world object is, or null when it is not one. Both halves of the display reference are
		///     required for the same reason <see cref="HarvestCatalog.NodeFor" /> demands them: an LDB id is only
		///     unique within its category.
		/// </summary>
		public static AchievementVista? VistaFor(uint? displayCategory, uint? displayId) =>
			displayCategory == DisplayCategory && displayId is { } id && Vistas.TryGetValue(id, out var vista) ? vista : null;
	}

	/// <summary>One achievement vista, as the capture states it.</summary>
	/// <param name="DisplayId">The LDB <see cref="VistaCatalog.DisplayCategory" /> id the object displays - the key.</param>
	/// <param name="Name">That string, for logs and documentation.</param>
	/// <param name="ScryScriptId">
	///     The RDB scry script the client runs, carried as the <c>ScriptID</c> of both the start and the stop
	///     GameAction.
	/// </param>
	/// <param name="CameraPath">
	///     The camera path's name - <c>PathName</c> in the action's params. Camera paths are playfield-scoped (RDB
	///     type 1000047, keyed by playfield id), so a path name only resolves while the client is on this object's
	///     map.
	/// </param>
	/// <param name="PathDurationSeconds">
	///     <c>PathDuration</c> in the params: how long the fly-through runs. Informational to us - the end of the
	///     scene is driven by the client's own <c>CinematicTransition</c>, not by a server timer.
	/// </param>
	/// <param name="BuffSpellBaseId">The casterless buff retail applies for the duration of the scene.</param>
	/// <param name="BuffDurationCentiseconds">That buff's duration - 40 s against a 36 s path.</param>
	/// <param name="AchievementTagId">The tag <c>DispatchAddTag</c> awards when the scene ends (an <c>Achievements.Id</c>).</param>
	/// <param name="ObjectActionIndex">
	///     The object-action slot the feedback remoteformat names (<c>startobjectaction(7)</c>, echoed as the record's
	///     one string parameter). Per-object, like everything else here.
	/// </param>
	public sealed record AchievementVista(uint DisplayId, string Name, uint ScryScriptId, string CameraPath, uint PathDurationSeconds, uint BuffSpellBaseId,
		uint BuffDurationCentiseconds, uint AchievementTagId, uint ObjectActionIndex);
}
