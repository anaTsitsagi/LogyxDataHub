(function () {
    function addButton() {
        var btn = document.createElement('button');
        btn.textContent = 'Get Dev Token';
        btn.style.marginLeft = '8px';
        btn.className = 'btn authorize dev-token-btn';
        btn.onclick = async function () {
            try {
                // Dev credentials - change if needed
                var body = { username: 'dev', password: 'dev' };
                var r = await fetch('/auth/token', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(body)
                });
                if (!r.ok) {
                    alert('Token request failed: ' + r.status);
                    return;
                }
                var j = await r.json();
                var token = j.access_token || j.accessToken || j.token;
                if (!token) {
                    alert('Token not returned');
                    return;
                }

                // Wait until swagger-ui global object is available
                var attempts = 0;
                function setTokenWhenReady() {
                    attempts++;
                    if (window.ui && typeof window.ui.preauthorizeApiKey === 'function') {
                        // Use the same security scheme id you used in AddSecurityDefinition("Bearer", ...)
                        window.ui.preauthorizeApiKey('Bearer', 'Bearer ' + token);
                        alert('Token set in Swagger Authorize');
                        return;
                    }
                    if (attempts < 20) {
                        setTimeout(setTokenWhenReady, 200);
                    } else {
                        alert('Swagger UI did not expose preauthorizeApiKey.');
                    }
                }
                setTokenWhenReady();
            } catch (err) {
                alert('Error fetching token: ' + err);
            }
        };

        // Attach to the topbar next to the default controls
        var topbar = document.querySelector('.swagger-ui .topbar') || document.body;
        topbar.appendChild(btn);
    }

    if (document.readyState === 'complete') addButton();
    else window.addEventListener('load', addButton);
})();