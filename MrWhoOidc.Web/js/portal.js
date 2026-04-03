(function () {
    const oidcAuthority = "https://mrwho.onrender.com/t/default";
    const clientId = "mrwho-portal";
    const redirectUri = new URL("portal.html", window.location.href).toString();
    const scope = "openid profile email";

    const loginButton = document.getElementById("login-button");
    const logoutButton = document.getElementById("logout-button");
    const portalAlert = document.getElementById("portal-alert");
    const portalState = document.getElementById("portal-state");
    const sessionStatus = document.getElementById("session-status");
    const sessionUser = document.getElementById("session-user");
    const sessionSubject = document.getElementById("session-subject");

    function decodeJwtPayload(token) {
        if (!token || token.split(".").length < 2) {
            return null;
        }

        try {
            const encoded = token.split(".")[1].replace(/-/g, "+").replace(/_/g, "/");
            const padded = encoded + "=".repeat((4 - encoded.length % 4) % 4);
            const json = atob(padded);
            return JSON.parse(json);
        } catch (error) {
            console.warn("Failed to decode token payload", error);
            return null;
        }
    }

    function buildAuthorizeUrl() {
        const state = crypto.randomUUID();
        const nonce = crypto.randomUUID();
        const verifier = crypto.randomUUID() + crypto.randomUUID();

        sessionStorage.setItem("mrwho.portal.state", state);
        sessionStorage.setItem("mrwho.portal.nonce", nonce);
        sessionStorage.setItem("mrwho.portal.verifier", verifier);

        const params = new URLSearchParams({
            client_id: clientId,
            response_type: "code",
            redirect_uri: redirectUri,
            scope,
            state,
            nonce,
            code_challenge_method: "plain",
            code_challenge: verifier
        });

        return `${oidcAuthority}/connect/authorize?${params.toString()}`;
    }

    function setAuthenticatedState(payload, code) {
        sessionStatus.textContent = code ? "Authorization returned" : "Authenticated token present";
        sessionUser.textContent = payload?.email || payload?.name || "-";
        sessionSubject.textContent = payload?.sub || "-";
        loginButton.classList.add("d-none");
        logoutButton.classList.remove("d-none");
        portalAlert.className = "alert alert-success";
        portalAlert.textContent = "Authenticated portal session detected. Next implementation step is wiring API calls to MrWhoLicensing.";
        portalState.textContent = JSON.stringify({
            status: "authenticated",
            code,
            claims: payload || null,
            licensingApiTargets: [
                "/api/organizations",
                "/api/license-requests",
                "/api/payment-records"
            ]
        }, null, 2);
    }

    function setAnonymousState() {
        sessionStatus.textContent = "Anonymous";
        sessionUser.textContent = "-";
        sessionSubject.textContent = "-";
        loginButton.classList.remove("d-none");
        logoutButton.classList.add("d-none");
        portalAlert.className = "alert alert-info";
        portalAlert.textContent = "Sign in to load your customer portal data.";
        portalState.textContent = JSON.stringify({
            status: "anonymous",
            authority: oidcAuthority,
            clientId,
            redirectUri
        }, null, 2);
    }

    loginButton?.addEventListener("click", function () {
        window.location.href = buildAuthorizeUrl();
    });

    logoutButton?.addEventListener("click", function () {
        sessionStorage.removeItem("mrwho.portal.code");
        sessionStorage.removeItem("mrwho.portal.payload");
        setAnonymousState();
    });

    const url = new URL(window.location.href);
    const code = url.searchParams.get("code");
    const state = url.searchParams.get("state");
    const expectedState = sessionStorage.getItem("mrwho.portal.state");

    if (code) {
        if (!state || state !== expectedState) {
            portalAlert.className = "alert alert-danger";
            portalAlert.textContent = "OIDC callback state validation failed.";
            setAnonymousState();
            return;
        }

        const payload = decodeJwtPayload(sessionStorage.getItem("mrwho.portal.payload"));
        sessionStorage.setItem("mrwho.portal.code", code);
        setAuthenticatedState(payload, code);
        url.searchParams.delete("code");
        url.searchParams.delete("state");
        url.searchParams.delete("iss");
        window.history.replaceState({}, document.title, url.toString());
        return;
    }

    const storedCode = sessionStorage.getItem("mrwho.portal.code");
    const payload = decodeJwtPayload(sessionStorage.getItem("mrwho.portal.payload"));
    if (storedCode) {
        setAuthenticatedState(payload, storedCode);
        return;
    }

    setAnonymousState();
})();
