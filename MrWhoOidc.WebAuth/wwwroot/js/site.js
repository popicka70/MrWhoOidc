// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Write your JavaScript code.

document.addEventListener('DOMContentLoaded', () => {
	document.querySelectorAll('[data-license-file-input]').forEach((input) => {
		input.addEventListener('change', async () => {
			const targetSelector = input.getAttribute('data-license-file-target');
			const statusSelector = input.getAttribute('data-license-file-status');
			const target = targetSelector ? document.querySelector(targetSelector) : null;
			const status = statusSelector ? document.querySelector(statusSelector) : null;
			const file = input.files && input.files.length > 0 ? input.files[0] : null;

			if (!target || !file) {
				return;
			}

			try {
				const text = await file.text();
				const licenseKey = text.trim();
				target.value = licenseKey;
				target.dispatchEvent(new Event('input', { bubbles: true }));
				target.dispatchEvent(new Event('change', { bubbles: true }));

				if (status) {
					status.textContent = `${file.name} loaded (${licenseKey.length.toLocaleString()} characters).`;
					status.classList.remove('text-danger');
					status.classList.add('text-success');
				}
			} catch {
				if (status) {
					status.textContent = 'Could not read the selected license file.';
					status.classList.remove('text-success');
					status.classList.add('text-danger');
				}
			}
		});
	});
});
