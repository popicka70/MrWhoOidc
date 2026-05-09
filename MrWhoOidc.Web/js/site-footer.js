(() => {
    const footerMarkup = `
        <div class="container">
            <div class="row g-4">
                <div class="col-6 col-md-3">
                    <h6>Product</h6>
                    <ul class="list-unstyled small">
                        <li><a href="features.html">Features</a></li>
                        <li><a href="about.html">About</a></li>
                        <li><a href="getting-started.html">Getting Started</a></li>
                        <li><a href="open-source.html">Open Source</a></li>
                        <li><a href="privacy.html">Privacy</a></li>
                    </ul>
                </div>
                <div class="col-6 col-md-3">
                    <h6>Resources</h6>
                    <ul class="list-unstyled small">
                        <li><a href="https://github.com/popicka70/MrWhoOidc" target="_blank" rel="noopener">GitHub Repository</a></li>
                        <li><a href="https://github.com/popicka70/MrWhoOidc/blob/main/docs/deployment-guide.md" target="_blank" rel="noopener">Deployment Guide</a></li>
                        <li><a href="https://github.com/popicka70/MrWhoOidc/blob/main/docs/troubleshooting/local-development.md" target="_blank" rel="noopener">Troubleshooting</a></li>
                    </ul>
                </div>
                <div class="col-6 col-md-3">
                    <h6>Community</h6>
                    <ul class="list-unstyled small">
                        <li><a href="https://github.com/popicka70/MrWhoOidc/issues" target="_blank" rel="noopener">GitHub Issues</a></li>
                        <li><a href="https://github.com/popicka70/MrWhoOidc/discussions" target="_blank" rel="noopener">Discussions</a></li>
                    </ul>
                </div>
                <div class="col-6 col-md-3">
                    <h6>Contact</h6>
                    <ul class="list-unstyled small">
                        <li><a href="mailto:info@mrwhooidc.com">info@mrwhooidc.com</a></li>
                        <li><a href="portal.html">Portal Roadmap</a></li>
                    </ul>
                </div>
            </div>
            <hr class="border-secondary mt-4 mb-3">
            <p class="text-center text-secondary small mb-0">&copy; 2026 MrWhoOidc. Source code available under <a href="open-source.html">Apache 2.0</a>; trademarks and brand assets reserved.</p>
        </div>`;

    document.querySelectorAll("footer[data-site-footer]").forEach((footer) => {
        footer.className = "site-footer text-light py-5";
        footer.innerHTML = footerMarkup;
    });
})();