using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace Faolan.Web.Components.Pages
{
	public partial class GmConsole
	{
		private readonly Dictionary<uint, string> _cmds = [];
		private readonly Dictionary<uint, string> _msgs = [];
		private readonly Dictionary<uint, string> _tpMap = [];
		private readonly Dictionary<uint, string> _tpX = [];
		private readonly Dictionary<uint, string> _tpY = [];
		private readonly Dictionary<uint, string> _tpZ = [];
		private readonly Dictionary<uint, uint> _tpTarget = [];
		private IReadOnlyList<LiveSession>? _online;

		private bool _busy;
		private string _broadcast = "";
		private string? _notice;

		protected override async Task LoadAsync()
		{
			await Refresh();
		}

		private async Task Broadcast()
		{
			if (_online == null || string.IsNullOrWhiteSpace(_broadcast))
				return;

			_busy = true;
			var sent = 0;
			foreach (var s in _online)
			{
				if (s.CharacterId is { } id && await Live.SendSystemMessageAsync(id, _broadcast) != null)
					sent++;
			}

			_notice = $"Broadcast sent to {sent} player(s).";
			_broadcast = "";
			_busy = false;
		}

		private string Cmd(uint id)
		{
			return _cmds.TryGetValue(id, out var v)
				? v
				: "";
		}

		private async Task Gm(uint id, string command)
		{
			if (string.IsNullOrWhiteSpace(command))
				return;

			_busy = true;
			var res = await Live.RunGmCommandAsync(id, command);
			_notice = res ?? "Server unreachable.";
			if (_cmds.ContainsKey(id))
				_cmds[id] = "";
			await Refresh();
			_busy = false;
		}

		private string Msg(uint id)
		{
			return _msgs.TryGetValue(id, out var v)
				? v
				: "";
		}

		private List<LiveSession> Others(uint id)
		{
			return _online?.Where(o => o.CharacterId != null && o.CharacterId != id).ToList() ?? [];
		}

		private async Task Refresh()
		{
			_online = await Live.GetSessionsAsync();
		}

		private async Task TeleportToCoords(uint srcId)
		{
			uint? map = uint.TryParse(Tp(_tpMap, srcId), out var mv)
				? mv
				: null;

			var (x, y, z) = (Parse(Tp(_tpX, srcId)), Parse(Tp(_tpY, srcId)), Parse(Tp(_tpZ, srcId)));

			// Need either a map or a full coordinate triple.
			if (map == null && (x == null || y == null || z == null))
			{
				_notice = "Enter a map id, or a full x/y/z (and optionally a map).";
				return;
			}

			_busy = true;
			_notice = await Live.TeleportAsync(srcId, map, x, y, z) ?? "Server unreachable.";
			await Refresh();
			_busy = false;
			return;

			float? Parse(string s)
			{
				return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)
					? f
					: null;
			}
		}

		private async Task TeleportToPlayer(uint srcId)
		{
			var targetId = TpTarget(srcId);
			if (targetId == 0)
				return;

			_busy = true;
			var live = await Live.GetCharacterLiveAsync(targetId);
			if (live == null)
				_notice = "Couldn't read the target's live position (not connected?).";
			else
				_notice = await Live.TeleportAsync(srcId, live.MapId, live.X, live.Y, live.Z) ?? "Server unreachable.";
			await Refresh();
			_busy = false;
		}

		private uint TpTarget(uint id)
		{
			return _tpTarget.TryGetValue(id, out var v)
				? v
				: 0;
		}

		private async Task Whisper(uint id)
		{
			_busy = true;
			var res = await Live.SendSystemMessageAsync(id, Msg(id));
			_notice = res ?? "Server unreachable.";
			_msgs[id] = "";
			_busy = false;
		}

		private static string Tp(Dictionary<uint, string> d, uint id)
		{
			return d.TryGetValue(id, out var v)
				? v
				: "";
		}
	}
}
