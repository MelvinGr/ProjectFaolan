// Live world radar: polls /api/zone/{mapId} and plots entity positions on a canvas. Drawn entirely client-side so it
// can auto-refresh without Blazor round-trips. Players green, hostile NPCs red, friendly NPCs amber, loot grey.
let _timer = null;
let _canvasId = null;
let _mapId = null;
const RES = 760; // internal canvas resolution (square); CSS scales it to the layout.

export function start(canvasId, mapId) {
    _canvasId = canvasId;
    _mapId = mapId;
    const c = document.getElementById(canvasId);
    if (c) { c.width = RES; c.height = RES; }
    stop();
    poll();
    _timer = setInterval(poll, 3000);
}

export function setMap(mapId) { _mapId = mapId; poll(); }
export function stop() { if (_timer) { clearInterval(_timer); _timer = null; } }

async function poll() {
    if (!_canvasId || _mapId == null) return;
    let data = null;
    try {
        const r = await fetch(`/api/zone/${_mapId}`, { headers: { 'Accept': 'application/json' } });
        if (r.ok) data = await r.json();
    } catch { /* offline */ }
    draw(data);
}

function setStatus(text, cls) {
    const el = document.getElementById('radar-status');
    if (el) { el.textContent = text; el.className = 'radar-status ' + (cls || ''); }
}

function draw(data) {
    const canvas = document.getElementById(_canvasId);
    if (!canvas) return;
    const ctx = canvas.getContext('2d');
    const W = canvas.width, H = canvas.height;

    // Backdrop + grid.
    ctx.fillStyle = '#0c0e11';
    ctx.fillRect(0, 0, W, H);
    ctx.strokeStyle = '#1b2733';
    ctx.lineWidth = 1;
    for (let i = 1; i < 12; i++) {
        const p = (i / 12) * W;
        ctx.beginPath(); ctx.moveTo(p, 0); ctx.lineTo(p, H); ctx.stroke();
        ctx.beginPath(); ctx.moveTo(0, p); ctx.lineTo(W, p); ctx.stroke();
    }
    ctx.strokeStyle = '#2c2a22';
    ctx.strokeRect(0.5, 0.5, W - 1, H - 1);

    if (data === null) { setStatus('Game server offline', 'off'); return; }

    const ents = data.entities || [];
    // Extent: real world bounds when known, else auto-fit to the entities (with padding) so a few NPCs still spread out.
    let minX = 0, maxX = data.bounds ? data.bounds.w : 0, minZ = 0, maxZ = data.bounds ? data.bounds.h : 0;
    if (!data.bounds) {
        if (ents.length === 0) {
            setStatus(data.online ? 'No entities loaded on this map yet' : 'Game server offline', data.online ? '' : 'off');
            return;
        }
        minX = Math.min(...ents.map(e => e.x)); maxX = Math.max(...ents.map(e => e.x));
        minZ = Math.min(...ents.map(e => e.z)); maxZ = Math.max(...ents.map(e => e.z));
        const padX = (maxX - minX) * 0.1 + 5, padZ = (maxZ - minZ) * 0.1 + 5;
        minX -= padX; maxX += padX; minZ -= padZ; maxZ += padZ;
    }
    const spanX = (maxX - minX) || 1, spanZ = (maxZ - minZ) || 1;
    const margin = 14;
    const sx = x => margin + ((x - minX) / spanX) * (W - 2 * margin);
    // Flip Z so larger Z (north) is up.
    const sz = z => margin + (1 - (z - minZ) / spanZ) * (H - 2 * margin);

    const colors = { player: '#5fd06a', loot: '#9a937f' };
    let players = 0, npcs = 0, loot = 0;

    // Loot first (lowest), then NPCs, players on top.
    const order = e => (e.kind === 'player' ? 2 : e.kind === 'loot' ? 0 : 1);
    for (const e of [...ents].sort((a, b) => order(a) - order(b))) {
        const x = sx(e.x), y = sz(e.z);
        let color = e.kind === 'player' ? colors.player
            : e.kind === 'loot' ? colors.loot
                : (e.hostile ? '#e0553f' : '#d8b53f');
        if (e.dead) color = '#555';
        const r = e.kind === 'player' ? 6 : e.kind === 'loot' ? 3 : 4;

        if (e.kind === 'player') { players++; ctx.shadowColor = color; ctx.shadowBlur = 8; }
        else { e.kind === 'loot' ? loot++ : npcs++; ctx.shadowBlur = 0; }

        ctx.beginPath();
        ctx.arc(x, y, r, 0, Math.PI * 2);
        ctx.fillStyle = color;
        ctx.fill();
        ctx.shadowBlur = 0;

        if (e.kind === 'player') {
            ctx.fillStyle = '#e7e2d2';
            ctx.font = '12px system-ui, sans-serif';
            ctx.fillText(e.name || '?', x + 9, y + 4);
        }
    }

    setStatus(`${players} player${players === 1 ? '' : 's'} · ${npcs} NPC${npcs === 1 ? '' : 's'} · ${loot} loot`
        + (data.bounds ? ` · ${data.bounds.w}×${data.bounds.h}` : ' · auto-scaled'), 'on');
}
