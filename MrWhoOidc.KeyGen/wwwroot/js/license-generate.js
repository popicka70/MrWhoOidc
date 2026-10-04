// LicenseGeneration/Generate (legacy generator): scope sections and tier-locked features.
(function () {
    'use strict';

    const scopeRadios = document.querySelectorAll('input[name="Scope"]');
    const scopeSections = document.querySelectorAll('[data-scope-section]');
    const featureCheckboxes = document.querySelectorAll('input[name="SelectedFeatures"]');
    const tierSelect = document.getElementById('Tier');
    const tierFeatureMapElement = document.getElementById('tierFeatureMap');
    const tierFeatureMap = tierFeatureMapElement ? JSON.parse(tierFeatureMapElement.textContent) : {};

    function currentScope() {
        return Array.from(scopeRadios).find(r => r.checked)?.value || 'platform';
    }

    function applyTierLocks(scope) {
        if (!tierSelect) {
            return;
        }

        const normalizedTier = (tierSelect.value || '').toLowerCase();
        const normalizedScope = (scope || 'platform').toLowerCase();
        const isTenantScope = normalizedScope === 'tenant';
        const tierFeatures = tierFeatureMap[normalizedTier] || [];
        const tierFeatureSet = new Set(tierFeatures.map(f => f.toLowerCase()));

        featureCheckboxes.forEach(input => {
            const featureKey = (input.dataset.featureKey || '').toLowerCase();
            const isPlatformOnly = input.dataset.platformOnly === 'true';
            const shouldLock = tierFeatureSet.has(featureKey) && !(isTenantScope && isPlatformOnly);
            input.dataset.tierLock = shouldLock ? 'true' : 'false';
            if (shouldLock) {
                input.checked = true;
            }
        });
    }

    function applyScope(scope) {
        scopeSections.forEach(section => {
            const targetScope = section.getAttribute('data-scope-section');
            section.classList.toggle('d-none', targetScope !== scope);
        });

        const isTenant = scope === 'tenant';
        featureCheckboxes.forEach(input => {
            const isPlatformOnly = input.dataset.platformOnly === 'true';
            const isTierLocked = input.dataset.tierLock === 'true';

            if (isTierLocked) {
                input.checked = true;
                input.setAttribute('disabled', 'disabled');
                return;
            }

            if (isTenant && isPlatformOnly) {
                input.checked = false;
                input.setAttribute('disabled', 'disabled');
                return;
            }

            input.removeAttribute('disabled');
        });
    }

    scopeRadios.forEach(radio => {
        radio.addEventListener('change', () => {
            if (radio.checked) {
                const scope = radio.value;
                applyTierLocks(scope);
                applyScope(scope);
            }
        });
    });

    if (tierSelect) {
        tierSelect.addEventListener('change', () => {
            const scope = currentScope();
            applyTierLocks(scope);
            applyScope(scope);
        });
    }

    const initialScope = currentScope();
    applyTierLocks(initialScope);
    applyScope(initialScope);
})();
