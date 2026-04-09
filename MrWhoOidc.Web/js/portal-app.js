const portalConfig = {
    authority: 'https://localhost:8443/t/default',
    oidcProxyBaseUrl: `${window.location.origin}/oidc/t/default`,
    clientId: 'portal-web',
    redirectUri: `${window.location.origin}/portal-callback.html`,
    postLogoutRedirectUri: `${window.location.origin}/portal-signed-out.html`,
    licensingApiBaseUrl: `${window.location.origin}/licensing`,
    scope: 'openid profile email offline_access',
    storageKey: 'mrwho.portal.session',
    pkceKey: 'mrwho.portal.pkce',
    sessionExpirySkewMs: 5000,
    sessionExpiredMessage: 'Your portal session expired. Sign in again to continue.'
};

const pageState = {
    session: null,
    user: null,
    me: null,
    sessionExpiryTimerId: null
};

const productPlans = {
    mrwhopdf: [
        { key: 'pdf-community', label: 'MrWhoPdf Community' },
        { key: 'pdf-professional', label: 'MrWhoPdf Professional' },
        { key: 'pdf-enterprise', label: 'MrWhoPdf Enterprise' }
    ],
    mrwhooidc: [
        { key: 'oidc-community', label: 'MrWhoOidc Community' },
        { key: 'oidc-professional', label: 'MrWhoOidc Professional' },
        { key: 'oidc-enterprise', label: 'MrWhoOidc Enterprise' }
    ]
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

function clearSessionExpiryTimer() {
    if (pageState.sessionExpiryTimerId) {
        window.clearTimeout(pageState.sessionExpiryTimerId);
        pageState.sessionExpiryTimerId = null;
    }
}

function isSessionExpired(session = pageState.session) {
    if (!session?.accessToken || !Number.isFinite(session.expiresAt)) {
        return false;
    }

    return Date.now() >= (session.expiresAt - portalConfig.sessionExpirySkewMs);
}

function renderSignedOutState(message = null) {
    setVisible('signed-out-card', true);
    setVisible('signed-in-card', false);
    setVisible('dashboard-card', false);
    setVisible('onboarding-card', false);
    setVisible('ops-panel', false);

    if (message) {
        renderAlert('warning', message);
        return;
    }

    renderAlert(null, null);
}

function createSessionExpiredError(message = portalConfig.sessionExpiredMessage) {
    const error = new Error(message);
    error.code = 'SESSION_EXPIRED';
    error.status = 401;
    return error;
}

function expireSession(message = portalConfig.sessionExpiredMessage) {
    clearSession();
    renderSignedOutState(message);
}

function scheduleSessionExpiry() {
    clearSessionExpiryTimer();

    if (!pageState.session?.accessToken) {
        return;
    }

    if (isSessionExpired(pageState.session)) {
        expireSession();
        return;
    }

    const delay = Math.min(
        Math.max(0, pageState.session.expiresAt - Date.now() - portalConfig.sessionExpirySkewMs),
        2147483647);

    pageState.sessionExpiryTimerId = window.setTimeout(() =>
    {
        expireSession();
    }, delay);
}

function requireActiveSession() {
    const session = loadSession();
    if (!session?.accessToken) {
        throw new Error('You are not signed in.');
    }

    if (isSessionExpired(session)) {
        expireSession();
        throw createSessionExpiredError();
    }

    return session;
}

function isSessionExpiredError(error) {
    return error?.code === 'SESSION_EXPIRED';
}

function saveSession(session) {
    localStorage.setItem(portalConfig.storageKey, JSON.stringify(session));
    pageState.session = session;
    scheduleSessionExpiry();
}

function clearSession() {
    clearSessionExpiryTimer();
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

    if (pageState.session?.accessToken) {
        scheduleSessionExpiry();
    } else {
        clearSessionExpiryTimer();
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
    let response;
    try {
        response = await fetch(url, options);
    } catch (error) {
        const networkError = new Error(`Network request failed for ${url}.`);
        networkError.cause = error;
        throw networkError;
    }

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

async function getOidcMetadata() {
    const metadata = await fetchJson(`${portalConfig.oidcProxyBaseUrl}/.well-known/openid-configuration`);
    return {
        ...metadata,
        authorization_endpoint: `${portalConfig.authority}/authorize`,
        token_endpoint: `${portalConfig.oidcProxyBaseUrl}/token`
    };
}

async function buildAuthorizationUrl() {
    const metadata = await getOidcMetadata();
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
    return url;
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

function syncRequestPlanOptions(preferredPlanKey) {
    const productSelect = document.getElementById('productKey');
    const planSelect = document.getElementById('planKey');
    if (!productSelect || !planSelect) {
        return;
    }

    const selectedProductKey = productSelect.value;
    const plans = productPlans[selectedProductKey] || [];
    const selectedPlanKey = plans.some(plan => plan.key === preferredPlanKey)
        ? preferredPlanKey
        : plans[0]?.key;

    planSelect.innerHTML = plans
        .map(plan => `<option value="${plan.key}">${plan.label}</option>`)
        .join('');

    if (selectedPlanKey) {
        planSelect.value = selectedPlanKey;
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
    const url = await buildAuthorizationUrl();
    window.location.assign(url.toString());
}

async function beginRegistration() {
    const authorizeUrl = await buildAuthorizationUrl();
    const registrationUrl = new URL(`${portalConfig.authority}/Registrations`);
    registrationUrl.searchParams.set('returnUrl', `${authorizeUrl.pathname}${authorizeUrl.search}`);
    window.location.assign(registrationUrl.toString());
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

    const metadata = await getOidcMetadata();
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
    const session = requireActiveSession();

    try {
        return await fetchJson(`${portalConfig.licensingApiBaseUrl}${path}`, {
            ...init,
            headers: {
                'Authorization': `Bearer ${session.accessToken}`,
                'Content-Type': 'application/json',
                ...(init.headers || {})
            }
        });
    } catch (error) {
        if (error.status === 401) {
            expireSession();
            throw createSessionExpiredError();
        }

        throw error;
    }
}

async function authorizedFetch(path, init = {}) {
    const session = requireActiveSession();
    const response = await fetch(`${portalConfig.licensingApiBaseUrl}${path}`, {
        ...init,
        headers: {
            'Authorization': `Bearer ${session.accessToken}`,
            ...(init.headers || {})
        }
    });

    if (response.status === 401) {
        expireSession();
        throw createSessionExpiredError();
    }

    return response;
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

    body.innerHTML = licenses.map(license => {
        const linkedRequest = findRequestForLicense(license.licenseChangeRequestId);
        const sourceLabel = linkedRequest
            ? `Request ${shortId(linkedRequest.id)} / ${linkedRequest.status}`
            : 'Manual grant';
        const canDownload = license.downloadReady && (!linkedRequest || linkedRequest.status === 'Fulfilled');
        const actionLabel = canDownload
            ? `<button type="button" class="btn btn-sm btn-outline-primary" data-download-license-id="${license.id}">Download</button>`
            : '<span class="text-secondary small">Pending</span>';
        const validityLabel = `<div>${license.validFrom}</div><div class="license-source-note">until ${license.validUntil ?? 'Open-ended'}</div>`;

        return `
        <tr>
            <td>${license.productKey}</td>
            <td><div>${license.planKey}</div><div class="license-source-note">${sourceLabel}</div></td>
            <td>${license.status}</td>
            <td>${validityLabel}</td>
            <td>${actionLabel}</td>
        </tr>`;
    }).join('');
}

function findRequestForLicense(requestId) {
    if (!requestId) {
        return null;
    }

    const requests = pageState.me?.licenseRequests || [];
    return requests.find(request => request.id === requestId) || null;
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

function renderPaymentInstructions(instructions) {
    const body = document.getElementById('payment-instructions-body');
    if (!body) {
        return;
    }

    if (!instructions || instructions.length === 0) {
        body.innerHTML = '<div class="col-12"><div class="text-secondary">No payment instructions yet.</div></div>';
        return;
    }

    body.innerHTML = instructions.map((instruction, index) => `
        <div class="col-md-6">
            <div class="payment-instruction-card p-3 shadow-sm">
                <div class="d-flex justify-content-between align-items-start gap-3 mb-3">
                    <div>
                        <div class="fw-semibold">${escapeHtml(instruction.productKey)} / ${escapeHtml(instruction.requestedPlanKey)}</div>
                        <div class="small text-secondary">${escapeHtml(instruction.changeType)} request ${escapeHtml(shortId(instruction.licenseChangeRequestId))}</div>
                    </div>
                    <span class="badge text-bg-light border">${escapeHtml(instruction.paymentStatus || 'Pending')}</span>
                </div>
                <div id="payment-qr-${index}" class="payment-qr mb-3"></div>
                <div class="row g-2 small">
                    <div class="col-6">
                        <div class="payment-meta-label">Amount</div>
                        <div>${escapeHtml(`${instruction.amount} ${instruction.currency}`)}</div>
                    </div>
                    <div class="col-6">
                        <div class="payment-meta-label">Reference</div>
                        <div>${escapeHtml(instruction.paymentReference)}</div>
                    </div>
                    <div class="col-12">
                        <div class="payment-meta-label">Description</div>
                        <div>${escapeHtml(instruction.paymentDescription)}</div>
                    </div>
                    <div class="col-12">
                        <div class="payment-meta-label">Expires</div>
                        <div>${escapeHtml(new Date(instruction.expiresAtUtc).toLocaleString())}</div>
                    </div>
                </div>
            </div>
        </div>`).join('');

    instructions.forEach((instruction, index) => {
        const node = document.getElementById(`payment-qr-${index}`);
        if (!node) {
            return;
        }

        if (!window.QRCode?.toCanvas) {
            node.innerHTML = '<div class="text-secondary small">QR rendering unavailable.</div>';
            return;
        }

        const canvas = document.createElement('canvas');
        node.innerHTML = '';
        node.appendChild(canvas);
        window.QRCode.toCanvas(canvas, instruction.qrPayload, { width: 160, margin: 1 }, error => {
            if (error) {
                node.innerHTML = '<div class="text-secondary small">QR rendering unavailable.</div>';
            }
        });
    });
}

function escapeHtml(value) {
    return String(value ?? '')
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#39;');
}

function renderDashboard() {
    const session = loadSession();
    if (!session?.accessToken || isSessionExpired(session)) {
        if (session?.accessToken) {
            expireSession();
            return;
        }

        renderSignedOutState();
        return;
    }

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
    renderPaymentInstructions(pageState.me?.paymentInstructions || []);
}

function handlePortalError(error) {
    if (isSessionExpiredError(error)) {
        return;
    }

    renderAlert('danger', error.message);
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
        handlePortalError(error);
    }
}

async function handleLicenseRequestSubmit(event) {
    event.preventDefault();
    const form = event.currentTarget;
    const availablePlans = productPlans[form.productKey.value] || [];
    const selectedPlan = availablePlans.find(plan => plan.key === form.planKey.value);
    if (!selectedPlan) {
        renderAlert('danger', 'Select a valid plan for the chosen product.');
        syncRequestPlanOptions();
        return;
    }

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
        syncRequestPlanOptions();
    } catch (error) {
        handlePortalError(error);
    }
}

function getDownloadFileName(response, fallbackFileName) {
    const contentDisposition = response.headers.get('Content-Disposition');
    const match = contentDisposition?.match(/filename="?([^";]+)"?/i);
    return match?.[1] || fallbackFileName;
}

async function downloadLicenseKey(licenseId) {
    const response = await authorizedFetch(`/api/licenses/${licenseId}/download`);
    if (!response.ok) {
        const text = await response.text();
        let message = `Request failed with status ${response.status}`;
        if (text) {
            try {
                const body = JSON.parse(text);
                message = body?.title || body?.detail || message;
            } catch {
                message = text;
            }
        }

        throw new Error(message);
    }

    const blob = await response.blob();
    const downloadUrl = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = downloadUrl;
    link.download = getDownloadFileName(response, `license-${licenseId}.jwt`);
    document.body.appendChild(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(downloadUrl);
}

async function handleDashboardActionsClick(event) {
    const downloadButton = event.target.closest('[data-download-license-id]');

    if (!downloadButton) {
        return;
    }

    try {
        await downloadLicenseKey(downloadButton.dataset.downloadLicenseId);
        renderAlert('success', 'License key download started.');
    } catch (error) {
        handlePortalError(error);
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

    document.getElementById('register-button')?.addEventListener('click', async () =>
    {
        renderAlert(null, null);
        try {
            await beginRegistration();
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
    document.getElementById('dashboard-card')?.addEventListener('click', handleDashboardActionsClick);
    document.getElementById('productKey')?.addEventListener('change', () => syncRequestPlanOptions());
    document.addEventListener('visibilitychange', () =>
    {
        if (document.hidden || !pageState.session?.accessToken) {
            return;
        }

        if (isSessionExpired(pageState.session)) {
            expireSession();
            return;
        }

        scheduleSessionExpiry();
    });
    syncRequestPlanOptions();

    if (!pageState.session?.accessToken) {
        renderSignedOutState();
        return;
    }

    if (isSessionExpired(pageState.session)) {
        expireSession();
        return;
    }

    try {
        await loadPortalContext();
    } catch (error) {
        if (isSessionExpiredError(error)) {
            return;
        }

        renderAlert('danger', error.message.includes('Network request failed')
            ? 'Portal could not reach the local licensing services. Refresh once the stack is ready.'
            : error.message);
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