const portalConfig = {
    authority: 'https://localhost:8443/t/default',
    clientId: 'portal-web',
    redirectUri: `${window.location.origin}/portal-callback.html`,
    postLogoutRedirectUri: `${window.location.origin}/portal.html`,
    licensingApiBaseUrl: 'https://localhost:7443',
    scope: 'openid profile email offline_access',
    storageKey: 'mrwho.portal.session',
    pkceKey: 'mrwho.portal.pkce'
};

const pageState = {
    session: null,
    user: null,
    me: null
};

function randomString(length) {
    const bytes = new Uint8Array(length);
    crypto.getRandomValues(bytes);
    return Array.from(bytes, byte => ('0' + (byte % 36).toString(36)).slice(-1)).join('');
}

async function sha256(input) {
    const data = new TextEncoder().encode(input);
    const digest = await crypto.subtle.digest('SHA-256', data);
    return new Uint8Array(digest);
}

function base64UrlEncode(bytes) {
    return btoa(String.fromCharCode(...bytes))
        .replace(/\+/g, '-')
        .replace(/\//g, '_')
        .replace(/=+$/g, '');
}

async function createPkce() {
    const verifier = randomString(64);
    const challenge = base64UrlEncode(await sha256(verifier));
    const state = crypto.randomUUID();
    const nonce = crypto.randomUUID();
    const payload = { verifier, state, nonce, createdAt: Date.now() };
    sessionStorage.setItem(portalConfig.pkceKey, JSON.stringify(payload));
    return { verifier, challenge, state, nonce };
}

function getPkce() {
    const raw = sessionStorage.getItem(portalConfig.pkceKey);
    return raw ? JSON.parse(raw) : null;
}

function saveSession(session) {
    localStorage.setItem(portalConfig.storageKey, JSON.stringify(session));
    pageState.session = session;
}

function clearSession() {
    localStorage.removeItem(portalConfig.storageKey);
    sessionStorage.removeItem(portalConfig.pkceKey);
    pageState.session = null;
    pageState.user = null;
    pageState.me = null;
}

function loadSession() {
    const raw = localStorage.getItem(portalConfig.storageKey);
    pageState.session = raw ? JSON.parse(raw) : null;

    if (pageState.session?.idToken) {
        const accessClaims = pageState.session.claims || {};
        const idClaims = parseJwt(pageState.session.idToken) || {};
        pageState.session.claims = {
            ...accessClaims,
            ...idClaims,
            sub: accessClaims.sub || idClaims.sub,
            email: accessClaims.email || idClaims.email || idClaims.preferred_username,
            name: accessClaims.name || idClaims.name
        };
    }

    return pageState.session;
}

function parseJwt(token) {
    const [, payload] = token.split('.');
    if (!payload) {
        return null;
    }

    const decoded = atob(payload.replace(/-/g, '+').replace(/_/g, '/'));
    return JSON.parse(decoded);
}

async function fetchJson(url, options = {}) {
    const response = await fetch(url, options);
    const text = await response.text();
    const body = text ? JSON.parse(text) : null;
    if (!response.ok) {
        const error = new Error(body?.title || body?.detail || `Request failed with status ${response.status}`);
        error.status = response.status;
        error.body = body;
        throw error;
    }

    return body;
}

function setText(id, value) {
    const node = document.getElementById(id);
    if (node) {
        node.textContent = value ?? '';
    }
}

function setVisible(id, visible) {
    const node = document.getElementById(id);
    if (node) {
        node.classList.toggle('d-none', !visible);
    }
}

function renderAlert(kind, message) {
    const node = document.getElementById('portal-alert');
    if (!node) {
        return;
    }

    if (!message) {
        node.className = 'alert d-none';
        node.textContent = '';
        return;
    }

    node.className = `alert alert-${kind}`;
    node.textContent = message;
}

async function beginLogin() {
    const metadata = await fetchJson(`${portalConfig.authority}/.well-known/openid-configuration`);
    const pkce = await createPkce();
    const url = new URL(metadata.authorization_endpoint);
    url.searchParams.set('client_id', portalConfig.clientId);
    url.searchParams.set('redirect_uri', portalConfig.redirectUri);
    url.searchParams.set('response_type', 'code');
    url.searchParams.set('scope', portalConfig.scope);
    url.searchParams.set('code_challenge', pkce.challenge);
    url.searchParams.set('code_challenge_method', 'S256');
    url.searchParams.set('state', pkce.state);
    url.searchParams.set('nonce', pkce.nonce);
    window.location.assign(url.toString());
}

async function finishLogin() {
    const params = new URLSearchParams(window.location.search);
    const code = params.get('code');
    const state = params.get('state');
    const error = params.get('error');
    if (error) {
        throw new Error(params.get('error_description') || error);
    }
    if (!code || !state) {
        throw new Error('Missing authorization response values.');
    }

    const pkce = getPkce();
    if (!pkce || pkce.state !== state) {
        throw new Error('OIDC state validation failed.');
    }

    const metadata = await fetchJson(`${portalConfig.authority}/.well-known/openid-configuration`);
    const body = new URLSearchParams();
    body.set('grant_type', 'authorization_code');
    body.set('client_id', portalConfig.clientId);
    body.set('code', code);
    body.set('redirect_uri', portalConfig.redirectUri);
    body.set('code_verifier', pkce.verifier);

    const response = await fetch(metadata.token_endpoint, {
        method: 'POST',
        headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
        body
    });

    const tokenResponse = await response.json();
    if (!response.ok) {
        throw new Error(tokenResponse.error_description || tokenResponse.error || 'Token exchange failed.');
    }

    const accessClaims = parseJwt(tokenResponse.access_token) || {};
    const idClaims = tokenResponse.id_token ? (parseJwt(tokenResponse.id_token) || {}) : {};
    const claims = {
        ...accessClaims,
        ...idClaims,
        sub: accessClaims.sub || idClaims.sub,
        email: idClaims.email || accessClaims.email || idClaims.preferred_username || accessClaims.preferred_username,
        name: idClaims.name || accessClaims.name
    };
    saveSession({
        accessToken: tokenResponse.access_token,
        idToken: tokenResponse.id_token,
        refreshToken: tokenResponse.refresh_token,
        expiresAt: Date.now() + (tokenResponse.expires_in * 1000),
        claims
    });

    sessionStorage.removeItem(portalConfig.pkceKey);
    window.location.replace('/portal.html');
}

function beginLogout() {
    const endSessionUrl = new URL(`${portalConfig.authority}/connect/endsession`);
    endSessionUrl.searchParams.set('post_logout_redirect_uri', portalConfig.postLogoutRedirectUri);
    if (pageState.session?.idToken) {
        endSessionUrl.searchParams.set('id_token_hint', pageState.session.idToken);
    }

    clearSession();
    window.location.assign(endSessionUrl.toString());
}

async function authorizedJson(path, init = {}) {
    const session = loadSession();
    if (!session?.accessToken) {
        throw new Error('You are not signed in.');
    }

    return fetchJson(`${portalConfig.licensingApiBaseUrl}${path}`, {
        ...init,
        headers: {
            'Authorization': `Bearer ${session.accessToken}`,
            'Content-Type': 'application/json',
            ...(init.headers || {})
        }
    });
}

async function loadPortalContext() {
    try {
        const me = await authorizedJson('/api/portal/me');
        pageState.me = me;
        renderDashboard();
        renderAlert(null, null);
    } catch (error) {
        if (error.status === 404 || String(error.message).includes('Not Found')) {
            pageState.me = null;
            setVisible('onboarding-card', true);
            setVisible('dashboard-card', false);
            renderAlert(null, null);
            return;
        }

        throw error;
    }
}

function renderLicenses(licenses) {
    const body = document.getElementById('licenses-body');
    if (!body) {
        return;
    }

    if (!licenses || licenses.length === 0) {
        body.innerHTML = '<tr><td colspan="5" class="text-secondary">No license grants yet.</td></tr>';
        return;
    }

    body.innerHTML = licenses.map(license => `
        <tr>
            <td>${license.productKey}</td>
            <td>${license.planKey}</td>
            <td>${license.status}</td>
            <td>${license.validFrom}</td>
            <td>${license.validUntil ?? 'Open-ended'}</td>
        </tr>`).join('');
}

function shortId(value) {
    if (!value) {
        return 'n/a';
    }

    return String(value).slice(0, 8);
}

function renderLicenseRequests(requests) {
    const body = document.getElementById('license-requests-body');
    if (!body) {
        return;
    }

    if (!requests || requests.length === 0) {
        body.innerHTML = '<tr><td colspan="6" class="text-secondary">No license requests yet.</td></tr>';
        return;
    }

    body.innerHTML = requests.map(request => `
        <tr>
            <td>${request.productKey}</td>
            <td>${request.requestedPlanKey}</td>
            <td>${request.changeType}</td>
            <td>${request.status}</td>
            <td>${new Date(request.createdAtUtc).toLocaleDateString()}</td>
            <td>${request.reason || 'n/a'}</td>
        </tr>`).join('');
}

function renderPayments(payments) {
    const body = document.getElementById('payments-body');
    if (!body) {
        return;
    }

    if (!payments || payments.length === 0) {
        body.innerHTML = '<tr><td colspan="5" class="text-secondary">No payment records yet.</td></tr>';
        return;
    }

    body.innerHTML = payments.map(payment => `
        <tr>
            <td>${shortId(payment.licenseChangeRequestId)}</td>
            <td>${payment.productKey}</td>
            <td>${payment.amount} ${payment.currency}</td>
            <td>${payment.status}</td>
            <td>${payment.externalReference || 'n/a'}</td>
        </tr>`).join('');
}

function renderDashboard() {
    const session = loadSession();
    const claims = session?.claims || {};
    setVisible('signed-out-card', false);
    setVisible('signed-in-card', true);
    setVisible('dashboard-card', !!pageState.me);
    setVisible('onboarding-card', !pageState.me);

    setText('session-subject', claims.sub || 'unknown');
    setText('session-email', claims.email || 'unknown');
    setText('org-name', pageState.me?.organization?.name || 'Not onboarded');
    setText('org-billing-email', pageState.me?.organization?.billingEmail || claims.email || '');
    setText('portal-role', pageState.me?.portalUser?.role || 'Pending');
    setText('license-count', String(pageState.me?.licenses?.length || 0));
    setText('request-count', String(pageState.me?.licenseRequests?.length || 0));
    renderLicenses(pageState.me?.licenses || []);
    renderLicenseRequests(pageState.me?.licenseRequests || []);
    renderPayments(pageState.me?.payments || []);
}

async function handleOnboardingSubmit(event) {
    event.preventDefault();
    const form = event.currentTarget;
    const claims = pageState.session?.claims || {};
    const payload = {
        organizationName: form.organizationName.value,
        portalUserEmail: claims.email || null,
        billingEmail: form.billingEmail.value,
        externalReference: form.externalReference.value || null
    };

    try {
        const result = await authorizedJson('/api/portal/onboarding', {
            method: 'POST',
            body: JSON.stringify(payload)
        });
        pageState.me = result;
        renderDashboard();
        renderAlert('success', 'Organization onboarding completed.');
    } catch (error) {
        renderAlert('danger', error.message);
    }
}

async function handleLicenseRequestSubmit(event) {
    event.preventDefault();
    const form = event.currentTarget;
    const payload = {
        organizationId: pageState.me.organization.id,
        productKey: form.productKey.value,
        requestedPlanKey: form.planKey.value,
        changeType: form.changeType.value,
        reason: form.requestReason.value || null
    };

    try {
        await authorizedJson('/api/license-requests', {
            method: 'POST',
            body: JSON.stringify(payload)
        });
        await loadPortalContext();
        renderAlert('success', 'Request submitted. A license will be issued only after payment is recorded, reconciled, and the request is fulfilled.');
        form.reset();
    } catch (error) {
        renderAlert('danger', error.message);
    }
}

async function bootstrapPortalPage() {
    loadSession();

    document.getElementById('login-button')?.addEventListener('click', async () =>
    {
        renderAlert(null, null);
        try {
            await beginLogin();
        } catch (error) {
            renderAlert('danger', error.message);
        }
    });

    document.getElementById('logout-button')?.addEventListener('click', () =>
    {
        beginLogout();
    });

    document.getElementById('onboarding-form')?.addEventListener('submit', handleOnboardingSubmit);
    document.getElementById('request-license-form')?.addEventListener('submit', handleLicenseRequestSubmit);

    if (!pageState.session?.accessToken) {
        setVisible('signed-out-card', true);
        setVisible('signed-in-card', false);
        setVisible('dashboard-card', false);
        setVisible('onboarding-card', false);
        return;
    }

    try {
        await loadPortalContext();
    } catch (error) {
        renderAlert('danger', error.message);
    }

    renderDashboard();
}

if (window.location.pathname.endsWith('/portal-callback.html')) {
    finishLogin().catch(error =>
    {
        const node = document.getElementById('callback-status');
        if (node) {
            node.textContent = error.message;
            node.classList.remove('text-secondary');
            node.classList.add('text-danger');
        }
    });
} else {
    bootstrapPortalPage();
}