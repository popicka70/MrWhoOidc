// KeyGeneration/Generate: show the RSA or EC options that match the selected algorithm.
(function () {
    'use strict';

    var algorithmSelect = document.getElementById('algorithmSelect');
    if (!algorithmSelect) {
        return; // The result view (key already generated) has no form.
    }

    algorithmSelect.addEventListener('change', function () {
        var algorithm = this.value;
        var rsaOptions = document.getElementById('rsaOptions');
        var ecOptions = document.getElementById('ecOptions');
        var isRsa = algorithm.startsWith('RS') || algorithm.startsWith('PS');
        var isEc = algorithm.startsWith('ES');

        rsaOptions.classList.toggle('d-none', !isRsa);
        ecOptions.classList.toggle('d-none', !isEc);
    });
})();
