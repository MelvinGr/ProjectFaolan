using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Web.Components.Pages
{
	public partial class Status
	{
		private DateTimeOffset? _lastLogin;
		private IReadOnlyList<LiveSession>? _online; // Null when the game server is unreachable, that is, offline.
		private List<ClassRow> _classes = [];

		private List<RecentRow> _recent = [];
		private bool _loaded;
		private int _accounts;
		private int _chars;
		private int _classMax;
		private int _maps;
		private int _recent24h;
		private long _items;
		private long _spells;

		protected override async Task OnInitializedAsync()
		{
			await using var db = await DbFactory.CreateDbContextAsync();

			_accounts = await db.Accounts.CountAsync();
			_items = await db.Items.LongCountAsync();
			_maps = await db.Maps.CountAsync();
			_spells = await db.Spells.LongCountAsync();
			_lastLogin = await db.Accounts.MaxAsync(a => a.LastConnection);

			// The roster is small and SQLite cannot translate DateTimeOffset comparisons or ordering, so the characters
			// are pulled into memory and the recent list and class distribution computed here.
			var chars = await db.Characters.Where(c => c.Name != null && c.Name != "").Include(c => c.Map).ToListAsync();
			_chars = chars.Count;

			var dayAgo = DateTimeOffset.UtcNow.AddDays(-1);
			_recent24h = chars.Count(c => c.LastConnection is { } lc && lc >= dayAgo);

			_recent = chars
				.OrderByDescending(c => c.LastConnection ?? DateTimeOffset.MinValue)
				.Take(12)
				.Select(c => new RecentRow(c.Id, c.Name ?? "", c.Level, c.Class.ToString(), c.Map?.Name ?? "—", c.LastConnection))
				.ToList();

			_classes = chars.GroupBy(c => c.Class).Select(g => new ClassRow(g.Key.ToString(), g.Count())).OrderByDescending(x => x.Count).ToList();
			_classMax = _classes.Count == 0
				? 0
				: _classes.Max(c => c.Count);

			// Live presence from the running game server's MCP endpoint; null when it is offline.
			_online = await Live.GetSessionsAsync();

			_loaded = true;
		}

		private static string Ago(DateTimeOffset? when)
		{
			if (when == null)
				return "never";

			var d = DateTimeOffset.UtcNow - when.Value.ToUniversalTime();
			if (d.TotalMinutes < 1)
				return "just now";
			if (d.TotalMinutes < 60)
				return $"{(int)d.TotalMinutes}m ago";
			if (d.TotalHours < 24)
				return $"{(int)d.TotalHours}h ago";
			if (d.TotalDays < 30)
				return $"{(int)d.TotalDays}d ago";

			return when.Value.ToString("yyyy-MM-dd");
		}

		private record RecentRow(uint Id, string Name, ushort Level, string Class, string Map, DateTimeOffset? LastConnection);

		private record ClassRow(string Class, int Count);
	}
}
