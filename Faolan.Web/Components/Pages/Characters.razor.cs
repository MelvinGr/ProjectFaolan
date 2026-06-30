using System.Security.Claims;
using Faolan.Core.Database.Tables;
using Faolan.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.JSInterop;

namespace Faolan.Web.Components.Pages
{
	public partial class Characters
	{
		// Carried items, split into Normal/Quest/Resources tabs and paged into the grid (AoC-style backpack: 4×6 per page).
		private const int InvPageSize = 24;
		private static readonly string[] InvTabs = ["Normal", "Quest", "Resources"];

		// Paper-doll equipment slots, arranged around the character preview (AoC layout; placeholders for now): Head sits
		// centred above and Legs centred below, with armour columns flanking and a weapon/utility bar across the bottom.
		private static readonly string[] LeftSlots = ["Neck", "Shoulder", "Cloak", "Chest", "Wrist"];
		private static readonly string[] RightSlots = ["Hands", "Belt", "Boots", "Ring L", "Ring R"];
		private static readonly string[] WeaponSlots = ["Main Hand", "Off Hand", "Ranged", "Tokens", "Bag"];
		private bool _refreshThumbs;
		private Character? _selected;
		private IJSObjectReference? _module;
		private int _invPage;
		private IReadOnlyDictionary<string, ItemInfo> _equipment = new Dictionary<string, ItemInfo>();
		private IReadOnlyList<(ItemInfo Info, string Tab)> _inventory = [];
		private IReadOnlyList<(string Name, byte Level, float Range, float Damage)> _spells = [];

		private List<Character>? _characters;
		private string _invTab = "Normal";
		private string _mapName = "—";
		private string _money = "0";
		private string _status = "";
		private string? _guildName;

		// Selected character's sheet: vitals (player-facing MaxStats) + a few resolved fields.
		private uint _health, _mana, _stamina;

		[SupplyParameterFromQuery(Name = "char")]
		public long? CharId { get; set; }

		private int InvPageCount => Math.Max(1, (int)Math.Ceiling(TabItems.Count / (double)InvPageSize));

		private List<(ItemInfo Info, string Tab)> TabItems => _inventory.Where(i => i.Tab == _invTab).ToList();

		[CascadingParameter]
		private Task<AuthenticationState>? AuthState { get; set; }

		protected override async Task OnAfterRenderAsync(bool firstRender)
		{
			if (firstRender)
			{
				_module = await JS.InvokeAsync<IJSObjectReference>("import", "./js/viewer.js");
				await _module.InvokeVoidAsync("initItemTooltips");

				// Deep-link: ?char=<id> auto-loads that character once the viewer module is ready.
				if (CharId is { } id && _characters?.FirstOrDefault(c => c.Id == id) is { } c)
					await Select(c);
			}

			// After an inventory page flip the new slots exist in the DOM — (re)render their item thumbnails.
			if (_refreshThumbs && _module != null)
			{
				_refreshThumbs = false;
				await _module.InvokeVoidAsync("populateItemThumbs");
			}
		}

		protected override async Task OnInitializedAsync()
		{
			await using var db = await DbFactory.CreateDbContextAsync();
			// Skip blank-name rows: unfinished/placeholder characters with nothing to show.
			var query = db.Characters.Where(c => c.Name != null && c.Name != "");

			// Public viewer: when signed in, scope the roster to this account's own characters; otherwise show all.
			if (AuthState != null)
			{
				var user = (await AuthState).User;
				if (user.Identity?.IsAuthenticated == true && uint.TryParse(user.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var accountId))
					query = query.Where(c => c.AccountId == accountId);
			}

			_characters = await query.OrderBy(c => c.Name).ToListAsync();
		}

		// Load the selected character's sheet data: vitals (MaxStats, regenerated from class/level if missing), the map
		// name and guild, and a formatted wallet.
		private async Task LoadSheet(Character c)
		{
			await using var db = await DbFactory.CreateDbContextAsync();
			var full = await db.Characters.Include(x => x.MaxStats).Include(x => x.Map).Include(x => x.Guild).FirstOrDefaultAsync(x => x.Id == c.Id);

			var stats = full?.MaxStats;
			if (stats == null && full != null)
			{
				full.RegenerateMaxStats(); // older rows may predate the MaxStats block
				stats = full.MaxStats;
			}

			_health = stats?.Health ?? 0;
			_mana = stats?.Mana ?? 0;
			_stamina = stats?.Stamina ?? 0;
			_mapName = full?.Map?.Name ?? "—";
			_guildName = full?.Guild?.Name;
			_money = FormatMoney(full?.Money ?? 0);
			_spells = Models.Spells(c.Id);
		}

		private void PageInventory(int delta)
		{
			_invPage = Math.Clamp(_invPage + delta, 0, InvPageCount - 1);
			_refreshThumbs = true;
		}

		// (Re)build the selected character's model, overlaying the chosen armour set. Cache-bust so a rebuilt/redressed
		// model isn't masked by the browser's cached glb.
		private async Task Render()
		{
			if (_selected == null)
				return;

			_status = "building…";
			StateHasChanged();

			// The posable character rig (skinned body + head + hair), driven by the elbow slider.
			var url = $"/api/character/{_selected.Id}/model.glb?rig=1&t={DateTime.UtcNow.Ticks}";
			if (_module != null)
				await _module.InvokeVoidAsync("show", "char-canvas", url);

			_status = "loaded";
			StateHasChanged();
		}

		private async Task Select(Character c)
		{
			_selected = c;
			_equipment = Models.Equipment(c.Id);
			_inventory = Models.Inventory(c.Id);
			// Open on the first tab that actually has items (most characters only carry Normal).
			_invTab = InvTabs.FirstOrDefault(t => _inventory.Any(i => i.Tab == t)) ?? "Normal";
			_invPage = 0;
			await LoadSheet(c);
			await Render();
			if (_module != null)
			{
				// Build the per-joint pose sliders, then render a 3D thumbnail into each equipped slot.
				await _module.InvokeVoidAsync("buildPoseControls", "char-canvas", "pose-controls");
				await _module.InvokeVoidAsync("populateItemThumbs");
			}
		}

		private void SelectInvTab(string tab)
		{
			_invTab = tab;
			_invPage = 0;
			_refreshThumbs = true;
		}

		// Equipment paper-doll slot by name (placeholder label = the slot name).
		private RenderFragment Slot(string slot, string extra = "")
		{
			return ItemSlot(_equipment.TryGetValue(slot, out var it)
					? it
					: null,
				slot,
				extra);
		}

		// Wallet in tin -> gold/silver/copper/tin (100 tin = 1 copper, 100 copper = 1 silver, 100 silver = 1 gold).
		private static string FormatMoney(ulong tin)
		{
			var (g, s, c, t) = (tin / 1_000_000, tin / 10_000 % 100, tin / 100 % 100, tin % 100);
			var parts = new List<string>();
			if (g > 0)
				parts.Add($"{g}g");
			if (s > 0)
				parts.Add($"{s}s");
			if (c > 0)
				parts.Add($"{c}c");
			if (t > 0 || parts.Count == 0)
				parts.Add($"{t}t");
			return string.Join(" ", parts);
		}
	}
}
