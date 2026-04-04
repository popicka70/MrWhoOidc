const portalConfig = {
    authority: 'https://localhost:8443/t/default',
    oidcProxyBaseUrl: `${window.location.origin}/oidc/t/default`,
    clientId: 'portal-web',
    redirectUri: `${window.location.origin}/portal-callback.html`,
    postLogoutRedirectUri: `${window.location.origin}/portal.html`,
    licensingApiBaseUrl: `${window.location.origin}/licensing`,
    scope: 'openid profile email offline_access',
    storageKey: 'mrwho.portal.session',
    pkceKey: 'mrwho.portal.pkce'
};

const pageState = {
    session: null,
    user: null,
    me: null
};

const isDevelopmentOperationsMode = window.location.hostname === 'localhost';

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

async function authorizedFetch(path, init = {}) {
    const session = loadSession();
    if (!session?.accessToken) {
        throw new Error('You are not signed in.');
    }

    return fetch(`${portalConfig.licensingApiBaseUrl}${path}`, {
        ...init,
        headers: {
            'Authorization': `Bearer ${session.accessToken}`,
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

function escapeHtml(value) {
    return String(value ?? '')
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#39;');
}

function findPaymentForRequest(requestId) {
    const payments = pageState.me?.payments || [];
    return payments.find(payment => payment.licenseChangeRequestId === requestId) || null;
}

function findLicenseForRequest(requestId) {
    const licenses = pageState.me?.licenses || [];
    return licenses.find(license => license.licenseChangeRequestId === requestId) || null;
}

function renderOpsRequestOptions() {
    const select = document.getElementById('opsRequestId');
    if (!select) {
        return;
    }

    const requests = pageState.me?.licenseRequests || [];
    if (requests.length === 0) {
        select.innerHTML = '<option value="">No requests available</option>';
        select.disabled = true;
        return;
    }

    select.disabled = false;
    select.innerHTML = requests.map(request =>
        `<option value="${request.id}">${escapeHtml(request.productKey)} / ${escapeHtml(request.requestedPlanKey)} / ${escapeHtml(request.status)}</option>`).join('');
}

function buildRequestActions(request) {
    const payment = findPaymentForRequest(request.id);
    const license = findLicenseForRequest(request.id);
    const actions = [];

    if (request.status === 'Submitted') {
        actions.push(`<button type="button" class="btn btn-sm btn-outline-secondary" data-request-action="UnderReview" data-request-id="${request.id}">Review</button>`);
        actions.push(`<button type="button" class="btn btn-sm btn-outline-success" data-request-action="Approved" data-request-id="${request.id}">Approve</button>`);
        actions.push(`<button type="button" class="btn btn-sm btn-outline-danger" data-request-action="Rejected" data-request-id="${request.id}">Reject</button>`);
    }

    if (request.status === 'UnderReview') {
        actions.push(`<button type="button" class="btn btn-sm btn-outline-success" data-request-action="Approved" data-request-id="${request.id}">Approve</button>`);
        actions.push(`<button type="button" class="btn btn-sm btn-outline-danger" data-request-action="Rejected" data-request-id="${request.id}">Reject</button>`);
    }

    if (payment && payment.status === 'Pending') {
        actions.push(`<button type="button" class="btn btn-sm btn-outline-primary" data-payment-action="Received" data-payment-id="${payment.id}">Mark received</button>`);
        actions.push(`<button type="button" class="btn btn-sm btn-outline-danger" data-payment-action="Cancelled" data-payment-id="${payment.id}">Cancel payment</button>`);
    }

    if (payment && payment.status === 'Received') {
        actions.push(`<button type="button" class="btn btn-sm btn-outline-primary" data-payment-action="Reconciled" data-payment-id="${payment.id}">Reconcile</button>`);
        actions.push(`<button type="button" class="btn btn-sm btn-outline-danger" data-payment-action="Cancelled" data-payment-id="${payment.id}">Cancel payment</button>`);
    }

    if (request.status === 'Approved' && payment?.status === 'Reconciled' && !license) {
        actions.push(`<button type="button" class="btn btn-sm btn-accent" data-fulfill-request-id="${request.id}">Fulfill license</button>`);
    }

    return actions.length > 0
        ? `<div class="ops-actions">${actions.join('')}</div>`
        : '<span class="text-secondary small">No actions</span>';
}

function renderOpsPanel() {
    setVisible('ops-panel', !!pageState.me && isDevelopmentOperationsMode);

    const body = document.getElementById('ops-requests-body');
    if (!body || !pageState.me || !isDevelopmentOperationsMode) {
        return;
    }

    const requests = pageState.me.licenseRequests || [];
    if (requests.length === 0) {
        body.innerHTML = '<tr><td colspan="4" class="text-secondary">No requests available for ops actions.</td></tr>';
        renderOpsRequestOptions();
        return;
    }

    body.innerHTML = requests.map(request => {
        const payment = findPaymentForRequest(request.id);
        const license = findLicenseForRequest(request.id);
        const paymentSummary = payment
            ? `${escapeHtml(payment.status)}${payment.externalReference ? ` / ${escapeHtml(payment.externalReference)}` : ''}`
            : 'No payment';
        const licenseSummary = license ? ` / Fulfilled as ${escapeHtml(shortId(license.id))}` : '';

        return `
            <tr>
                <td>
                    <div class="fw-semibold">${escapeHtml(request.productKey)} / ${escapeHtml(request.requestedPlanKey)}</div>
                    <div class="small text-secondary">${escapeHtml(shortId(request.id))}${request.reason ? ` / ${escapeHtml(request.reason)}` : ''}</div>
                </td>
                <td>${escapeHtml(request.status)}</td>
                <td>${paymentSummary}${licenseSummary}</td>
                <td>${buildRequestActions(request)}</td>
            </tr>`;
    }).join('');

    renderOpsRequestOptions();
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
    renderOpsPanel();
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
        renderAlert('danger', error.message);
    }
}

async function updateLicenseRequestStatus(requestId, status) {
    await authorizedJson(`/api/license-requests/${requestId}/status`, {
        method: 'POST',
        body: JSON.stringify({ status })
    });
}

async function updatePaymentStatus(paymentId, status) {
    await authorizedJson(`/api/payment-records/${paymentId}/status`, {
        method: 'POST',
        body: JSON.stringify({ status })
    });
}

async function fulfillLicenseRequest(requestId) {
    const expiration = new Date();
    expiration.setFullYear(expiration.getFullYear() + 1);

    await authorizedJson('/api/licenses/issue', {
        method: 'POST',
        body: JSON.stringify({
            licenseRequestId: requestId,
            expiresAt: expiration.toISOString(),
            createdBy: 'portal-ops'
        })
    });
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

async function handleOpsPaymentSubmit(event) {
    event.preventDefault();
    const form = event.currentTarget;
    const requestId = form.opsRequestId.value;
    const linkedRequest = (pageState.me?.licenseRequests || []).find(request => request.id === requestId);
    if (!linkedRequest) {
        renderAlert('danger', 'Select a valid license request before recording a payment.');
        return;
    }

    try {
        await authorizedJson('/api/payment-records', {
            method: 'POST',
            body: JSON.stringify({
                organizationId: pageState.me.organization.id,
                licenseChangeRequestId: linkedRequest.id,
                productKey: linkedRequest.productKey,
                amount: Number(form.opsAmount.value),
                currency: form.opsCurrency.value,
                externalReference: form.opsExternalReference.value || null,
                notes: form.opsNotes.value || null
            })
        });

        await loadPortalContext();
        renderAlert('success', 'Payment record created. Move it to received and reconciled when processing completes.');
        form.reset();
        form.opsCurrency.value = 'USD';
        form.opsAmount.value = '249.00';
    } catch (error) {
        renderAlert('danger', error.message);
    }
}

async function handleDashboardActionsClick(event) {
    const requestButton = event.target.closest('[data-request-action]');
    const paymentButton = event.target.closest('[data-payment-action]');
    const fulfillButton = event.target.closest('[data-fulfill-request-id]');
    const downloadButton = event.target.closest('[data-download-license-id]');

    if (!requestButton && !paymentButton && !fulfillButton && !downloadButton) {
        return;
    }

    try {
        if (downloadButton) {
            await downloadLicenseKey(downloadButton.dataset.downloadLicenseId);
            renderAlert('success', 'License key download started.');
            return;
        }

        if (requestButton) {
            await updateLicenseRequestStatus(
                requestButton.dataset.requestId,
                requestButton.dataset.requestAction);
            await loadPortalContext();
            renderAlert('success', `Request moved to ${requestButton.dataset.requestAction}.`);
            return;
        }

        if (paymentButton) {
            await updatePaymentStatus(
                paymentButton.dataset.paymentId,
                paymentButton.dataset.paymentAction);
            await loadPortalContext();
            renderAlert('success', `Payment moved to ${paymentButton.dataset.paymentAction}.`);
            return;
        }

        if (fulfillButton) {
            await fulfillLicenseRequest(fulfillButton.dataset.fulfillRequestId);
            await loadPortalContext();
            renderAlert('success', 'Approved paid request fulfilled into an issued license.');
        }
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
    document.getElementById('ops-payment-form')?.addEventListener('submit', handleOpsPaymentSubmit);
    document.getElementById('dashboard-card')?.addEventListener('click', handleDashboardActionsClick);
    document.getElementById('productKey')?.addEventListener('change', () => syncRequestPlanOptions());
    syncRequestPlanOptions();

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