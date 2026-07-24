using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Cli;
using Faolan.Core.Database.Extensions;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Rdb.Assets;
using Faolan.Web.Auth;
using Faolan.Web.Components;
using Faolan.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Faolan.Web
{
	public class Program
	{
		public static int Main(string[] args)
		{
			var builder = WebApplication.CreateBuilder(args);

			// appsettings.json FIRST: it is the app's own defaults (logging, CdnUrl), and the shared faolan.json is
			// deployment config, so the shared file has to win over it - not the other way round.
			//
			// ⚠️ It used to be the other way round, and appsettings.json shipped `"RdbDir": ""`. An empty value is
			// still a value, so it OVERRODE a perfectly good RdbDir in faolan.json and the viewer reported the folder
			// as unconfigured. The empty entry is gone too: absent means unset, "" does not.
			builder.Configuration.Sources.Clear();
			builder.Configuration.AddJsonFile("appsettings.json", true);
			builder.Configuration.AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", true);

			// Then the shared layers, in the order every Faolan executable uses: faolan.json < command line < env.
			var overrides = WebCommand.Overrides(args, out var parseExit);
			if (overrides == null)
				return parseExit;

			builder.Configuration.AddConfiguration(FaolanCommandLine.Build(overrides));

			builder.Services.AddRazorComponents().AddInteractiveServerComponents();

			// faolan.db (read-only here, for the character roster) via the shared pooled-factory registration.
			builder.Services.ConfigureFaolanDatabase(builder.Configuration);

			// The RDB-backed character model builder (#275).
			builder.Services.AddSingleton<CharacterModelService>();

			// Cache for the expensive deterministic renders (item glbs, map pngs). Size is counted in bytes; cap the
			// cache so a scrape can't grow it unbounded.
			builder.Services.AddMemoryCache(o => o.SizeLimit = 256 * 1024 * 1024);

			// Live presence: queries the running game server's MCP endpoint (separate process) for connected players.
			builder.Services.AddSingleton<LiveSessionService>();

			// Admin tools over the shared Accounts table (ban/unban, account type).
			builder.Services.AddScoped<AccountAdminService>();

			// In-game mail (read/compose) directly against the shared Mail table.
			builder.Services.AddScoped<MailService>();

			// Identity Core (UserManager only — no SignInManager/cookie schema) over the game's Accounts table via a
			// custom store. UserManager handles register (CreateAsync) and login (CheckPasswordAsync); the password
			// hasher delegates to the shared PasswordHashing format so the game-server login stays compatible, and
			// CheckPasswordAsync auto-rehashes a legacy plaintext password. Cookie sign-in (below) is still ours.
			builder.Services.AddIdentityCore<Account>(o =>
			{
				// Game usernames/passwords aren't ASCII/complexity-restricted — relax Identity's defaults.
				o.User.AllowedUserNameCharacters = "";
				o.Password.RequiredLength = 1;
				o.Password.RequiredUniqueChars = 1;
				o.Password.RequireDigit = false;
				o.Password.RequireLowercase = false;
				o.Password.RequireUppercase = false;
				o.Password.RequireNonAlphanumeric = false;
			});
			builder.Services.AddScoped<IUserStore<Account>, AccountUserStore>();
			builder.Services.AddScoped<IPasswordHasher<Account>, AccountPasswordHasher>();

			// Cookie auth over the shared game Accounts table. The viewer is public; signing in scopes the roster to
			// your own characters.
			builder
				.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
				.AddCookie(o =>
				{
					o.LoginPath = "/login";
					o.AccessDeniedPath = "/login";
					o.ExpireTimeSpan = TimeSpan.FromDays(14);
				});
			builder.Services.AddAuthorization();
			builder.Services.AddCascadingAuthenticationState();

			var app = builder.Build();

			if (!app.Environment.IsDevelopment())
			{
				app.UseExceptionHandler("/Error", true);
				app.UseHsts();
			}

			app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
			app.UseHttpsRedirection();
			app.UseAuthentication();
			app.UseAuthorization();
			app.UseAntiforgery();
			app.MapStaticAssets();

			// Auth form posts, via Identity's UserManager (hashing + legacy-plaintext auto-rehash). Antiforgery
			// disabled here to keep the simple HTML forms.
			app
				.MapPost("/auth/register", async (HttpContext ctx, UserManager<Account> users, [FromForm] string username, [FromForm] string password) =>
				{
					if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
						return Results.Redirect("/register?error=invalid");

					var account = new Account
					{
						UserName = username,
						Type = AccountType.Normal,
						State = AccountState.Active,
						CreationDate = DateTimeOffset.UtcNow,
						AuthChallenge = "",
						LastIpAddress = ""
					};
					var result = await users.CreateAsync(account, password);
					if (!result.Succeeded)
					{
						var taken = result.Errors.Any(e => e.Code.Contains("Duplicate", StringComparison.OrdinalIgnoreCase));
						return Results.Redirect(taken
							? "/register?error=taken"
							: "/register?error=invalid");
					}

					await SignIn(ctx, account);
					return Results.Redirect("/characters");
				})
				.DisableAntiforgery();

			app
				.MapPost("/auth/login", async (HttpContext ctx, UserManager<Account> users, [FromForm] string username, [FromForm] string password) =>
				{
					var account = await users.FindByNameAsync(username);
					if (account == null || !await users.CheckPasswordAsync(account, password))
						return Results.Redirect("/login?error=1");

					await SignIn(ctx, account);
					return Results.Redirect("/characters");
				})
				.DisableAntiforgery();

			app
				.MapPost("/auth/logout", async (HttpContext ctx) =>
				{
					await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
					return Results.Redirect("/");
				})
				.DisableAntiforgery();

			// glTF endpoints: a character's assembled model, and any single RDB model by id.
			app.MapGet("/api/character/{race}/{sex}/model.glb", (Race race, Sex sex, HttpContext ctx, CharacterModelService models) =>
			{
				var glb = models.BuildCharacter(race, sex);
				if (glb == null)
					return Results.NotFound();

				CacheADay(ctx);
				return Results.File(glb, "model/gltf-binary");
			});

			// ?equip=290301,290354 overlays those armor MESHINDEXFILE ids on the character (gear preview, #275).
			// ?rig=1 returns the character's BODY as a posable skinned rig instead of the static assembled model.
			app.MapGet("/api/character/{id:long}/model.glb", (long id, string? equip, string? rig, string? hide, CharacterModelService models) =>
			{
				// ?hide=Chest,Legs drops those equipment slots from the auto-resolved gear (the viewer's per-slot toggle).
				var hidden = hide?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

				if (rig is "1" or "true" or "True")
				{
					var rigGlb = models.BuildCharacterRig((uint)id, hidden);
					return rigGlb == null
						? Results.NotFound()
						: Results.File(rigGlb, "model/gltf-binary");
				}

				var equipIds = equip
					?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
					.Select(s => uint.TryParse(s, out var v)
						? v
						: 0u)
					.Where(v => v != 0)
					.ToList();
				var glb = models.BuildCharacterById((uint)id, equipIds is { Count: > 0 }
					? equipIds
					: null, hiddenSlots: hidden);
				return glb == null
					? Results.NotFound()
					: Results.File(glb, "model/gltf-binary");
			});

			// Appearance-editor LIVE PREVIEW: build a character glb from explicit query params (nothing persisted).
			// Strict - a missing asset returns 422 with the reason (no silent fallback), which the editor displays.
			app.MapGet("/api/character/custom/model.glb", (Sex sex, Race race, uint? hair, uint? head, float? arm, float? chest, float? bosom, float? stomach, float? arse, float? thigh, float? leg,
				string? tex, CharacterModelService models) =>
			{
				var refs = (tex ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
				var morphs = new BodyMorphDeformer.Morphs(arm ?? 0, chest ?? 0, bosom ?? 0, stomach ?? 0, arse ?? 0, thigh ?? 0, leg ?? 0);
				var (glb, error) = models.BuildCharacterCustom(sex, race, hair, head, morphs, refs);
				return glb == null
					? Results.Problem(error, statusCode: 422)
					: Results.File(glb, "model/gltf-binary");
			});

			// The appearance choices legal for a sex+race (hair / colours / body+face tattoo / heads + morph limits),
			// from the CHARACTERCREATOR table, for the editor's pickers. (SAVE is a direct server-side service call
			// from the Blazor component - no open write endpoint.)
			app.MapGet("/api/appearance/options", (Sex sex, Race race, CharacterModelService models) => Results.Json(models.GetAppearanceOptions(sex, race)));

			// Authentic ANIMSYS clips for the viewer's animation dropdown (#292). ?skeleton=ID overrides the default
			// humanoid rig. Returns [{name, rdbname, duration}] sorted by name.
			app.MapGet("/api/animations", (uint? skeleton, CharacterModelService models) => Results.Json(models.ListAnimations(skeleton)));

			// One clip's decoded keyframes (by rdbname), for THREE.AnimationClip. CDN-only clips resolve via the mirror.
			app.MapGet("/api/animation/{rdbname:long}.json", (long rdbname, HttpContext ctx, CharacterModelService models) =>
			{
				var clip = models.GetAnimation((uint)rdbname);
				if (clip == null)
					return Results.NotFound();

				CacheADay(ctx);
				return Results.Json(clip);
			});

			// Wearable armour-set catalogue (optionally filtered by ?sex=Female/Male), for the viewer's wardrobe picker.
			app.MapGet("/api/armorsets", (Sex? sex, CharacterModelService models) => Results.Json(models
				.ArmorSets(sex)
				.Select(s => new
				{
					s.Number,
					s.Gender,
					s.Name,
					label = $"{s.Number:0000} {s.Name} ({s.Gender})",
					equip = string.Join(',', s.PieceMeshIndexIds)
				})));

			// ?rig=1 (or rig=true) returns the SKINNED model (body/equipment + character skeleton as a glTF skin,
			// posable); ?skeleton=ID overrides the default humanoid rig (14853). Without ?rig it's the static mesh.
			app.MapGet("/api/model/{meshIndexId:long}.glb", (long meshIndexId, string? rig, long? skeleton, CharacterModelService models) =>
			{
				var wantRig = rig is "1" or "true" or "True";
				var glb = wantRig
					? models.BuildRig((uint)meshIndexId, skeleton is { } s
						? (uint)s
						: null)
					: models.BuildModel((uint)meshIndexId);
				return glb == null
					? Results.NotFound()
					: Results.File(glb, "model/gltf-binary");
			});

			app.MapGet("/api/rm/{renderMeshId:long}.glb", (long renderMeshId, CharacterModelService models) =>
			{
				var glb = models.BuildRenderMesh((uint)renderMeshId);
				return glb == null
					? Results.NotFound()
					: Results.File(glb, "model/gltf-binary");
			});

			// An item's 3D display model for the equipment-slot thumbnails (404 when the item has no display mesh).
			app.MapGet("/api/item/{itemId:long}/model.glb", (long itemId, HttpContext ctx, CharacterModelService models) =>
			{
				var glb = models.ItemModel((uint)itemId);
				if (glb == null)
					return Results.NotFound();

				CacheADay(ctx);
				return Results.File(glb, "model/gltf-binary");
			});

			// Live world radar feed (admin only): the map's entities (from the running server via MCP) + world bounds.
			app.MapGet("/api/zone/{mapId:long}", async (long mapId, HttpContext ctx, LiveSessionService live, CharacterModelService models) =>
			{
				if (!(ctx.User?.IsInRole("Admin") ?? false))
					return Results.StatusCode(StatusCodes.Status403Forbidden);

				var entities = await live.GetZoneEntitiesAsync((uint)mapId);
				var bounds = models.MapBounds((uint)mapId);
				return Results.Json(new
				{
					online = entities != null,
					bounds = bounds is { } b
						? new
						{
							w = b.Width,
							h = b.Height
						}
						: null,
					hasMap = models.HasMapImage((uint)mapId),
					entities = entities ?? []
				});
			});

			// A map's top-down terrain image (RDB type 1010209), for the radar background. Admin only; 404 when the
			// map has no local image.
			app.MapGet("/api/zone/{mapId:long}/map.png", (long mapId, HttpContext ctx, CharacterModelService models) =>
			{
				if (!(ctx.User?.IsInRole("Admin") ?? false))
					return Results.StatusCode(StatusCodes.Status403Forbidden);

				var png = models.MapImage((uint)mapId);
				if (png == null)
					return Results.NotFound();

				CacheADay(ctx);
				return Results.File(png, "image/png");
			});

			if (app.Environment.IsDevelopment())
			{
				app.MapGet("/api/debug/char/{id:long}", (long id, CharacterModelService models) => Results.Json(models.DebugCharacter((uint)id)));

				// Body UV mirror diagnostic (#275): is the body's left/right unwrap mirrored?
				app.MapGet("/api/debug/bodyuv/{id:long}", (long id, CharacterModelService models) => Results.Json(models.DescribeBodyUv((uint)id)));

				// Decode an FCTX texture (type/id) to PNG - for inspecting tattoo decal textures (#275).
				app.MapGet("/api/debug/tex/{type:long}/{id:long}.png", (long type, long id, CharacterModelService models) =>
				{
					var png = models.DecodeTexture((uint)type, (uint)id);
					return png == null
						? Results.NotFound()
						: Results.Bytes(png, "image/png");
				});

				// Raw RDB resource text (e.g. a TexScript 1010035 XML) for protocol/asset RE.
				app.MapGet("/api/debug/raw/{type:long}/{id:long}", (long type, long id, CharacterModelService models) =>
				{
					var text = models.RawResourceText((uint)type, (uint)id);
					return text == null
						? Results.NotFound()
						: Results.Text(text, "text/plain");
				});

				// Raw decompressed RDB resource bytes (binary blob analysis, e.g. the rendermesh DPPM morph-target chunk).
				app.MapGet("/api/debug/blob/{type:long}/{id:long}", (long type, long id, CharacterModelService models) =>
				{
					var bytes = models.RawResourceBytes((uint)type, (uint)id);
					return bytes == null
						? Results.NotFound()
						: Results.Bytes(bytes, "application/octet-stream");
				});
			}

			app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

			app.Run();
			return 0;

			// Browser cache for deterministic-by-id renders (item models, race/sex base, map images): a day, immutable.
			static void CacheADay(HttpContext ctx)
			{
				ctx.Response.Headers.CacheControl = "public, max-age=86400, immutable";
			}
		}

		// Issue the auth cookie for an account (id + username claims, plus an "Admin" role for GM/Administrator
		// accounts so the admin tools can gate on it).
		private static Task SignIn(HttpContext ctx, Account account)
		{
			var claims = new List<Claim>
			{
				new(ClaimTypes.NameIdentifier, account.Id.ToString()),
				new(ClaimTypes.Name, account.UserName)
			};
			if (account.Type >= AccountType.GameMaster)
				claims.Add(new Claim(ClaimTypes.Role, "Admin"));

			var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
			return ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
		}
	}
}
