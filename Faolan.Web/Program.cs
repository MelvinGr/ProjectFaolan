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

            builder.Configuration.AddJsonFile(Path.GetFullPath("faolan.json"));
            // The app's own appsettings.json (RdbDir, ...) lives next to the dll, not the CWD repo root.
            builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));

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

            // ?equip=290301,290354 overlays those armor MESHINDEXFILE ids on the character (gear preview, #275).
            // ?rig=1 returns the character's BODY as a posable skinned rig instead of the static assembled model.
            app.MapGet("/api/character/{id:long}/model.glb", (long id, string? equip, string? rig, CharacterModelService models) =>
            {
                if (rig is "1" or "true" or "True")
                {
                    var rigGlb = models.BuildCharacterRig((uint)id);
                    return rigGlb is null ? Results.NotFound() : Results.File(rigGlb, "model/gltf-binary");
                }

                var equipIds = equip?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(s => uint.TryParse(s, out var v) ? v : 0u).Where(v => v != 0).ToList();
                var glb = models.BuildCharacterById((uint)id, equipIds is { Count: > 0 } ? equipIds : null);
                return glb is null ? Results.NotFound() : Results.File(glb, "model/gltf-binary");
            });

            // Wearable armour-set catalogue (optionally filtered by ?sex=Female/Male), for the viewer's wardrobe picker.
            app.MapGet("/api/armorsets", (Sex? sex, CharacterModelService models) =>
                Results.Json(models.ArmorSets(sex).Select(s => new
                {
                    s.Number, s.Gender, s.Name,
                    label = $"{s.Number:0000} {s.Name} ({s.Gender})",
                    equip = string.Join(',', s.PieceMeshIndexIds)
                })));

            // ?rig=1 (or rig=true) returns the SKINNED model (body/equipment + character skeleton as a glTF skin,
            // posable); ?skeleton=ID overrides the default humanoid rig (14853). Without ?rig it's the static mesh.
            app.MapGet("/api/model/{meshIndexId:long}.glb", (long meshIndexId, string? rig, long? skeleton, CharacterModelService models) =>
            {
                var wantRig = rig is "1" or "true" or "True";
                var glb = wantRig
                    ? models.BuildRig((uint)meshIndexId, skeleton is { } s ? (uint)s : null)
                    : models.BuildModel((uint)meshIndexId);
                return glb is null ? Results.NotFound() : Results.File(glb, "model/gltf-binary");
            });

            app.MapGet("/api/rm/{renderMeshId:long}.glb", (long renderMeshId, CharacterModelService models) =>
            {
                var glb = models.BuildRenderMesh((uint)renderMeshId);
                return glb is null ? Results.NotFound() : Results.File(glb, "model/gltf-binary");
            });

            // An item's 3D display model for the equipment-slot thumbnails (404 when the item has no display mesh).
            app.MapGet("/api/item/{itemId:long}/model.glb", (long itemId, CharacterModelService models) =>
            {
                var glb = models.ItemModel((uint)itemId);
                return glb is null ? Results.NotFound() : Results.File(glb, "model/gltf-binary");
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
                    return png is null
                        ? Results.NotFound()
                        : Results.Bytes(png, "image/png");
                });

                // Raw RDB resource text (e.g. a TexScript 1010035 XML) for protocol/asset RE.
                app.MapGet("/api/debug/raw/{type:long}/{id:long}", (long type, long id, CharacterModelService models) =>
                {
                    var text = models.RawResourceText((uint)type, (uint)id);
                    return text is null
                        ? Results.NotFound()
                        : Results.Text(text, "text/plain");
                });

                // Raw decompressed RDB resource bytes (binary blob analysis, e.g. the rendermesh DPPM morph-target chunk).
                app.MapGet("/api/debug/blob/{type:long}/{id:long}", (long type, long id, CharacterModelService models) =>
                {
                    var bytes = models.RawResourceBytes((uint)type, (uint)id);
                    return bytes is null
                        ? Results.NotFound()
                        : Results.Bytes(bytes, "application/octet-stream");
                });
            }

            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();

            app.Run();
        }
    }
}
