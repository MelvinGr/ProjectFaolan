using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.JSInterop;

namespace Faolan.Web.Components.Pages
{
	public partial class MapRadar
	{
		private IJSObjectReference? _module;
		private List<(uint Id, string Name)> _maps = [];

		private uint MapId { get; set; } = 4010; // Tortage Island.

		public async ValueTask DisposeAsync()
		{
			try
			{
				if (_module != null)
				{
					await _module.InvokeVoidAsync("stop");
					await _module.DisposeAsync();
				}
			}
			catch
			{
				/* circuit already gone */
			}
		}

		protected override async Task OnAfterRenderAsync(bool firstRender)
		{
			if (firstRender && Authorized)
			{
				_module = await JS.InvokeAsync<IJSObjectReference>("import", "./js/radar.js");
				await _module.InvokeVoidAsync("start", "radar-canvas", MapId);
			}
		}

		protected override async Task LoadAsync()
		{
			await using var db = await DbFactory.CreateDbContextAsync();
			_maps = (await db
					.Maps.OrderBy(m => m.Id)
					.Select(m => new
					{
						m.Id,
						m.Name
					})
					.ToListAsync())
				.Select(m => (m.Id, m.Name))
				.ToList();

			// Prefer a map a player is actually on, so the radar opens on something live.
			var sessions = await Live.GetSessionsAsync();
			var liveCharId = sessions?.FirstOrDefault(s => s.CharacterId != null)?.CharacterId;
			if (liveCharId is { } cid)
			{
				var c = await db.Characters.FirstOrDefaultAsync(x => x.Id == cid);
				if (c != null)
					MapId = c.MapId;
			}
		}

		private async Task OnMapChanged()
		{
			if (_module != null)
				await _module.InvokeVoidAsync("setMap", MapId);
		}
	}
}
