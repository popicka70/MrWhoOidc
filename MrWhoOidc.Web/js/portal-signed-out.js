// Clears any portal session state left in this browser. Kept as an external file so the
// portal CSP can forbid inline scripts.
(function () {
    try {
        sessionStorage.removeItem('mrwho.portal.session');
        sessionStorage.removeItem('mrwho.portal.pkce');
        // Older portal builds kept tokens in localStorage; remove any leftover copy.
        localStorage.removeItem('mrwho.portal.session');
    } catch {
        // Storage can be unavailable (privacy mode, blocked site data); nothing to clear then.
    }
})();
