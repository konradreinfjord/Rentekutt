// Felt-nivå låsing på kundekortet.
// Alle felt i .kort-felter.felt-laas er låst (via CSS). Dobbeltklikk et felt for
// å låse opp NETTOPP det feltet; det låses igjen når du forlater det (blur).
// Klassen «aapen» styres kun her (ikke av Blazor), så Blazor-render rører den ikke.
(function () {
    if (window.__feltLaasInit) return;
    window.__feltLaasInit = true;

    document.addEventListener('dblclick', function (e) {
        var host = e.target.closest && e.target.closest('.kort-felter.felt-laas');
        if (!host) return;
        var felt = e.target.closest('.ff, .ff-check');
        if (!felt || !host.contains(felt)) return;
        felt.classList.add('aapen');
        var ctrl = felt.querySelector('input, select, textarea');
        if (ctrl) { try { ctrl.focus(); if (ctrl.select) ctrl.select(); } catch (_) {} }
    });

    document.addEventListener('focusout', function (e) {
        var felt = e.target.closest && e.target.closest('.kort-felter.felt-laas .ff.aapen, .kort-felter.felt-laas .ff-check.aapen');
        if (felt) felt.classList.remove('aapen');
    });
})();
