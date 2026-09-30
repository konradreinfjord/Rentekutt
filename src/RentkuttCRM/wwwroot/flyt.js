// Flyt-board: dra noder (zoom-bevisst), kantete/egendefinerte piler, og punktvis ruting.
let dn = null;
let wpEdge = null;   // hvilken kant vi legger veipunkter på (null = av)

export function init(dotNet) { dn = dotNet; bindCanvas(); }

function canvasEl() { return document.querySelector('.flyt-canvas'); }
function contentEl() { return document.querySelector('.flyt-content'); }
function zoom() { const c = canvasEl(); return c ? (parseFloat(c.dataset.zoom) || 1) : 1; }
function off() { const c = canvasEl(); return c ? (parseFloat(c.dataset.off) || 0) : 0; }

export function attachAll() {
    document.querySelectorAll('.flyt-node').forEach(el => {
        if (el._flytBound) return;
        el._flytBound = true;
        bind(el, el.dataset.flytid);
    });
    bindCanvas();
    redrawEdges();
    scrollTilInnhold();
}

// Rull lerretet én gang så nodene vises (ikke den tomme margen til venstre/oppe).
let scrollet = false;
function scrollTilInnhold() {
    if (scrollet) return;
    const canvas = canvasEl();
    const nodes = document.querySelectorAll('.flyt-node');
    if (!canvas || nodes.length === 0) return;
    scrollet = true;
    const z = zoom();
    let minX = Infinity, minY = Infinity;
    nodes.forEach(n => { minX = Math.min(minX, n.offsetLeft); minY = Math.min(minY, n.offsetTop); });
    canvas.scrollLeft = Math.max(0, (minX - 60) * z);
    canvas.scrollTop = Math.max(0, (minY - 60) * z);
}

// Klikk på lerretet for å legge veipunkt (når en kant er valgt for ruting).
function bindCanvas() {
    const content = contentEl();
    if (!content || content._flytCanvasBound) return;
    content._flytCanvasBound = true;
    content.addEventListener('pointerdown', e => {
        if (wpEdge === null) return;
        if (e.target.closest('.flyt-node') || e.target.closest('.flyt-kant-etikett')) return;
        const rect = content.getBoundingClientRect();
        const z = zoom();
        const x = Math.round((e.clientX - rect.left) / z);
        const y = Math.round((e.clientY - rect.top) / z);
        dn?.invokeMethodAsync('LeggTilPunkt', wpEdge, x, y);
    });
}

export function settWaypointModus(edgeIdx) {
    wpEdge = (edgeIdx === null || edgeIdx < 0) ? null : edgeIdx;
    contentEl()?.classList.toggle('wp-mode', wpEdge !== null);
}

function bind(el, id) {
    let sx = 0, sy = 0, ox = 0, oy = 0, dragging = false;
    el.addEventListener('pointerdown', e => {
        if (e.button !== 0 || wpEdge !== null) return;
        dragging = true;
        el.setPointerCapture(e.pointerId);
        sx = e.clientX; sy = e.clientY;
        ox = parseFloat(el.style.left) || 0;
        oy = parseFloat(el.style.top) || 0;
        el.style.zIndex = '200';
        e.preventDefault();
        e.stopPropagation();
    });
    el.addEventListener('pointermove', e => {
        if (!dragging) return;
        const z = zoom();
        let nx = Math.max(0, ox + (e.clientX - sx) / z);
        let ny = Math.max(0, oy + (e.clientY - sy) / z);
        // Snap til andre noders senter (symmetri).
        const w = el.offsetWidth, h = el.offsetHeight;
        const T = 9;
        const cx = nx + w / 2, cy = ny + h / 2;
        let snapX = null, snapY = null;
        document.querySelectorAll('.flyt-node').forEach(o => {
            if (o === el) return;
            const ocx = o.offsetLeft + o.offsetWidth / 2, ocy = o.offsetTop + o.offsetHeight / 2;
            if (snapX === null && Math.abs(cx - ocx) <= T) snapX = ocx;
            if (snapY === null && Math.abs(cy - ocy) <= T) snapY = ocy;
        });
        if (snapX !== null) nx = snapX - w / 2;
        if (snapY !== null) ny = snapY - h / 2;
        el.style.left = nx + 'px';
        el.style.top = ny + 'px';
        el.classList.toggle('flyt-snap', snapX !== null || snapY !== null);
        redrawEdges();
    });
    el.addEventListener('pointerup', e => {
        if (!dragging) return;
        dragging = false;
        try { el.releasePointerCapture(e.pointerId); } catch { }
        el.style.zIndex = '';
        el.classList.remove('flyt-snap');
        const moved = Math.abs(e.clientX - sx) + Math.abs(e.clientY - sy);
        const nx = Math.max(0, parseFloat(el.style.left) || 0);
        const ny = Math.max(0, parseFloat(el.style.top) || 0);
        const o = off();
        if (moved < 5) dn?.invokeMethodAsync('NodeKlikk', id);
        else dn?.invokeMethodAsync('OppdaterPosisjon', id, nx - o, ny - o);
    });
}

// Fast festepunkt på en gitt side av noden ('t','r','b','l'). Returnerer også utgangsretning.
function ankerPunkt(el, side) {
    const l = el.offsetLeft, t = el.offsetTop, w = el.offsetWidth, h = el.offsetHeight;
    switch (side) {
        case 't': return { x: l + w / 2, y: t, dir: 'v' };
        case 'b': return { x: l + w / 2, y: t + h, dir: 'v' };
        case 'l': return { x: l, y: t + h / 2, dir: 'h' };
        case 'r': return { x: l + w, y: t + h / 2, dir: 'h' };
    }
    return null;
}

// Kantet rute mellom to eksplisitte punkter, gitt utgangsretning i hver ende.
function rutePath(sp, ep) {
    const sx = sp.x, sy = sp.y, ex = ep.x, ey = ep.y;
    // Begge vertikale, eller begge horisontale → dobbel albue.
    if (sp.dir === 'v' && ep.dir === 'v') {
        const my = (sy + ey) / 2;
        return `M ${sx} ${sy} L ${sx} ${my} L ${ex} ${my} L ${ex} ${ey}`;
    }
    if (sp.dir === 'h' && ep.dir === 'h') {
        const mx = (sx + ex) / 2;
        return `M ${sx} ${sy} L ${mx} ${sy} L ${mx} ${ey} L ${ex} ${ey}`;
    }
    // Blandet: gå ut i startens retning, så inn i endens retning (én albue).
    if (sp.dir === 'v') return `M ${sx} ${sy} L ${sx} ${ey} L ${ex} ${ey}`;
    return `M ${sx} ${sy} L ${ex} ${sy} L ${ex} ${ey}`;
}

// Punkt på nodeboksens kant i retning mot (tx,ty).
function boxEdge(el, tx, ty) {
    const cx = el.offsetLeft + el.offsetWidth / 2, cy = el.offsetTop + el.offsetHeight / 2;
    const dx = tx - cx, dy = ty - cy;
    if (dx === 0 && dy === 0) return { x: cx, y: cy };
    const hw = el.offsetWidth / 2, hh = el.offsetHeight / 2;
    const sx = dx === 0 ? Infinity : hw / Math.abs(dx);
    const sy = dy === 0 ? Infinity : hh / Math.abs(dy);
    const t = Math.min(sx, sy);
    return { x: cx + dx * t, y: cy + dy * t };
}

export function redrawEdges() {
    const canvas = canvasEl();
    if (!canvas) return;
    const nodes = {};
    canvas.querySelectorAll('.flyt-node').forEach(el => nodes[el.dataset.flytid] = el);
    canvas.querySelectorAll('.flyt-edge').forEach(p => {
        const a = nodes[p.dataset.from], b = nodes[p.dataset.to];
        if (!a || !b) { p.removeAttribute('d'); return; }
        let punkter = [];
        try { punkter = JSON.parse(p.dataset.punkter || '[]'); } catch { }
        const acx = a.offsetLeft + a.offsetWidth / 2, acy = a.offsetTop + a.offsetHeight / 2;
        const bcx = b.offsetLeft + b.offsetWidth / 2, bcy = b.offsetTop + b.offsetHeight / 2;
        let d, mx, my;

        const fa = p.dataset.fraanker || '';
        const ta = p.dataset.tilanker || '';

        if (punkter.length > 0) {
            const start = fa ? ankerPunkt(a, fa) : boxEdge(a, punkter[0].x, punkter[0].y);
            const end = ta ? ankerPunkt(b, ta) : boxEdge(b, punkter[punkter.length - 1].x, punkter[punkter.length - 1].y);
            d = `M ${start.x} ${start.y}`;
            punkter.forEach(pt => d += ` L ${pt.x} ${pt.y}`);
            d += ` L ${end.x} ${end.y}`;
            const mid = punkter[Math.floor((punkter.length - 1) / 2)];
            mx = mid.x; my = mid.y;
        } else if (fa || ta) {
            // Eksplisitt festeside i minst én ende → kantet rute mellom festepunktene.
            const dx = bcx - acx, dy = bcy - acy;
            const autoDir = Math.abs(dy) >= Math.abs(dx) ? 'v' : 'h';
            const sp = fa ? ankerPunkt(a, fa)
                : (() => { const e = boxEdge(a, bcx, bcy); return { x: e.x, y: e.y, dir: autoDir }; })();
            const ep = ta ? ankerPunkt(b, ta)
                : (() => { const e = boxEdge(b, acx, acy); return { x: e.x, y: e.y, dir: autoDir }; })();
            d = rutePath(sp, ep);
            mx = (sp.x + ep.x) / 2; my = (sp.y + ep.y) / 2;
        } else {
            const dx = bcx - acx, dy = bcy - acy;
            const ret = p.dataset.retning || '';
            const vertical = ret === 'v' ? true : ret === 'h' ? false : Math.abs(dy) >= Math.abs(dx);
            if (vertical) {
                const sy = dy >= 0 ? a.offsetTop + a.offsetHeight : a.offsetTop;
                const ey = dy >= 0 ? b.offsetTop : b.offsetTop + b.offsetHeight;
                const midY = (sy + ey) / 2;
                d = `M ${acx} ${sy} L ${acx} ${midY} L ${bcx} ${midY} L ${bcx} ${ey}`;
                mx = (acx + bcx) / 2; my = midY;
            } else {
                const sx = dx >= 0 ? a.offsetLeft + a.offsetWidth : a.offsetLeft;
                const ex = dx >= 0 ? b.offsetLeft : b.offsetLeft + b.offsetWidth;
                const midX = (sx + ex) / 2;
                d = `M ${sx} ${acy} L ${midX} ${acy} L ${midX} ${bcy} L ${ex} ${bcy}`;
                mx = midX; my = (acy + bcy) / 2;
            }
        }
        p.setAttribute('d', d);
        const hit = canvas.querySelector(`.flyt-edge-hit[data-edge="${p.dataset.edge}"]`);
        if (hit) hit.setAttribute('d', d);
        const lbl = canvas.querySelector(`.flyt-kant-etikett[data-edge="${p.dataset.edge}"]`);
        if (lbl) { lbl.style.left = mx + 'px'; lbl.style.top = my + 'px'; }
    });
}
