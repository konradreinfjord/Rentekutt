// Hover-crosshair for Markedsinnsikt-grafene: viser rentenivå ved musepekeren.
// Grafene bruker viewBox 900x340, preserveAspectRatio="none" (lineær skalering),
// PadT=14, PlotH=300. yMax leses fra data-ymax på <svg>.
window.miChart = {
    initById: function (svgId, tipId) {
        this.bind(document.getElementById(svgId), document.getElementById(tipId));
    },
    bind: function (svg, tip) {
        if (!svg || svg._miBound) return;
        svg._miBound = true;
        const H = 340, padT = 14, plotH = 300;
        const yMax = parseFloat(svg.dataset.ymax) || 8;
        const cross = svg.querySelector('.mi-cross');

        svg.addEventListener('mousemove', function (e) {
            const r = svg.getBoundingClientRect();
            if (r.height === 0) return;
            const yView = ((e.clientY - r.top) / r.height) * H;
            let rate = yMax * (1 - (yView - padT) / plotH);
            if (rate < 0) rate = 0;
            if (rate > yMax) rate = yMax;
            if (cross) {
                cross.setAttribute('y1', yView);
                cross.setAttribute('y2', yView);
                cross.style.display = 'block';
            }
            if (tip) {
                tip.style.display = 'block';
                tip.style.left = (e.clientX - r.left) + 'px';
                tip.style.top = (e.clientY - r.top) + 'px';
                tip.textContent = rate.toFixed(2).replace('.', ',') + ' %';
            }
        });
        svg.addEventListener('mouseleave', function () {
            if (cross) cross.style.display = 'none';
            if (tip) tip.style.display = 'none';
        });
    }
};
