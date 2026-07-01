// three.js glTF viewer for the Faolan character/model pages (#275). ES module loaded via Blazor JS interop.
import * as THREE from 'three';
import {GLTFLoader} from 'three/addons/loaders/GLTFLoader.js';
import {OrbitControls} from 'three/addons/controls/OrbitControls.js';

const states = new Map(); // canvasId -> { renderer, scene, camera, controls, model }
if (typeof window !== 'undefined') window.__faolanStates = states; // dev hook (headless camera control)

function setup(canvas) {
    const renderer = new THREE.WebGLRenderer({canvas, antialias: true});
    renderer.setPixelRatio(window.devicePixelRatio);

    const scene = new THREE.Scene();
    scene.background = new THREE.Color(0x1b1d22);

    const camera = new THREE.PerspectiveCamera(45, 1, 0.01, 100);
    camera.position.set(0, 1, 3);

    const controls = new OrbitControls(camera, renderer.domElement);
    controls.enableDamping = true;

    // Soft three-point setup so skin curvature reads naturally (a single hard key exaggerates baked seams).
    scene.add(new THREE.HemisphereLight(0xfff4e6, 0x303840, 1.0));
    const key = new THREE.DirectionalLight(0xffffff, 1.1);
    key.position.set(2, 3, 3);
    scene.add(key);
    const fill = new THREE.DirectionalLight(0xbcd0ff, 0.5);
    fill.position.set(-3, 1, 2);
    scene.add(fill);
    const rim = new THREE.DirectionalLight(0xffffff, 0.6);
    rim.position.set(0, 2, -4);
    scene.add(rim);
    scene.add(new THREE.GridHelper(4, 8, 0x444444, 0x333333));

    const state = {renderer, scene, camera, controls, model: null};
    states.set(canvas.id, state);

    // Sync the drawing buffer + camera aspect to the live canvas box. Idempotent (only touches three.js when the
    // size actually changed) so it is safe to call every frame - which is what fixes the horizontal stretch: the
    // canvas box is often not laid out yet when setup() runs (height 0), so a one-shot resize leaves a stale square
    // aspect against a wide canvas. Reading clientWidth/clientHeight (never the buffer attributes) keeps the aspect
    // honest once layout settles.
    function resize() {
        const w = canvas.clientWidth, h = canvas.clientHeight;
        if (w === 0 || h === 0) return;
        const dpr = window.devicePixelRatio;
        if (canvas.width !== Math.round(w * dpr) || canvas.height !== Math.round(h * dpr))
            renderer.setSize(w, h, false);
        if (camera.aspect !== w / h) {
            camera.aspect = w / h;
            camera.updateProjectionMatrix();
        }
    }

    state.resize = resize;

    window.addEventListener('resize', resize);
    resize();

    (function loop() {
        requestAnimationFrame(loop);
        resize();
        controls.update();
        if (state.skinned && state.idle !== false) applyIdle(state, performance.now() / 1000);
        renderer.render(scene, camera);
    })();

    return state;
}

// A subtle synthetic idle (breathing + weight-shift) layered on the bind+pose each frame. This is NOT a retail clip -
// the authentic movement_idle keyframes live in the RDB ANIMSYS 'LMXB' container (type 1010511; e.g. skeleton 14853 ->
// eunuch_idle2 clip 14906), which is a proprietary property-tree/curve format still to be decoded (#275 follow-up). The
// breathing period (4s) mirrors the retail movement_idle duration. No-op unless the model is a skinned rig.
const IDLE_AXES = { pitch: [1, 0, 0], yaw: [0, 1, 0], roll: [0, 0, 1] };

function idleRot(byName, name, axis, deg) {
    const b = byName.get(name);
    if (!b || !deg) return;
    b.quaternion.multiply(new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(...axis), deg * Math.PI / 180));
}

function applyIdle(state, t) {
    applyPose(state); // reset to bind + slider pose, then layer the idle on top
    const byName = new Map(state.skinned.skeleton.bones.map(b => [b.name, b]));
    const TAU = Math.PI * 2;
    const breathe = Math.sin(t * TAU / 4.0);   // chest rise/fall, 4s (retail movement_idle duration)
    const sway = Math.sin(t * TAU / 7.3);       // slow weight shift side to side
    const drift = Math.sin(t * TAU / 9.1);      // slow head/gaze drift

    // Breathing: expand the chest and let the shoulders lift a touch on the in-breath; the neck counter-rotates so the
    // head stays level rather than nodding with the ribcage.
    idleRot(byName, 'rig_bone_Spine1', IDLE_AXES.pitch, breathe * 0.9 - 0.3);
    idleRot(byName, 'rig_bone_Spine2', IDLE_AXES.pitch, breathe * 0.8);
    idleRot(byName, 'rig_bone_Neck', IDLE_AXES.pitch, -breathe * 0.7);
    idleRot(byName, 'rig_bone_LeftShoulder', IDLE_AXES.pitch, breathe * 0.6);
    idleRot(byName, 'rig_bone_RightShoulder', IDLE_AXES.pitch, breathe * 0.6);

    // Weight shift: a gentle sway through the hips + a counter-lean in the lower spine so it reads as balance, not a lean.
    idleRot(byName, 'rig_bone_Hips', IDLE_AXES.roll, sway * 0.8);
    idleRot(byName, 'rig_bone_Spine', IDLE_AXES.roll, -sway * 0.6);

    // Slow gaze drift.
    idleRot(byName, 'rig_bone_Head', IDLE_AXES.yaw, drift * 1.6);
}

// Load (or reload) a glb URL into the named canvas. Frames the camera to the model unless keepCamera is set (the
// appearance editor reloads on every tweak and must NOT reset the user's orbit/zoom). Returns null on success, or the
// server's error text on failure (the strict appearance-preview endpoint returns 422 with a reason) - so the caller can
// surface exactly what is missing instead of a silent failure.
export async function show(canvasId, url, keepCamera) {
    const canvas = document.getElementById(canvasId);
    if (!canvas) return 'Canvas not found.';

    let gltf;
    try {
        gltf = await new GLTFLoader().loadAsync(url);
    } catch (e) {
        try {
            const r = await fetch(url);
            if (!r.ok) {
                const p = await r.json().catch(() => null);
                return (p && (p.detail || p.title)) || ('Load failed: ' + r.status);
            }
        } catch {
        }
        return 'Failed to load model.';
    }

    const state = states.get(canvasId) ?? setup(canvas);
    if (state.model) {
        state.scene.remove(state.model);
        state.model = null;
    }

    const model = gltf.scene;

    // Normals are now position-welded smooth normals baked by the C# GlbWriter (recomputing them here facets the mesh,
    // because the glb splits vertices at every UV-island seam). Just keep smooth shading on.
    model.traverse(o => {
        if (o.isMesh) {
            o.material.flatShading = false;
            o.material.needsUpdate = true;
        }
    });

    // Box3.setFromObject mis-measures a SkinnedMesh whose skeleton hasn't been posed yet (it collapsed our model to
    // ~0.6 tall and framed the camera onto the legs). Union each mesh's POSITION-based geometry box instead.
    model.updateWorldMatrix(true, true);
    const box = new THREE.Box3();
    model.traverse(o => {
        if (o.isMesh && o.geometry) {
            o.geometry.computeBoundingBox();
            box.union(o.geometry.boundingBox.clone().applyMatrix4(o.matrixWorld));
        }
    });
    const size = box.getSize(new THREE.Vector3());
    const center = box.getCenter(new THREE.Vector3());
    model.position.sub(center);                 // center at origin
    model.position.y += size.y / 2;             // stand on the grid

    // Capture a skinned rig (if any) so the model can be posed. Store each bone's bind rotation to compose deltas onto.
    state.skinned = null;
    state.bind = null;
    model.traverse(o => {
        if (o.isSkinnedMesh && !state.skinned) {
            state.skinned = o;
            state.bind = new Map(o.skeleton.bones.map(b => [b.name, b.quaternion.clone()]));
        }
    });
    applyPose(state); // re-apply the current pose across a reload

    state.scene.add(model);
    state.model = model;
    state.modelSize = size; // dev hook: lets headless tooling frame the head

    // Frame the whole model for the CURRENT aspect (portrait phones are width-limited, so fit both axes of the bounding
    // sphere - otherwise a tall body gets cropped to just the legs). Resize the renderer + aspect to the live canvas
    // first: on mobile the layout often hasn't settled when setup() ran, leaving a stale (square) projection.
    state.resize(); // sync renderer + aspect to the live canvas box (from clientWidth/Height only)

    // Frame the model - but not on an editor live-preview reload (keepCamera), so the user's orbit/zoom is preserved.
    if (!keepCamera) {
        const sphere = box.getBoundingSphere(new THREE.Sphere());
        const vFov = state.camera.fov * Math.PI / 180;
        const hFov = 2 * Math.atan(Math.tan(vFov / 2) * state.camera.aspect);
        const dist = Math.max(sphere.radius / Math.sin(vFov / 2), sphere.radius / Math.sin(hFov / 2)) * 1.08;
        const cy = size.y / 2;
        state.controls.target.set(0, cy, 0);
        state.camera.position.set(0, cy, dist);
    }
    state.controls.update();
    return null;
}

// Posable joints. Each control bends one or more rig bones `deg` degrees about a LOCAL axis (0 = bind/rest). Left/right
// pairs share the same local axis — the rig is mirrored so one axis bends both the same anatomical way (verified on the
// elbows). Angles compose onto each bone's BIND rotation; the render loop refreshes the skeleton every frame so children
// follow. Axes/ranges are empirical — tweak the axis sign or swap components if a joint bends the wrong way.
const POSE_CONTROLS = [
    {id: 'head', label: 'head', axis: [1, 0, 0], min: -35, max: 35, bones: ['rig_bone_Neck', 'rig_bone_Head']},
    {id: 'turn', label: 'head turn', axis: [0, 1, 0], min: -60, max: 60, bones: ['rig_bone_Neck']},
    {id: 'spine', label: 'spine', axis: [1, 0, 0], min: -25, max: 35, bones: ['rig_bone_Spine1', 'rig_bone_Spine2']},
    {
        id: 'shoulders',
        label: 'shoulders',
        axis: [0, 0, 1],
        min: -90,
        max: 20,
        bones: ['rig_bone_LeftArm', 'rig_bone_RightArm']
    },
    {
        id: 'elbows',
        label: 'elbows',
        axis: [-1, 0, 0],
        min: 0,
        max: 120,
        bones: ['rig_bone_LeftForearm', 'rig_bone_RightForearm']
    },
    {
        id: 'wrists',
        label: 'wrists',
        axis: [-1, 0, 0],
        min: -45,
        max: 45,
        bones: ['rig_bone_LeftHand', 'rig_bone_RightHand']
    },
    {
        id: 'hips',
        label: 'hips',
        axis: [1, 0, 0],
        min: -35,
        max: 60,
        bones: ['rig_bone_LeftUpleg', 'rig_bone_RightUpleg']
    },
    {id: 'knees', label: 'knees', axis: [1, 0, 0], min: -120, max: 0, bones: ['rig_bone_LeftLeg', 'rig_bone_Rightleg']},
    {
        id: 'ankles',
        label: 'ankles',
        axis: [1, 0, 0],
        min: -30,
        max: 30,
        bones: ['rig_bone_LeftFoot', 'rig_bone_RightFoot']
    },
];

// Re-pose the rig from `state.pose` (id -> degrees). Resets every bone to bind, then layers each non-zero control. No-op
// if the model isn't skinned.
function applyPose(state) {
    if (!state.skinned) return;
    const byName = new Map(state.skinned.skeleton.bones.map(b => [b.name, b]));
    for (const [name, q] of state.bind) byName.get(name)?.quaternion.copy(q); // reset to bind first
    const pose = state.pose ?? {};
    for (const ctl of POSE_CONTROLS) {
        const deg = pose[ctl.id] || 0;
        if (!deg) continue;
        const q = new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(...ctl.axis), deg * Math.PI / 180);
        for (const name of ctl.bones) byName.get(name)?.quaternion.multiply(q);
    }
}

// Enable/disable the synthetic idle animation for a canvas. When turned off, snap the rig back to its bind+pose so
// it holds still instead of freezing mid-breath.
export function setIdle(canvasId, on) {
    const state = states.get(canvasId);
    if (!state) return false;
    state.idle = !!on;
    if (!state.idle && state.skinned) applyPose(state);
    return true;
}

// Set one pose control to `deg` degrees (0 = rest). Driven by the per-joint sliders.
export function setPose(canvasId, id, deg) {
    const state = states.get(canvasId);
    if (!state) return false;
    (state.pose ??= {})[id] = deg || 0;
    if (!state.skinned) return false;
    applyPose(state);
    return true;
}

// Build a labelled range slider per joint into `containerId` and wire each to drive the pose client-side (no Blazor
// round-trip per drag = smooth on mobile). Idempotent: rebuilt each character select, preserving nothing (rest pose).
export function buildPoseControls(canvasId, containerId) {
    const host = document.getElementById(containerId);
    if (!host) return;
    const state = states.get(canvasId);
    if (state) state.pose = {};
    host.innerHTML = '';

    for (const ctl of POSE_CONTROLS) {
        const label = document.createElement('label');
        label.className = 'pose';
        label.append(ctl.label);
        const input = document.createElement('input');
        input.type = 'range';
        input.min = ctl.min;
        input.max = ctl.max;
        input.value = 0;
        input.addEventListener('input', () => setPose(canvasId, ctl.id, +input.value));
        input.addEventListener('dblclick', () => {
            input.value = 0;
            setPose(canvasId, ctl.id, 0);
        });
        label.append(input);
        host.append(label);
    }

    const reset = document.createElement('button');
    reset.type = 'button';
    reset.className = 'pose-reset';
    reset.textContent = 'reset pose';
    reset.addEventListener('click', () => {
        for (const input of host.querySelectorAll('input[type=range]')) input.value = 0;
        if (state) {
            state.pose = {};
            applyPose(state);
        }
    });
    host.append(reset);
}

// Make a floating window draggable by its title bar. Idempotent (guarded by a dataset flag) so it can be re-called
// after each Blazor re-render. Switches the window to left/top positioning on first drag so it moves regardless of the
// initial right-anchored CSS.
export function makeDraggable(windowId, handleId) {
    const win = document.getElementById(windowId);
    const handle = document.getElementById(handleId);
    if (!win || !handle || handle.dataset.dragInit) return;
    handle.dataset.dragInit = '1';

    let startX = 0, startY = 0, originX = 0, originY = 0, dragging = false;
    handle.addEventListener('pointerdown', e => {
        // Don't start a drag when the press lands on a control in the bar (e.g. the collapse button) - the drag's
        // preventDefault + pointer-capture would otherwise swallow its click.
        if (e.target.closest('button, input, select, a')) return;
        dragging = true;
        const r = win.getBoundingClientRect();
        win.style.left = r.left + 'px';
        win.style.top = r.top + 'px';
        // Clear the CSS anchors so ONLY left/top drive position. The pose window is bottom-anchored (bottom:1.25rem);
        // leaving `bottom` set alongside a new `top` stretches the fixed element to span both edges (it grows vertically).
        win.style.right = 'auto';
        win.style.bottom = 'auto';
        startX = e.clientX;
        startY = e.clientY;
        originX = r.left;
        originY = r.top;
        handle.setPointerCapture(e.pointerId);
        e.preventDefault();
    });
    handle.addEventListener('pointermove', e => {
        if (!dragging) return;
        win.style.left = (originX + e.clientX - startX) + 'px';
        win.style.top = (originY + e.clientY - startY) + 'px';
    });
    const end = e => {
        dragging = false;
        try {
            handle.releasePointerCapture(e.pointerId);
        } catch {
        }
    };
    handle.addEventListener('pointerup', end);
    handle.addEventListener('pointercancel', end);
}

// Render an item glb to a PNG data-URL with ONE reused offscreen renderer (so 16 equipment slots don't each take a
// WebGL context). Null on load failure (the item has no display mesh -> the slot keeps its label).
let _thumbRenderer = null;

async function renderItemThumb(url) {
    if (!_thumbRenderer) {
        _thumbRenderer = new THREE.WebGLRenderer({antialias: true, alpha: true, preserveDrawingBuffer: true});
        _thumbRenderer.setSize(96, 96);
    }

    let gltf;
    try {
        gltf = await new GLTFLoader().loadAsync(url);
    } catch {
        return null;
    }

    const scene = new THREE.Scene();
    scene.add(new THREE.HemisphereLight(0xffffff, 0x40404a, 1.4));
    const dl = new THREE.DirectionalLight(0xffffff, 1.3);
    dl.position.set(1.2, 2, 2.2);
    scene.add(dl);

    const model = gltf.scene;
    model.updateWorldMatrix(true, true);
    const box = new THREE.Box3();
    model.traverse(o => {
        if (o.isMesh && o.geometry) {
            o.geometry.computeBoundingBox();
            box.union(o.geometry.boundingBox.clone().applyMatrix4(o.matrixWorld));
            o.material.flatShading = false;
        }
    });
    const sphere = box.getBoundingSphere(new THREE.Sphere());
    model.position.sub(sphere.center);
    scene.add(model);

    const cam = new THREE.PerspectiveCamera(32, 1, 0.001, 100);
    const r = sphere.radius || 1;
    cam.position.set(r * 1.3, r * 0.8, r * 2.1);
    cam.lookAt(0, 0, 0);
    _thumbRenderer.render(scene, cam);
    return _thumbRenderer.domElement.toDataURL('image/png');
}

// Fill each filled slot (.slot[data-item], equipment + inventory) with a rendered thumbnail of its item's 3D model.
// Clears stale thumbnails when a slot empties or changes item (inventory paging reuses the same slot elements).
function clearThumb(slot, img) {
    if (img.dataset.itemDone === undefined && !slot.classList.contains('has-img')) return;
    img.removeAttribute('src');
    delete img.dataset.itemDone;
    slot.classList.remove('has-img');
}

export async function populateItemThumbs() {
    const slots = document.querySelectorAll('.slot[data-item]');

    // Pass 1 (synchronous): drop every stale thumbnail up front, so a paged/reused slot never shows the PREVIOUS
    // item's icon while the new ones render. Without this, slots later in the async loop below keep their old image
    // until the loop reaches them.
    for (const slot of slots) {
        const id = slot.getAttribute('data-item');
        const img = slot.querySelector('.slot-img');
        if (img && img.dataset.itemDone !== id) clearThumb(slot, img);
    }

    // Pass 2 (async): render the thumbnails that are now missing.
    for (const slot of slots) {
        const id = slot.getAttribute('data-item');
        const img = slot.querySelector('.slot-img');
        if (!img || !id || img.dataset.itemDone === id) continue;
        const data = await renderItemThumb(`/api/item/${id}/model.glb`);
        if (data) {
            img.src = data;
            img.dataset.itemDone = id;
            slot.classList.add('has-img');
        }
    }
}

// --- Item tooltips -----------------------------------------------------------
// A single floating panel, populated from the hovered slot's data-* attributes (name/type/level/value/sell/count/id).
// Appended to <body> (outside the Blazor-scoped component) so it can overflow the panels.
let _tip = null;
const esc = s => (s ?? '').replace(/[&<>"]/g, m => ({'&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;'}[m]));

// tin -> g/s/c/t (100 tin = 1 copper, 100 copper = 1 silver, 100 silver = 1 gold) — mirrors the C# wallet formatter.
function fmtMoney(tin) {
    const g = Math.floor(tin / 1e6), s = Math.floor(tin / 1e4) % 100, c = Math.floor(tin / 100) % 100, t = tin % 100;
    const p = [];
    if (g) p.push(g + 'g');
    if (s) p.push(s + 's');
    if (c) p.push(c + 'c');
    if (t || !p.length) p.push(t + 't');
    return p.join(' ');
}

function tipHtml(ds) {
    const out = [`<div class="tt-name">${esc(ds.name)}</div>`];
    const meta = [];
    if (ds.type && ds.type !== 'Normal') meta.push(esc(ds.type));
    if (+ds.level > 0) meta.push('Level ' + ds.level);
    if (meta.length) out.push(`<div class="tt-meta">${meta.join(' · ')}</div>`);
    if (+ds.count > 1) out.push(`<div class="tt-row">Stack of ${ds.count}</div>`);
    if (+ds.sell > 0) out.push(`<div class="tt-row">Sells for ${fmtMoney(+ds.sell)}</div>`);
    else if (+ds.value > 0) out.push(`<div class="tt-row">Value ${ds.value}</div>`);
    out.push(`<div class="tt-id">#${esc(ds.item)}</div>`);
    return out.join('');
}

function moveTip(e) {
    if (!_tip) return;
    const pad = 14, w = _tip.offsetWidth, h = _tip.offsetHeight;
    let x = e.clientX + pad, y = e.clientY + pad;
    if (x + w > window.innerWidth - 8) x = e.clientX - w - pad;
    if (y + h > window.innerHeight - 8) y = e.clientY - h - pad;
    _tip.style.left = Math.max(8, x) + 'px';
    _tip.style.top = Math.max(8, y) + 'px';
}

function hideTip() {
    if (_tip) _tip.style.display = 'none';
}

export function initItemTooltips() {
    if (document._tipInit) return;
    document._tipInit = true;
    _tip = document.createElement('div');
    _tip.className = 'item-tooltip';
    _tip.style.display = 'none';
    document.body.appendChild(_tip);

    const slotAt = t => {
        const s = t?.closest?.('.slot[data-item]');
        return s && s.getAttribute('data-item') ? s : null;
    };
    document.addEventListener('mouseover', e => {
        const s = slotAt(e.target);
        if (!s) {
            hideTip();
            return;
        }
        _tip.innerHTML = tipHtml(s.dataset);
        _tip.style.display = 'block';
        moveTip(e);
    });
    document.addEventListener('mousemove', e => {
        if (_tip.style.display !== 'block') return;
        if (slotAt(e.target)) moveTip(e); else hideTip();
    });
}

// Dev hooks so headless tooling can drive the REAL viewer page (load a model URL + set the pose angle).
if (typeof window !== 'undefined') {
    window.__faolanShow = show;
    window.__faolanSetPose = setPose;
}
