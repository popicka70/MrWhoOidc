// LicenseGeneration/PlatformLicense: default-tenant section and tier-locked features.
(function () {
    'use strict';

    const deploymentRadios = document.querySelectorAll('input[name="DeploymentMode"]');
    const defaultTenantSection = document.getElementById('default-tenant-features-section');
    const featureCheckboxes = document.querySelectorAll('input[name="SelectedFeatures"]');
    const tierSelect = document.getElementById('Tier');
    const tierFeatureMapElement = document.getElementById('tierFeatureMap');
    const tierFeatureMap = tierFeatureMapElement ? JSON.parse(tierFeatureMapElement.textContent) : {};

    function currentDeploymentMode() {
        return Array.from(deploymentRadios).find(r => r.checked)?.value || 'multi-tenant';
    }

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

    function applyDeploymentMode(mode) {
        const isMultiTenant = mode === 'multi-tenant';
        if (defaultTenantSection) {
            defaultTenantSection.classList.toggle('d-none', !isMultiTenant);
        }
    }

    deploymentRadios.forEach(radio => {
        radio.addEventListener('change', () => {
            if (radio.checked) {
                applyDeploymentMode(radio.value);
            }
        });
    });

    if (tierSelect) {
        tierSelect.addEventListener('change', () => {
            applyTierLocks();
        });
    }

    // Initialize
    applyTierLocks();
    applyDeploymentMode(currentDeploymentMode());
})();
