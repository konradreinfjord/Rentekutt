// Enkel dra-og-slipp + tap-deteksjon for flyt-boardet. Blazor eier posisjonene;
// JS flytter elementet visuelt under draging og rapporterer ny posisjon ved slipp.
let dn = null;

export function init(dotNet) { dn = dotNet; }

export function attachAll() {
    document.querySelectorAll('.flyt-node').forEach(el => {
        if (el._flytBound) return;
        el._flytBound = true;
        bind(el, el.dataset.flytid);
    });
}

function bind(el, id) {
    let sx = 0, sy = 0, ox = 0, oy = 0, dragging = false;
    el.addEventListener('pointerdown', e => {
        if (e.button !== 0) return;
        dragging = true;
        el.setPointerCapture(e.pointerId);
        sx = e.clientX; sy = e.clientY;
        ox = parseFloat(el.style.left) || 0;
        oy = parseFloat(el.style.top) || 0;
        el.style.zIndex = '200';
        e.preventDefault();
    });
    el.addEventListener('pointermove', e => {
        if (!dragging) return;
        el.style.left = (ox + (e.clientX - sx)) + 'px';
        el.style.top = (oy + (e.clientY - sy)) + 'px';
    });
    el.addEventListener('pointerup', e => {
        if (!dragging) return;
        dragging = false;
        try { el.releasePointerCapture(e.pointerId); } catch { }
        el.style.zIndex = '';
        const moved = Math.abs(e.clientX - sx) + Math.abs(e.clientY - sy);
        const nx = Math.max(0, parseFloat(el.style.left) || 0);
        const ny = Math.max(0, parseFloat(el.style.top) || 0);
        if (moved < 5) dn?.invokeMethodAsync('NodeKlikk', id);
        else dn?.invokeMethodAsync('OppdaterPosisjon', id, nx, ny);
    });
}
