using System;
using System.Threading.Tasks;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Dialogue;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Progression;
using Faolan.GameServer.Combat;
using Faolan.GameServer.World;
using Faolan.GameServer;
using Faolan.GameServer.Progression;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Scripting
{
	// The achievement VISTA (#780) - using a viewpoint object plays a client-side camera fly-through and awards an
	// achievement tag. #740 decoded the whole chain off one 50-second window; this is the wiring, and every record it
	// sends is one we already build.
	//
	// Retail's order, and ours:
	//
	//   1. c2s `BccIIR UseDynel` on the object, and our ordinary ack.
	//   2. s2c `SimpleDynelVisualChangeIIR` `object_opened` - the lid.
	//   3. s2c `BccIIR ObjectInteraction` - the player's five-second `social_item_accept_2` gesture, carrying the
	//      `GenericAchievementVista` feedback remoteformat.
	//   4. …that gesture's blend later: the casterless scene buff, then `GameActionListIIR` 0xD00C, whose params name
	//      the camera path, its duration and the object to attach to.
	//   5. the CLIENT answers `CinematicTransition …01` and runs the camera itself; the lid shuts behind it.
	//   6. the client answers `CinematicTransition …00` when it is out. Only THEN does the server close the scene:
	//      `ScryScriptIIR "EndPath"`, `GameActionListIIR` 0xCFEF, the buff's END twin and `DispatchAddTag`.
	//
	// ⚠️ **Nothing here is on a timer except the gesture.** `PathDuration` is told to the client and never used by us:
	// the scene ends when the client says it has left the cinematic, which is why a player who skips out early is
	// handled by construction. A client that never reports leaving simply never gets the tag, which is also retail -
	// there is no server-side fallback in the capture.
	public sealed class VistaService(TagService tags, ILogger logger)
	{
		private readonly TagService _tags = tags;
		private readonly ILogger _logger = logger;

		// A player used a vista object. Returns false when it is not one, so the UseObject switch falls through to the
		// harvest / door / loot paths exactly as before.
		public async Task<bool> TryStartAchievementVista(INetworkClient client, LiveWorldObject live)
		{
			if (VistaCatalog.VistaFor(live.Row.DisplayCategory, live.Row.DisplayId) is not { } vista)
				return false;

			var player = client.Session.Character;
			var typeTag = WorldObjectColumns.TypeTagForClass(live.SubOpcode);

			// The lid, then the gesture. Both are addressed to the OBJECT, not the player - the object's own class
			// word, like every other world-object record (#366).
			await WorldIir.Send(client, SimpleDynelVisualChangeCodec.Build(SimpleDynelVisualChanges.SetBaseAnim(typeTag, live.Handle, VistaOpenedAnimation)));
			await WorldIir.Send(client,
				BehaviorControlCenterCodec.Build(BehaviorAnimations.PlayerViewVista(player.Id, live.Handle, BehaviorAnimations.VistaRemoteformat(vista.ObjectActionIndex))));

			client.Session.ActiveVista = new ActiveVistaScene(vista, live.Handle, typeTag);
			_logger.LogInformation("Vista: {Player} is viewing '{Vista}' 0x{Handle:X8} (path {Path}, {Duration}s).", player.Name, vista.Name, live.Handle, vista.CameraPath,
				vista.PathDurationSeconds);

			// The camera starts when the gesture ends - retail's own 5.04 s gap, which is that animation's blend.
			// Fire-and-forget with a session check, the same shape as the ground-AoE channel: the client is not
			// waiting on a reply, it is waiting on the animation.
			_ = Task.Run(async () =>
			{
				try
				{
					await Task.Delay(TimeSpan.FromSeconds(BehaviorAnimations.VistaGestureSeconds));
					if (client.Session.Account == null || client.Session.ActiveVista is not { } scene || scene.ObjectHandle != live.Handle)
						return; // disconnected, or the player used something else in the meantime

					await SendVistaCameraStart(client, scene);
				}
				catch (Exception ex)
				{
					_logger.LogWarning(ex, "Vista: starting the camera path for '{Vista}' failed.", vista.Name);
				}
			});

			return true;
		}

		// The two records that actually start the fly-through: the scene buff (casterless - retail writes a zero class
		// word where the caster reference would be) and the client-side scry script.
		private static async Task SendVistaCameraStart(INetworkClient client, ActiveVistaScene scene)
		{
			var self = client.Session.Character.Id;
			var vista = scene.Vista;

			await WorldIir.Send(client,
				MagicBuffCodec.Build(new MagicBuffIIR(self, 0x00, vista.BuffSpellBaseId, MagicBuffIds.FormApply, null,
					MagicBuffDataCodec.Build(MagicBuffDatas.ApplyNowNoCaster(vista.BuffSpellBaseId, vista.BuffDurationCentiseconds, BuffService.CasterLevel(client))))));

			var start = VistaScryActions.Start(vista.ScryScriptId, vista.CameraPath, vista.PathDurationSeconds, scene.ObjectClass, scene.ObjectHandle);
			await SendVistaAction(client, start);
		}

		// The client has ENTERED the cinematic. Retail answers with the lid shutting behind the camera and nothing
		// else - the fly-through is the client's own from here.
		public static async Task OnVistaCinematicEntered(INetworkClient client, ActiveVistaScene scene)
		{
			await WorldIir.Send(client, SimpleDynelVisualChangeCodec.Build(SimpleDynelVisualChanges.SetBaseAnim(scene.ObjectClass, scene.ObjectHandle, VistaClosedAnimation)));
		}

		// The client has LEFT the cinematic: close the scene and award the achievement, in retail's order.
		public async Task OnVistaCinematicLeft(INetworkClient client, ActiveVistaScene scene)
		{
			var self = client.Session.Character.Id;
			var vista = scene.Vista;
			client.Session.ActiveVista = null;

			await WorldIir.Send(client, ScryScriptCodec.Build(self, VistaEndPathStep));
			await SendVistaAction(client, VistaScryActions.Stop(vista.ScryScriptId));

			// The buff's END twin: same spell id with the END form selector and no data region at all.
			await WorldIir.Send(client, MagicBuffCodec.Build(new MagicBuffIIR(self, 0x00, vista.BuffSpellBaseId, MagicBuffIds.FormEnd, null, [])));

			var (granted, achievement) = await _tags.GrantTag(client, vista.AchievementTagId);
			_logger.LogInformation("Vista: {Player} finished '{Vista}'; achievement {Tag} ({Name}) {Outcome}.", client.Session.Character.Name, vista.Name, vista.AchievementTagId,
				achievement?.Name ?? "?", granted
					? "awarded"
					: "already held");
		}

		// One vista camera action, wrapped in the GameActionListIIR the client reads them out of. `field` is 0 and the
		// element's seed is the template-loaded one: retail's vista is world CONTENT, not a constructed action, which
		// is the opposite of the travel trip's `GameActionInstanceId` (see VistaScryActionCodec).
		private static Task SendVistaAction(INetworkClient client, VistaScryAction action)
		{
			return WorldIir.Send(client, GameActionListCodec.Build(new GameActionListIIR(client.Session.Character.Id, action.TypeId, 0, VistaScryActionCodec.Build(action))));
		}

		// The base animations the object plays either side of the scene. Named by retail, not by us.
		private const string VistaOpenedAnimation = "object_opened";
		private const string VistaClosedAnimation = "object_closed";

		// The scry step retail sends once the client reports it is out of the cinematic.
		private const string VistaEndPathStep = "EndPath";
	}
}
