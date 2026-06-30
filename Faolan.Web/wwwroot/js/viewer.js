// three.js glTF viewer for the Faolan character/model pages (#275). ES module loaded via Blazor JS interop.
import * as THREE from 'three';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';

const states = new Map(); // canvasId -> { renderer, scene, camera, controls, model }
if (typeof window !== 'undefined') window.__faolanStates = states; // dev hook (headless camera control)

function setup(canvas) {
    const renderer = new THREE.WebGLRenderer({ canvas, antialias: true });
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

    const state = { renderer, scene, camera, controls, model: null };
    states.set(canvas.id, state);

    function resize() {
        const w = canvas.clientWidth, h = canvas.clientHeight;
        if (w === 0 || h === 0) return;
        renderer.setSize(w, h, false);
        camera.aspect = w / h;
        camera.updateProjectionMatrix();
    }
    window.addEventListener('resize', resize);
    resize();

    (function loop() {
        requestAnimationFrame(loop);
        controls.update();
        renderer.render(scene, camera);
    })();

    return state;
}

// Load (or reload) a glb URL into the named canvas, framing the camera to the model.
export async function show(canvasId, url) {
    const canvas = document.getElementById(canvasId);
    if (!canvas) return;
    const state = states.get(canvasId) ?? setup(canvas);

    if (state.model) {
        state.scene.remove(state.model);
        state.model = null;
    }

    const gltf = await new GLTFLoader().loadAsync(url);
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
    if (state.poseDeg) applyDemoPose(state, state.poseDeg); // re-apply the current pose angle across a reload

    state.scene.add(model);
    state.model = model;
    state.modelSize = size; // dev hook: lets headless tooling frame the head

    // Frame the whole model for the CURRENT aspect (portrait phones are width-limited, so fit both axes of the bounding
    // sphere - otherwise a tall body gets cropped to just the legs). Resize the renderer + aspect to the live canvas
    // first: on mobile the layout often hasn't settled when setup() ran, leaving a stale (square) projection.
    const sphere = box.getBoundingSphere(new THREE.Sphere());
    const cw = canvas.clientWidth || canvas.width, chh = canvas.clientHeight || canvas.height;
    if (cw > 0 && chh > 0) {
        state.renderer.setSize(cw, chh, false);
        state.camera.aspect = cw / chh;
        state.camera.updateProjectionMatrix();
    }
    const vFov = state.camera.fov * Math.PI / 180;
    const hFov = 2 * Math.atan(Math.tan(vFov / 2) * state.camera.aspect);
    const dist = Math.max(sphere.radius / Math.sin(vFov / 2), sphere.radius / Math.sin(hFov / 2)) * 1.08;
    const cy = size.y / 2;
    state.controls.target.set(0, cy, 0);
    state.camera.position.set(0, cy, dist);
    state.controls.update();
}

// Pose a loaded rig by bending both elbows `deg` degrees about each forearm's local -X hinge (0 = bind/rest). The delta
// is composed onto each bone's BIND rotation; the render loop refreshes world matrices + the skeleton every frame, so
// children follow correctly. No-op if the model isn't skinned.
function applyDemoPose(state, deg) {
    if (!state.skinned) return;
    const byName = new Map(state.skinned.skeleton.bones.map(b => [b.name, b]));
    for (const [name, q] of state.bind) byName.get(name)?.quaternion.copy(q); // reset to bind first
    if (deg)
        for (const name of ['rig_bone_LeftForearm', 'rig_bone_RightForearm']) {
            const b = byName.get(name);
            if (b) b.quaternion.multiply(new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(-1, 0, 0), deg * Math.PI / 180));
        }
}

// Set the rig's elbow pose to `deg` degrees (0 = rest). Driven by the viewer page's pose slider.
export function setPose(canvasId, deg) {
    const state = states.get(canvasId);
    if (!state) return false;
    state.poseDeg = deg || 0;
    if (!state.skinned) return false;
    applyDemoPose(state, state.poseDeg);
    return true;
}

// Wire a range <input> to drive the pose directly on the client (no Blazor round-trip per drag = smooth on mobile).
export function bindPoseSlider(canvasId, sliderId) {
    const el = document.getElementById(sliderId);
    if (el && !el.dataset.bound) {
        el.dataset.bound = '1';
        el.addEventListener('input', () => setPose(canvasId, +el.value));
    }
}

// Render an item glb to a PNG data-URL with ONE reused offscreen renderer (so 16 equipment slots don't each take a
// WebGL context). Null on load failure (the item has no display mesh -> the slot keeps its label).
let _thumbRenderer = null;
async function renderItemThumb(url) {
    if (!_thumbRenderer) {
        _thumbRenderer = new THREE.WebGLRenderer({ antialias: true, alpha: true, preserveDrawingBuffer: true });
        _thumbRenderer.setSize(96, 96);
    }

    let gltf;
    try { gltf = await new GLTFLoader().loadAsync(url); }
    catch { return null; }

    const scene = new THREE.Scene();
    scene.add(new THREE.HemisphereLight(0xffffff, 0x40404a, 1.4));
    const dl = new THREE.DirectionalLight(0xffffff, 1.3); dl.position.set(1.2, 2, 2.2); scene.add(dl);

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

// Fill each filled equipment slot (.slot[data-item]) with a rendered thumbnail of its item's 3D model.
export async function populateItemThumbs() {
    for (const slot of document.querySelectorAll('.slot[data-item]')) {
        const id = slot.getAttribute('data-item');
        const img = slot.querySelector('.slot-img');
        if (!id || !img || img.dataset.itemDone === id)
            continue;
        const data = await renderItemThumb(`/api/item/${id}/model.glb`);
        if (data) {
            img.src = data;
            img.dataset.itemDone = id;
            slot.classList.add('has-img');
        }
    }
}

// Dev hooks so headless tooling can drive the REAL viewer page (load a model URL + set the pose angle).
if (typeof window !== 'undefined') {
    window.__faolanShow = show;
    window.__faolanSetPose = setPose;
}
