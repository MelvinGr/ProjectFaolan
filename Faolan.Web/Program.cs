using Faolan.Core;
using Faolan.Core.Enums;
using Faolan.Web.Components;
using Faolan.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Faolan.Web
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Like the game server, run from the repo root so faolan.json (DatabasePath, ...) + faolan.db resolve.
            builder.Configuration.SetBasePath(Directory.GetCurrentDirectory());
            builder.Configuration.AddJsonFile(Path.GetFullPath("faolan.json"), optional: true);
            // The app's own appsettings.json (RdbDir, ...) lives next to the dll, not the CWD repo root.
            builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true);

            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();

            // faolan.db (read-only here, for the character roster) via the shared pooled-factory registration.
            builder.Services.ConfigureFaolanDatabase(builder.Configuration);

            // The RDB-backed character model builder (#275).
            builder.Services.AddSingleton<CharacterModelService>();

            var app = builder.Build();

            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Error", createScopeForErrors: true);
                app.UseHsts();
            }

            app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
            app.UseHttpsRedirection();
            app.UseAntiforgery();
            app.MapStaticAssets();

            // glTF endpoints: a character's assembled model, and any single RDB model by id.
            app.MapGet("/api/character/{race}/{sex}/model.glb", (Race race, Sex sex, CharacterModelService models) =>
            {
                var glb = models.BuildCharacter(race, sex);
                return glb is null ? Results.NotFound() : Results.File(glb, "model/gltf-binary");
            });

            app.MapGet("/api/character/{id:long}/model.glb", (long id, CharacterModelService models) =>
            {
                var glb = models.BuildCharacterById((uint)id);
                return glb is null ? Results.NotFound() : Results.File(glb, "model/gltf-binary");
            });

            app.MapGet("/api/model/{meshIndexId:long}.glb", (long meshIndexId, CharacterModelService models) =>
            {
                var glb = models.BuildModel((uint)meshIndexId);
                return glb is null ? Results.NotFound() : Results.File(glb, "model/gltf-binary");
            });

            app.MapGet("/api/rm/{renderMeshId:long}.glb", (long renderMeshId, CharacterModelService models) =>
            {
                var glb = models.BuildRenderMesh((uint)renderMeshId);
                return glb is null ? Results.NotFound() : Results.File(glb, "model/gltf-binary");
            });

            app.MapGet("/api/debug/char/{id:long}", (long id, CharacterModelService models) =>
                Results.Json(models.DebugCharacter((uint)id)));

            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();

            app.Run();
        }
    }
}
