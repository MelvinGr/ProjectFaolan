# Gear look→mesh resolver — RE handoff (#275)

**Goal:** render a character's actual equipped gear in the Faolan.Web 3D viewer, driven by the character's
equipment in `faolan.db` — for any character, without depending on client-side login caches.

## STATUS: SOLVED (2026-07-01) — no client RE needed

The worn mesh **is in the item template after all** — the earlier premise (an indirect look-id resolved by
client code) was wrong. The look-id at template body offset @16 (13117 for Darkandskiny's cotton set) is a
**red herring**: it is a dye/appearance grouping that *coincidentally collides* with an unrelated resource id
(1010030:13117 is a Tortage building; 5204334/9398638:13117 are that building's PNG textures). It does not
drive the mesh.

**The real mapping:** each equipped item's decrypted template carries its worn armour meshindex(es) in a
**byte-packed trailing sub-record, tagged by field-id 9** — `... 09 00 00 00 <meshIndexId u32> ...`. The value
resolves to a MESHINDEXFILE (type 1010030) under `items\armor`. The existing item-template report never
surfaced it because it scans u32s **word-aligned** and this sub-record is unaligned. An item may carry several
field-9 entries: a genuine male/female pair of one garment (pick by character sex) OR distinct pieces (e.g.
the slave manacles carry both the handcuff mesh `700842` and a base cotton body `700815` — keep both).

Verified for **Darkandskiny** (Cimmerian female; Chest=Silver Gown 324139, Legs=Silver Bottom 324171,
Wrist=Slave Manacles 3361557): field-9 yields exactly `{323713, 323725, 700842, 700815}` — the same set the
in-client capture verified. Generalises across plate/cloth/helms/boots (each gives its M+F variants); weapons
carry no field-9 armour mesh (correct — they are held meshes).

**Shipped:** `Faolan.Core.Rdb/Assets/ItemLookResolver.cs` (field-9 extraction + per-item sex-variant collapse)
+ `CharacterModelService.ResolveEquippedMeshes` wiring. `/api/character/{id}/model.glb` with **no** `?equip`
now auto-dresses the character from their `faolan.db` equipment (byte-identical to the explicit known-good
`?equip=` overlay). Fully offline: RDB + faolan.db only, no Preview.bin / Frida / live client.

> NOTE: deleting a character's `Prefs/.../Preview.bin` **crashes** this client on char-select (null-deref on
> the missing file, before the in-world resolver ever runs) — do not delete it. It was never needed anyway.

### Rendering in the viewer (also shipped)

The `/characters` page requests `model.glb?rig=1` (the posable skinned rig), so the gear had to render *and*
skin *and* morph there, not just as a static overlay:

- **Skinning:** the armour MESHINDEXFILE geometry carries its own per-vertex bone weights + palette (even the
  pieces whose manifest is `<RenderMesh>` not `<RenderSkinnedMesh>` — `ReadModel` reports `IsSkinned=true`).
  `BuildCharacterFromParts` adds the equip meshes before skeleton selection, so `GlbWriter` skins the whole
  assembly to one shared skeleton (body + head + hair + gear, all with `JOINTS_0`/`WEIGHTS_0`). No separate
  rigging step is needed. ⚠️ `GlbWriter` only emits a *skinned* glb when **every** submesh is skinned; a future
  genuinely-rigid gear piece would flip the whole model back to static — bind such pieces to their anchor bone
  (like the hair path) instead of failing the all-skinned check.
- **Morph:** the LBS body-morph loop was extended from `body.Count` to all body+gear submeshes, so worn gear
  conforms to a morphed body (same skeleton + weights) instead of clipping through it.
- **Alpha (3-way):** AoC diffuse alpha is overloaded. `ClassifyAlpha` (was `HasTransparency`): near-zero
  "holes" (>1% of texels) ⇒ **Cutout** (glTF `MASK`, hair/lattice); else a large mid-alpha body (>10%) ⇒
  **Blend** (glTF `BLEND`, sheer cloth); else **Opaque** (ignore the channel — many armour maps put a
  specular/gloss gradient in alpha, which the old `alpha<128` test mistook for transparency and alpha-MASK
  carved into ragged holes). `SubMesh.Alpha` (enum) replaced the old `AlphaMask` bool; `GlbWriter` emits
  `MASK`/`BLEND` accordingly. The Silver Gown's sheer cloth reads translucent; its trim + the manacles stay
  opaque; hair/eyelashes keep their cutout.

**Gitea #275 is reopened + labelled `needs-tuning`** — remaining tweaks: dial in the sheer/BLEND opacity level
(and possible double-sided-BLEND depth-sort artifacts); the alpha classification is heuristic (revisit if a
spec-map-in-alpha piece gets wrongly blended, or a sheer piece stays opaque); the M/F sex-variant collapse is
path-name based (verify on more items); weapons/held items + non-armor slots carry no field-9 mesh (not yet
rendered).

Everything below is the original RE investigation, kept for reference.

---

**Original blocker (now disproven):** an item's worn mesh is *not* in the item template. The item carries an
indirect **look-id** (template body offset @16), which the **client** resolves to one or more skinned-armour
meshindexes. That resolver is client code. This doc captures everything learned trying to crack it.

**Original status: NOT solved.** The tooling is solved (live RE now works via Frida). The exact resolver
function is still unfound — it is buried deep in the character visual-build subsystem.

## Verified target data

- **Darkandskiny** — char id `33554435`, Cimmerian female. Equipped: **Silver Gown** `324139` (chest,
  look-id **13117**), **Silver Bottom** `324171` (legs, look-id **13117**), **Slave Manacles** `3361557`
  (wrist). Decrypt any item template: `dotnet run --project Faolan.Importer -- --report item-template
  --report-arg <itemId>`.
- **Resolved meshes** (what the client actually loads for her): look 13117 → meshindexes **323713, 323725,
  700815** (all `items\armor\_sets\...\027x_..._global_cotton_female`); **4842950** (leg wraps); **700842**
  (manacles — this one is a *direct* meshindex in the Manacles template `field 12 = 700842`; the look-based
  items have no such field).
- Item template dead-ends (confirmed coincidences, NOT the mesh): look `13117` → MESHINDEXFILE `13117` is the
  Tortage building `tortage_addon_c30.max`; Fine Steelsilk Gloves' template ref `220416` → a blood-pool decal.
  So the mesh is genuinely not derivable from item data.

## Tooling breakthrough: Frida-under-Wine (this is the big win — reuse it)

On this Proton/Wine client, **gdb software breakpoints never fire**, **`strace -k` can't unwind the 32-bit Wine
stack**, and **winedbg isn't shipped by Proton**. **Frida works**, but only via **frida-server-under-Wine**:

1. Windows x86 (32-bit) `frida-server.exe` matching the host frida (17.14.1 here), run inside the game's Proton
   prefix so it shares the game's wineserver:
   ```
   WINEPREFIX=/mnt/btrfs/@games/debian-installation/steamapps/compatdata/217750/pfx/ \
   WINEDLLPATH="<Proton>/files/lib/vkd3d:<Proton>/files/lib/wine" WINEESYNC=0 WINEFSYNC=0 \
   "<Proton - Experimental>/files/bin/wine" frida-server.exe -l 127.0.0.1:27042
   ```
   (WINEESYNC/FSYNC=0 MUST match the game's PROTON_NO_ESYNC/FSYNC=1 or it won't share the wineserver.)
2. Linux side: run `frida`/`frida-ps` from a Python env with the matching frida version installed (the distro
   `frida` shim is often a stub - use the one in your pyenv/venv). `frida-ps -H 127.0.0.1:27042` lists Windows
   pids; `dev=frida.get_device_manager().
   add_remote_device('127.0.0.1:27042'); dev.attach(<winpid>)`. The AoC win-pid changes every launch.
3. Module base = **0x400000** (no ASLR) → Ghidra addresses == runtime addresses; hook `ptr('0x....')` directly.
   **Only hook function ENTRIES** — mid-function return-address hooks CRASHED the client. Backscan a return
   address to its entry via prologue `8B FF 55 8B EC` or `55 8B EC`. Never run two attaches concurrently
   (VirtualAllocEx 0x5 race). Injection is a bit fragile; expect the odd client crash + relog.

Frida probe scripts are in `Captures/frida/` (frida_stack.py = RequestResource full stack; frida_write.py =
Preview.bin write backtrace; frida_add.py = AddSkinnedMesh; frida_attr.py = attr-getter+builder; frida_up.py =
RequestResource caller; frida_entry.py/frida_capture.py = generic). Ghidra decompile:
`FNS=<hexaddr,hexaddr> /opt/ghidra/support/analyzeHeadless Captures/ghidra/output aoc -process
AgeOfConan.exe -noanalysis -scriptPath Captures/ghidra/output/scripts -postScript DecompileFns.java`.

## Architecture found (the call graph)

The mesh list is **always deserialized from a serialized `VisualCatMesh` blob — never computed inline.** The
"resolver" is whatever *builds that blob* from the equipment looks. Full stack captured at the meshindex
request for a gown piece (`323713`), bottom→top:

```
0x10601b4  FUN_01060070  RDBObject_t::ctor
0x1842818  FUN_01842818  RequestResource(type=ecx, id=edx)   <-- edx = the ALREADY-RESOLVED meshindex
0xdd8310 / 0xfcc5aa / 0x10606a2 / 0x104d0d5 / 0x104c90d       (resource-manager plumbing)
0x1033dd9  FUN_01033cd0  IdxOrFmeLoader (loads meshindex, type 0xf696e=1010030; gender variants 0x4f696e/0x8f696e)
0xfed7bf   FUN_00fed5d0  VisualCatMesh_c::AddSkinnedMesh(this, nIdxOrFmeId, ...)   <-- DESERIALIZE-path add
0xfeae4a   FUN_00feaa30  VisualCatMesh_c::LoadSerialized  (reads mesh ids from a serialized stream)
0xfe720e   FUN_00fe6ef0  VisualCATMesh_c loader (creates AsyncLoader_c<RDBMonsterData_c>; opens "/Preview.bin")
0x11f2658  FUN_011f23a0  CharacterSelectView_c (loads Prefs/<acct>/Char<id>/Preview.bin)
```
The **manacles** (`700842`, the direct-field-12 item) takes a *different* path after `0x104c90d`:
`0x100ee14 → 0x1018141 → 0x101b21d → 0xe78504 → 0x1531557 → 0x1530ef2 → 0x152eb84 → 0x152e748 → 0x12abb91`.

**In-world authoritative build** writes Preview.bin during `CoreFullPlayerCharacterIIR::Activate`
(`FUN_00796c80` @ frame 0x148c1bc). The Preview.bin *write* backtrace (serializer → save flow):
```
0xfea0xx (field serializer) <- 0x44bfa8 (VisualCatMesh serialize) <- 0x443cba <- 0x7971f5 <- 0x148c1bc
  (FUN_00796c80 CoreFullPlayerCharacterIIR::Activate) <- 0x58a09a / 0x482a66 / 0x492087 (IIR dispatch)
```

### Preview.bin (the resolver's OUTPUT — decoded, parseable)
`.../compatdata/217750/pfx/drive_c/users/steamuser/AppData/Local/Funcom/Conan/Prefs/<account>/Char<charId>/
Preview.bin` (~470 B). Written by the authoritative in-world `Activate`. Format: byte0 version `0x04`, then
identity matrices, then **16-byte skinned-mesh records** (meshindex u32 at record offset +1; e.g. `0x4F081`
=323713 @0x65, `0x4F08D`=323725 @0x75, `0xAB18F`=700815 @0x85), then texscript strings `*texscript_NNNNNNN`.
Deleting it forces the client to resolve fresh on next login (and it rewrites the file) — that's the trigger
used for captures.

## What is RULED OUT

- **`FUN_008b9ca0` (documented body-armor builder) + attribute `0x2da`**: does **NOT fire** for cloth. Tested
  with *working Frida* (not just the earlier broken gdb). The `gear_look_to_meshindex_runtime_attr` memory's
  mechanism is dead for this case.
- **`FUN_00fed5d0` AddSkinnedMesh**: only the *deserialize* path. The fresh-resolve build adds meshes via a
  different (unfound) path.
- Item template fields; sibling RDB types `0x4F696E`/`0x8F696E` (CDN, parallel per-meshindex data); MonsterData
  `1010201:13117` (doesn't exist).

## Next steps (recommended order)

1. **Find where the VisualCatMesh mesh-list vector is POPULATED in the fresh build** (not deserialized). Two
   good tacks:
   - **Frida hardware watchpoint** on the value `323713` (or the VisualCatMesh's mesh-vector memory) during a
     cold build (Preview.bin deleted) → the write backtrace is the resolver. Get the vector address by hooking
     the serialize method `FUN_00448...`/`0x44bfa8`'s function (decompile it: it iterates the mesh list — read
     the struct offset of the fcVector) then watch that vector during the next cold build.
   - **Decompile the `CoreFullPlayerCharacterIIR::Activate` (FUN_00796c80) subtree** for the equipment→visual
     build; it reads the char snapshot's equipment and drives the VisualCatMesh build.
2. **Decompile the manacles' simpler path** (`0x100ee14 = FUN_?`, `0x101b21d`, `0xe78504`) — the field-12
   direct-meshindex path; contrasting it with the look path should expose where a *look* becomes a mesh.
3. Cold-load crash: the client crashed once building cold with Frida attached (documented loading-screen
   fragility). Consider capturing on an **empty playfield (map 5)** and/or reducing hook overhead.

## Pragmatic alternative (recommended to actually SHIP gear rendering)

`RequestResource (FUN_01842818)` is a clean, working hook: at a cold entry `ecx`=type, `edx`=the **resolved
meshindex**. Filtering `type==1010030` yields exactly the worn meshindexes for the equipped gear (verified:
look 13117 → 323713/323725/700815). Build a **`(look-id, sex) → meshindexes` table in `faolan.db`**, populated
by capturing each distinct look once (Frida), and resolve item→look-id from the template at render time. This
is **login-independent to use** (unlike Preview.bin), authoritative (the client's own result, not
approximated), and covers every item that shares a captured look (13117 alone = Silver Gown / Blue Gown /
Dusklight Tunic / Brown Loincloth / …). The viewer already renders overlaid meshindexes via
`/api/character/{id}/model.glb?equip=id,id,...`.

See memories: `frida_under_wine_client_re`, `gear_mesh_capture_via_strace_leidx`,
`gear_look_to_meshindex_runtime_attr`, `rdb_mesh_texture_extraction_275`.
