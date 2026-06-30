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

    const box = new THREE.Box3().setFromObject(model);
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
    if (state.poseOn) applyDemoPose(state); // re-apply if posing was left enabled across a reload

    state.scene.add(model);
    state.model = model;
    state.modelSize = size; // dev hook: lets headless tooling frame the head

    const radius = Math.max(size.x, size.y, size.z);
    state.controls.target.set(0, size.y / 2, 0);
    state.camera.position.set(0, size.y * 0.6, radius * 2.2);
    state.controls.update();
}

// Apply a fixed demo pose to a loaded rig so the skeleton is visibly articulated (both forearms forward+up about
// their local -X hinge - the anatomically clean elbow bend). Composes the delta onto each bone's BIND rotation; the
// render loop (renderer.render every frame) refreshes world matrices + the skeleton, so children follow correctly.
function applyDemoPose(state) {
    if (!state.skinned) return;
    const byName = new Map(state.skinned.skeleton.bones.map(b => [b.name, b]));
    for (const [name, q] of state.bind) byName.get(name).quaternion.copy(q); // reset to bind first
    const bend = (name, axis, deg) => {
        const b = byName.get(name);
        if (!b) return;
        b.quaternion.multiply(new THREE.Quaternion().setFromAxisAngle(new THREE.Vector3(...axis).normalize(), deg * Math.PI / 180));
    };
    bend('rig_bone_LeftForearm', [-1, 0, 0], 72);
    bend('rig_bone_RightForearm', [-1, 0, 0], 72);
}

// Toggle the demo pose on a canvas's rig (no-op if the loaded model isn't skinned).
export function setPose(canvasId, on) {
    const state = states.get(canvasId);
    if (!state) return false;
    state.poseOn = on;
    if (!state.skinned) return false;
    if (on) applyDemoPose(state);
    else for (const [name, q] of state.bind) state.skinned.skeleton.getBoneByName(name).quaternion.copy(q);
    return true;
}

// Dev hooks so headless tooling can drive the REAL viewer page (load a model URL + toggle the pose).
if (typeof window !== 'undefined') {
    window.__faolanShow = show;
    window.__faolanSetPose = setPose;
}
