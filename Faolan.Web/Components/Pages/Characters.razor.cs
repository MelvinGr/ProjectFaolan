using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Faolan.Core.Database.Tables;
using Faolan.Core.Extensions;
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

		// Which equipment slots are hidden from the 3D render (the per-slot paper-doll toggle). The animation
		// dropdown + pose sliders are owned client-side by viewer.js. (Each floating Window component owns its own
		// collapse state + drag registration.)
		private readonly HashSet<string> _hiddenSlots = [];

		// --- Appearance editor (floating window) -------------------------------------------------------------
		// The pickable resources (loaded once) + the live edit state seeded from the character on select. Body
		// morphs are the decoded signed slider values (the unit CharacterBody stores / BodyMorphDeformer takes);
		// _eHead is editor-2x units. Preview is a strict ad-hoc build (no fallback) - _editError shows why it failed.
		private CharacterModelService.AppearanceOptions? _appOptions;
		private Character? _selected;
		private IJSObjectReference? _module;
		private IReadOnlyDictionary<string, ItemInfo> _equipment = new Dictionary<string, ItemInfo>();
		private IReadOnlyList<(ItemInfo Info, string Tab)> _inventory = [];
		private IReadOnlyList<(string Name, byte Level, float Range, float SplashDamage)> _spells = [];

		private List<Character>? _characters;
		private bool _ownsSelected;
		private bool _refreshThumbs;
		private int _eArm, _eChest, _eBosom, _eStomach, _eArse, _eThigh, _eLeg;
		private int _invPage;
		private string _eHairStyle = "", _eHairColor = "", _eEyes = "", _eSkin = "", _eBodyTat = "", _eFaceTat = "";

		private string _editStatus = "";
		private string _invTab = "Normal";
		private string _mapName = "—";
		private string _money = "0";
		private string _status = "";
		private string? _editError;
		private string? _guildName;
		private uint _eHead;

		// Selected character's sheet: vitals (player-facing MaxStats) + a few resolved fields.
		private uint _health, _mana, _stamina;

		// The signed-in account (null = anonymous public viewer). Ownership gates the inventory panel + appearance editing.
		private uint? _accountId;

		[SupplyParameterFromQuery(Name = "char")]
		public long? CharId { get; set; }

		private List<(ItemInfo Info, string Tab)> TabItems => _inventory.Where(i => i.Tab == _invTab).ToList();

		[CascadingParameter]
		private Task<AuthenticationState>? AuthState { get; set; }

		private int InvPageCount => Math.Max(1, (int)Math.Ceiling(TabItems.Count / (double)InvPageSize));

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
				{
					_accountId = accountId;
					query = query.Where(c => c.AccountId == accountId);
				}
			}

			// Order by creation date client-side: SQLite can't ORDER BY a DateTimeOffset column.
			_characters = (await query.ToListAsync()).OrderBy(c => c.CreationDate).ToList();
		}

		private string BuildPreviewUrl()
		{
			var tex = new[] { _eHairColor, _eEyes, _eSkin, _eBodyTat, _eFaceTat }.Where(s => !string.IsNullOrEmpty(s));
			var texJoined = string.Join(',', tex);
			var hair = ParseRefId(_eHairStyle);
			var head = _eHead; // already a head monsterdataid

			var q = $"/api/character/custom/model.glb?sex={_selected!.Sex}&race={_selected.Race}" + (hair > 0
					? $"&hair={hair}"
					: "") + (head > 0
					? $"&head={head}"
					: "") + $"&arm={_eArm}&chest={_eChest}&bosom={_eBosom}&stomach={_eStomach}&arse={_eArse}&thigh={_eThigh}&leg={_eLeg}" + (texJoined.Length > 0
					? $"&tex={Uri.EscapeDataString(texJoined)}"
					: "") + $"&t={DateTimeOffset.UtcNow.Ticks}";
			return q;
		}

		// Seed the editor state from the character's stored appearance (called on select). Body morphs come back as
		// floats (whole-number slider values); keep them as ints for clean slider binding + preview URLs.
		private void LoadEditState(Character c)
		{
			// Options are gated to this character's race+sex (CHARACTERCREATOR table).
			_appOptions = Models.GetAppearanceOptions(c.Sex, c.Race);

			var a = Models.GetCurrentAppearance(c.Id);
			_eArm = (int)a.Arm;
			_eChest = (int)a.Chest;
			_eBosom = (int)a.Bosom;
			_eStomach = (int)a.Stomach;
			_eArse = (int)a.Arse;
			_eThigh = (int)a.Thigh;
			_eLeg = (int)a.Leg;
			_eHead = a.HeadMesh / 2; // stored HeadMesh is editor-2x; the editor works in head monsterdataid units
			_eHairStyle = a.HairStyle;
			_eHairColor = a.HairColor;
			_eEyes = a.Eyes;
			_eSkin = a.Skin;
			_eBodyTat = a.BodyTattoo;
			_eFaceTat = a.FaceTattoo;
			_editError = null;
			_editStatus = "";
		}

		// Load the selected character's sheet data: vitals (MaxStats, regenerated from class/level if missing), the map
		// name and guild, and a formatted wallet.
		private async Task LoadSheet(Character c)
		{
			await using var db = await DbFactory.CreateDbContextAsync();
			var full = await db.Characters.Include(x => x.Map).Include(x => x.Guild).FirstOrDefaultAsync(x => x.Id == c.Id);

			// Vitals are DERIVED rather than stored, so there is no row to include and no older row to back-fill
			// (#578). The derivation scales off the character's ATTRIBUTES, which are the class/level base (#444) plus
			// worn gear; this page reads the base only, so the numbers here are the character's pools UNGEARED - a
			// floor, not what the client draws. Aggregating equipped items is the game server's job
			// (MaxStatAggregator), not this page's.
			var classBase = full == null
				? null
				: await db.ClassAttributeBases.AsNoTracking()
					.Where(r => r.Class == full.Class && r.Level == full.Level)
					.ToDictionaryAsync(r => r.StatId, r => r.Amount);
			var stats = full?.MaxVitals(VitalAttributes.From(classBase, null), classBase);

			_health = stats?.Health ?? 0;
			_mana = stats?.Mana ?? 0;
			_stamina = stats?.Stamina ?? 0;
			_mapName = full?.Map?.Name ?? "—";
			_guildName = full?.Guild?.Name;
			_money = FormatMoney(full?.Money ?? 0);
			_spells = Models.Spells(c.Id);
		}

		private int MorphMax(string morph)
		{
			return _appOptions?.MorphLimits.TryGetValue(morph, out var v) == true
				? v[1]
				: 250;
		}

		private int MorphMin(string morph)
		{
			return _appOptions?.MorphLimits.TryGetValue(morph, out var v) == true
				? v[0]
				: -250;
		}

		private void PageInventory(int delta)
		{
			_invPage = Math.Clamp(_invPage + delta, 0, InvPageCount - 1);
			_refreshThumbs = true;
		}

		// Live-preview the current edit state via the strict ad-hoc build endpoint. show() returns null on success or
		// the server's error text (missing asset) on failure - surfaced in _editError, no silent fallback.
		private async Task PreviewEdit()
		{
			if (_selected == null || _module == null)
				return;

			_editStatus = "building…";
			StateHasChanged();
			// keepCamera: true - preserve the user's orbit/zoom across an edit (don't reframe on every tweak).
			_editError = await _module.InvokeAsync<string?>("show", "char-canvas", BuildPreviewUrl(), true);
			_editStatus = _editError == null
				? "preview"
				: "";
			StateHasChanged();
		}

		// (Re)build the selected character's model, overlaying the chosen armour set. Cache-bust so a rebuilt/redressed
		// model isn't masked by the browser's cached glb.
		private async Task Render(bool keepCamera = false)
		{
			if (_selected == null)
				return;

			_status = "building…";
			StateHasChanged();

			// The posable character rig (skinned body + head + hair). ?hide= drops the slots toggled off in the paper-doll.
			var hide = _hiddenSlots.Count > 0
				? "&hide=" + Uri.EscapeDataString(string.Join(',', _hiddenSlots))
				: "";
			var url = $"/api/character/{_selected.Id}/model.glb?rig=1{hide}&t={DateTimeOffset.UtcNow.Ticks}";
			if (_module != null)
				await _module.InvokeVoidAsync("show", "char-canvas", url, keepCamera);

			_status = "loaded";
			StateHasChanged();
		}

		private async Task RevertEdit()
		{
			if (_selected == null)
				return;

			LoadEditState(_selected);
			await PreviewEdit();
		}

		// Persist the current edit state to the character (direct server-side service call - no open write endpoint).
		private void SaveEdit()
		{
			if (_selected == null)
				return;

			var refs = new[] { _eHairStyle, _eHairColor, _eEyes, _eSkin, _eBodyTat, _eFaceTat }.Where(s => !string.IsNullOrEmpty(s)).ToList();
			// Persist HeadMesh in the stored editor-2x units (the editor works in monsterdataid = HeadMesh/2).
			var spec = new CharacterModelService.AppearanceSpec(_eArm, _eChest, _eBosom, _eStomach, _eArse, _eThigh, _eLeg, _eHead * 2, refs);
			_editStatus = Models.SaveAppearance(_selected.Id, spec)
				? "saved ✓"
				: "save failed";
		}

		private async Task Select(Character c)
		{
			_selected = c;
			_ownsSelected = _accountId is { } aid && c.AccountId == aid;
			_hiddenSlots.Clear();
			_equipment = Models.Equipment(c.Id);
			_inventory = Models.Inventory(c.Id);
			// Open on the first tab that actually has items (most characters only carry Normal).
			_invTab = InvTabs.FirstOrDefault(t => _inventory.Any(i => i.Tab == t)) ?? "Normal";
			_invPage = 0;
			LoadEditState(c);
			await LoadSheet(c);
			await Render();
			if (_module != null)
			{
				// Build the per-joint pose sliders + the authentic-animation dropdown (in the floating pose Window; the
				// Window components self-register their own drag), then render a 3D thumbnail into each equipped slot.
				await _module.InvokeVoidAsync("buildPoseControls", "char-canvas", "pose-controls");
				await _module.InvokeVoidAsync("buildAnimationControls", "char-canvas", "anim-select");
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
				: null, slot, extra, slot);
		}

		// Show/hide one equipped piece in the 3D render (per-slot paper-doll toggle). Keeps the camera so toggling doesn't
		// reframe the view.
		private async Task ToggleSlotHidden(string slot)
		{
			if (!_hiddenSlots.Add(slot))
				_hiddenSlots.Remove(slot);

			await Render(true);
		}

		// Wallet in tin -> gold/silver/copper/tin (100 tin = 1 copper, 100 copper = 1 silver, 100 silver = 1 gold).
		private static string FormatMoney(ulong tin)
		{
			return tin.ToCurrencyString();
		}

		private static uint ParseRefId(string reference)
		{
			var i = reference.LastIndexOf('_');
			return i >= 0 && uint.TryParse(reference.AsSpan(i + 1), out var v)
				? v
				: 0;
		}
	}
}
