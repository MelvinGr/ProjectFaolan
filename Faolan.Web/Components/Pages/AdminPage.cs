using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace Faolan.Web.Components.Pages
{
	// The admin gate, in one place. Every /admin page had its own copy: resolve the cascading auth state, bounce to /
	// unless the user is in the "Admin" role (stamped at sign-in from the account type), then read the caller's own
	// account id out of the NameIdentifier claim.
	//
	// A per-page copy of this check fails silently - a page that drifted, or a new page whose author copied an older
	// version, would stop being protected while still looking correct. So OnInitializedAsync is sealed here and a
	// subclass gets LoadAsync instead, which is reached only once the gate has passed: a page cannot forget the gate or
	// replace it with its own variant.
	public abstract class AdminPage : ComponentBase
	{
		[Inject] protected NavigationManager Nav { get; set; } = null!;

		[CascadingParameter] private Task<AuthenticationState>? AuthState { get; set; }

		protected bool Authorized { get; private set; }

		// The signed-in admin's own account id, so a page can refuse to let them change their own privileges.
		protected uint SelfId { get; private set; }

		protected sealed override async Task OnInitializedAsync()
		{
			var user = AuthState == null
				? null
				: (await AuthState).User;
			if (user == null || !user.IsInRole("Admin"))
			{
				Nav.NavigateTo("/", true);
				return;
			}

			uint.TryParse(user.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var selfId);
			SelfId = selfId;
			Authorized = true;
			await LoadAsync();
		}

		// The page's own load, run only after the gate has passed.
		protected abstract Task LoadAsync();
	}
}
