// LicenseGeneration/TenantLicense: tier-locked features.
(function () {
    'use strict';

    const featureCheckboxes = document.querySelectorAll('input[name="SelectedFeatures"]');
    const tierSelect = document.getElementById('Tier');
    const tierFeatureMapElement = document.getElementById('tierFeatureMap');
    const tierFeatureMap = tierFeatureMapElement ? JSON.parse(tierFeatureMapElement.textContent) : {};

    function applyTierLocks() {
        if (!tierSelect) return;

        const normalizedTier = (tierSelect.value || '').toLowerCase();
        const tierFeatures = tierFeatureMap[normalizedTier] || [];
        const tierFeatureSet = new Set(tierFeatures.map(f => f.toLowerCase()));

        featureCheckboxes.forEach(input => {
            const featureKey = (input.dataset.featureKey || '').toLowerCase();
            const shouldLock = tierFeatureSet.has(featureKey);
            input.dataset.tierLock = shouldLock ? 'true' : 'false';
            if (shouldLock) {
                input.checked = true;
                input.setAttribute('disabled', 'disabled');
            } else {
                input.removeAttribute('disabled');
            }
        });
    }

    if (tierSelect) {
        tierSelect.addEventListener('change', applyTierLocks);
    }

    // Initialize
    applyTierLocks();
})();
