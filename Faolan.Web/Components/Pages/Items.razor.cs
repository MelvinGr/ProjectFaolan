using Faolan.Web.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace Faolan.Web.Components.Pages
{
	public partial class Items
	{
		private const int PageSize = 48;
		private bool _loading;
		private bool _refreshThumbs;
		private IJSObjectReference? _module;
		private int _page;
		private int _total;
		private IReadOnlyList<ItemInfo> _items = [];
		private string _query = "";
		private string _type = "";
		private int PageCount => Math.Max(1, (int)Math.Ceiling(_total / (double)PageSize));

		protected override async Task OnAfterRenderAsync(bool firstRender)
		{
			if (firstRender)
			{
				_module = await JS.InvokeAsync<IJSObjectReference>("import", "./js/viewer.js");
				await _module.InvokeVoidAsync("initItemTooltips");
			}

			if (_refreshThumbs && _module != null)
			{
				_refreshThumbs = false;
				await _module.InvokeVoidAsync("populateItemThumbs");
			}
		}

		protected override void OnInitialized()
		{
			Load();
		}

		private void Load()
		{
			_loading = true;
			var type = int.TryParse(_type, out var t)
				? t
				: (int?)null;
			(_items, _total) = Models.SearchItems(_query, type, _page, PageSize);
			_loading = false;
			_refreshThumbs = true;
		}

		private void OnKey(KeyboardEventArgs e)
		{
			if (e.Key == "Enter")
				Search();
		}

		private void Page(int delta)
		{
			_page = Math.Clamp(_page + delta, 0, PageCount - 1);
			Load();
		}

		private void Search()
		{
			_page = 0;
			Load();
		}

		private static string Initials(string name)
		{
			var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
			return parts.Length == 0 ? "?" : parts.Length == 1 ? parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant() : (parts[0][..1] + parts[1][..1]).ToUpperInvariant();
		}
	}
}
