// Shared behaviour for every KeyGen page.
// The Content-Security-Policy forbids inline scripts and inline event handlers, so
// pages declare behaviour with data- attributes and this file wires it up.
(function () {
    'use strict';

    function downloadFile(content, filename, contentType) {
        var blob = new Blob([content], { type: contentType });
        var url = window.URL.createObjectURL(blob);
        var a = document.createElement('a');
        a.style.display = 'none';
        a.href = url;
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        window.URL.revokeObjectURL(url);
    }

    // <form data-confirm="Are you sure?"> asks before submitting.
    document.addEventListener('submit', function (event) {
        var form = event.target;
        if (!(form instanceof HTMLFormElement) || !form.hasAttribute('data-confirm')) {
            return;
        }

        if (!window.confirm(form.getAttribute('data-confirm'))) {
            event.preventDefault();
        }
    });

    document.addEventListener('click', function (event) {
        if (!(event.target instanceof Element)) {
            return;
        }

        // <button data-copy-text="..." data-copy-success="..." data-copy-failure="...">
        var copyButton = event.target.closest('[data-copy-text]');
        if (copyButton) {
            var text = copyButton.getAttribute('data-copy-text');
            navigator.clipboard.writeText(text).then(function () {
                alert(copyButton.getAttribute('data-copy-success'));
            }, function () {
                alert(copyButton.getAttribute('data-copy-failure'));
            });
            return;
        }

        // <button data-download-source="elementId" [data-download-source-attr="data-x"]
        //         data-download-filename="..." data-download-content-type="...">
        // Downloads the source element's attribute value, or its text content when no attribute is named.
        var downloadButton = event.target.closest('[data-download-source]');
        if (downloadButton) {
            var source = document.getElementById(downloadButton.getAttribute('data-download-source'));
            if (!source) {
                return;
            }

            var attribute = downloadButton.getAttribute('data-download-source-attr');
            var content = attribute ? source.getAttribute(attribute) : source.textContent;
            downloadFile(
                content,
                downloadButton.getAttribute('data-download-filename'),
                downloadButton.getAttribute('data-download-content-type') || 'text/plain');
        }
    });
})();
