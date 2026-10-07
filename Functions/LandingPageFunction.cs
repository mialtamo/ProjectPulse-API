using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace ProjectPulse.Api.Functions;

public sealed class LandingPageFunction
{
    [Function("PulseApiLandingPage")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "{*path}")]
        HttpRequestData request,
        string? path)
    {
        var normalizedPath = (path ?? string.Empty).Trim('/');

        if (!string.IsNullOrEmpty(normalizedPath) &&
            !normalizedPath.Equals("pulse-api", StringComparison.OrdinalIgnoreCase))
        {
            return request.CreateResponse(HttpStatusCode.NotFound);
        }

        var version = WebUtility.HtmlEncode(
            Environment.GetEnvironmentVariable("PROJECT_PULSE_API_VERSION") ?? "0.1.0");

        var environment = WebUtility.HtmlEncode(
            Environment.GetEnvironmentVariable("PROJECT_PULSE_API_ENVIRONMENT") ?? "POC");

        var response = request.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "text/html; charset=utf-8");
        response.Headers.Add("Cache-Control", "no-store, no-cache, must-revalidate");
        response.Headers.Add("Pragma", "no-cache");
        response.Headers.Add("X-Content-Type-Options", "nosniff");
        response.Headers.Add("X-Frame-Options", "DENY");
        response.Headers.Add(
            "Content-Security-Policy",
            "default-src 'none'; img-src 'self'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'none'");

        var html = $$"""
<!doctype html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <meta name="color-scheme" content="dark" />
    <title>Project Pulse API</title>
    <style>
        :root {
            --cyan: #19ddff;
            --blue: #3478ff;
            --violet: #8a4dff;
            --text: #d7e6ff;
            --muted: #6f86a8;
            --green: #39ff9a;
            --red: #ff4d6d;
            --amber: #ffc857;
        }

        * { box-sizing: border-box; }

        html, body {
            width: 100%;
            height: 100%;
            margin: 0;
            overflow: hidden;
            background: #000;
            color: var(--text);
            font-family: "Courier New", Consolas, monospace;
        }

        body::before {
            content: "";
            position: fixed;
            inset: -35%;
            pointer-events: none;
            background:
                radial-gradient(circle at 30% 24%, rgba(25, 221, 255, .08), transparent 26%),
                radial-gradient(circle at 70% 58%, rgba(138, 77, 255, .07), transparent 34%);
            animation: atmosphere 8s ease-in-out infinite alternate;
        }

        .grid {
            position: fixed;
            inset: 0;
            opacity: .10;
            background-image:
                linear-gradient(rgba(74, 121, 255, .18) 1px, transparent 1px),
                linear-gradient(90deg, rgba(74, 121, 255, .18) 1px, transparent 1px);
            background-size: 72px 72px;
            mask-image: radial-gradient(circle at center, #000 0%, transparent 72%);
            pointer-events: none;
        }

        .scanline {
            position: fixed;
            left: 0;
            right: 0;
            height: 1px;
            top: -2px;
            background: linear-gradient(90deg, transparent 10%, rgba(25,221,255,.22), rgba(138,77,255,.18), transparent 90%);
            box-shadow: 0 0 8px rgba(25, 221, 255, .14);
            animation: scan 8s linear infinite;
            pointer-events: none;
        }

        .brand {
            position: fixed;
            top: 24px;
            left: 28px;
            z-index: 20;
            width: 310px;
        }

        .brand img {
            display: block;
            width: 100%;
            height: auto;
            filter: drop-shadow(0 0 18px rgba(41, 197, 255, .18));
        }

        .api-tag {
            margin-top: -4px;
            padding-left: 7px;
            color: var(--cyan);
            font-size: 11px;
            letter-spacing: .32em;
            text-transform: uppercase;
            text-shadow: 0 0 10px rgba(25, 221, 255, .4);
        }

        .status-panel {
            position: fixed;
            top: 24px;
            right: 28px;
            z-index: 20;
            min-width: 365px;
            padding: 16px 18px;
            border: 1px solid rgba(25, 221, 255, .16);
            border-radius: 8px;
            background: rgba(0, 0, 0, .56);
            backdrop-filter: blur(10px);
            box-shadow: 0 0 30px rgba(25, 221, 255, .05), inset 0 0 25px rgba(25, 221, 255, .025);
            font-size: 12px;
            line-height: 1.9;
            color: #8098ba;
        }

        .status-title, .console-title {
            margin-bottom: 8px;
            color: #d7e6ff;
            font-weight: 700;
            letter-spacing: .18em;
            text-transform: uppercase;
        }

        .connected { color: var(--green); text-shadow: 0 0 9px rgba(57,255,154,.68); }
        .disconnected { color: var(--red); text-shadow: 0 0 9px rgba(255,77,109,.62); }
        .checking { color: var(--amber); text-shadow: 0 0 8px rgba(255,200,87,.35); }

        .last-check {
            margin-top: 8px;
            padding-top: 7px;
            border-top: 1px solid rgba(111, 134, 168, .14);
            color: #526783;
            font-size: 10px;
            letter-spacing: .08em;
        }

        .console {
            position: absolute;
            left: 50%;
            top: 53%;
            transform: translate(-50%, -50%);
            width: min(760px, 78vw);
            padding: 24px;
            border: 1px solid rgba(25, 221, 255, .16);
            border-radius: 10px;
            background: rgba(0, 0, 0, .58);
            box-shadow: 0 0 60px rgba(25,221,255,.06), inset 0 0 35px rgba(138,77,255,.025);
            backdrop-filter: blur(12px);
        }

        .prompt {
            color: var(--cyan);
            margin-bottom: 12px;
            text-shadow: 0 0 8px rgba(25,221,255,.35);
        }

        .endpoint-select {
            width: 100%;
            padding: 13px 14px;
            border: 1px solid rgba(25, 221, 255, .24);
            border-radius: 6px;
            background: #03070c;
            color: #d7e6ff;
            font: inherit;
            outline: none;
        }

        .endpoint-select:focus {
            border-color: rgba(25,221,255,.55);
            box-shadow: 0 0 18px rgba(25,221,255,.08);
        }

        .endpoint-details {
            margin-top: 18px;
            min-height: 125px;
            padding: 16px;
            border-left: 2px solid rgba(25,221,255,.28);
            background: rgba(11, 18, 30, .36);
            color: #8ea6c9;
            line-height: 1.8;
        }

        .method {
            display: inline-block;
            min-width: 48px;
            margin-right: 8px;
            font-weight: 700;
        }

        .get { color: var(--green); }
        .post { color: var(--violet); }
        .path { color: #e2edff; }
        .description { margin-top: 8px; color: #7189aa; }

        .version {
            position: fixed;
            left: 24px;
            bottom: 19px;
            z-index: 5;
            display: flex;
            align-items: center;
            gap: 10px;
            color: var(--muted);
            font-size: 11px;
            font-weight: 600;
            letter-spacing: .16em;
            text-transform: uppercase;
            user-select: none;
        }

        .version .dot {
            width: 6px;
            height: 6px;
            border-radius: 50%;
            background: var(--cyan);
            box-shadow: 0 0 12px rgba(25, 221, 255, .95);
            animation: blink 2.1s ease-in-out infinite;
        }

        @keyframes blink { 0%,100% { opacity:.62; } 50% { opacity:1; } }
        @keyframes scan { from { transform: translateY(0); opacity:0; } 8% { opacity:1; } 92% { opacity:1; } to { transform: translateY(100vh); opacity:0; } }
        @keyframes atmosphere { from { transform:scale(1); opacity:.82; } to { transform:scale(1.06); opacity:1; } }

        @media (max-width: 820px) {
            .brand { width: 220px; left: 16px; top: 16px; }
            .status-panel { top: 140px; right: 12px; left: 12px; min-width: 0; }
            .console { top: 64%; width: 92vw; }
            .version { left: 16px; bottom: 14px; font-size: 9px; }
        }

        @media (prefers-reduced-motion: reduce) {
            *, *::before, *::after { animation: none !important; }
        }
    </style>
</head>
<body>
    <div class="grid" aria-hidden="true"></div>
    <div class="scanline" aria-hidden="true"></div>

    <div class="brand">
        <img src="/pulse-api-logo" alt="Project Pulse" />
        <div class="api-tag">API // MOCK CLAIMS INTERFACE</div>
    </div>

    <div class="status-panel">
        <div class="status-title">PULSE API SYSTEM STATUS</div>
        <div>Checking Function API........ <span id="apiStatus" class="checking">&lt;CHECKING...&gt;</span></div>
        <div>Checking Table Storage....... <span id="storageStatus" class="checking">&lt;CHECKING...&gt;</span></div>
        <div class="last-check" id="lastCheck">LAST CHECK: awaiting first health probe</div>
    </div>

    <section class="console" aria-label="Project Pulse API endpoints">
        <div class="console-title">API COMMAND CONSOLE</div>
        <div class="prompt">pulse-api&gt; select endpoint</div>
        <select id="endpointSelect" class="endpoint-select" aria-label="API endpoint selector">
            <option value="health">GET  /health</option>
            <option value="pending">GET  /claims/pending?maxRecords=100</option>
            <option value="byid">GET  /claims/{uniqueId}</option>
            <option value="submit">POST /claims</option>
            <option value="result">POST /claims/{uniqueId}/result</option>
        </select>
        <div id="endpointDetails" class="endpoint-details"></div>
    </section>

    <div class="version" aria-label="Project Pulse API version {{version}}, environment {{environment}}">
        <span class="dot" aria-hidden="true"></span>
        <span>Project Pulse API&nbsp;&nbsp;v{{version}}&nbsp;&nbsp;•&nbsp;&nbsp;{{environment}}</span>
    </div>

    <script>
        const endpoints = {
            health: {
                method: 'GET', cls: 'get', path: '/health',
                description: 'Checks the Project Pulse API and its Table Storage dependency.'
            },
            pending: {
                method: 'GET', cls: 'get', path: '/claims/pending?maxRecords=100',
                description: 'Returns up to TOP N pending fake claims for the Project Pulse poller.'
            },
            byid: {
                method: 'GET', cls: 'get', path: '/claims/{uniqueId}',
                description: 'Retrieves a previously generated or submitted claim by its unique ID.'
            },
            submit: {
                method: 'POST', cls: 'post', path: '/claims',
                description: 'Submits a fake claim. If the body is empty, FAKE_CLAIM_JSON is used as the template.'
            },
            result: {
                method: 'POST', cls: 'post', path: '/claims/{uniqueId}/result',
                description: 'Stores a simulated adjudication response for a claim.'
            }
        };

        function renderEndpoint() {
            const selected = endpoints[document.getElementById('endpointSelect').value];
            document.getElementById('endpointDetails').innerHTML =
                '<div><span class="method ' + selected.cls + '">' + selected.method + '</span>' +
                '<span class="path">' + selected.path + '</span></div>' +
                '<div class="description">' + selected.description + '</div>';
        }

        function setStatus(id, connected) {
            const el = document.getElementById(id);
            el.className = connected ? 'connected' : 'disconnected';
            el.textContent = connected ? '<CONNECTED>' : '<DISCONNECTED>';
        }

        async function checkHealth() {
            const api = document.getElementById('apiStatus');
            const storage = document.getElementById('storageStatus');
            api.className = storage.className = 'checking';
            api.textContent = storage.textContent = '<CHECKING...>';

            try {
                const response = await fetch('/health', { cache: 'no-store' });
                const data = await response.json();
                setStatus('apiStatus', response.ok);
                setStatus('storageStatus', data.storage === true);
                document.getElementById('lastCheck').textContent =
                    'LAST CHECK: ' + new Date().toLocaleTimeString();
            } catch {
                setStatus('apiStatus', false);
                setStatus('storageStatus', false);
                document.getElementById('lastCheck').textContent =
                    'LAST CHECK: ' + new Date().toLocaleTimeString() + ' | HEALTH ENDPOINT UNAVAILABLE';
            }
        }

        document.getElementById('endpointSelect').addEventListener('change', renderEndpoint);
        renderEndpoint();
        checkHealth();
        setInterval(checkHealth, 5000);
    </script>
</body>
</html>
""";

        await response.WriteStringAsync(html, Encoding.UTF8);
        return response;
    }

    [Function("PulseApiLogo")]
    public async Task<HttpResponseData> Logo(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "pulse-api-logo")]
        HttpRequestData request)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resources = assembly.GetManifestResourceNames();

        var resourceName = resources
            .FirstOrDefault(name =>
                name.EndsWith(
                    "project-pulse-logo.png",
                    StringComparison.OrdinalIgnoreCase));

        if (resourceName is null)
        {
            var debug = request.CreateResponse(HttpStatusCode.NotFound);
            await debug.WriteStringAsync(
                "Embedded resources: " + string.Join(", ", resources));
            return debug;
        }

        await using var resource = assembly.GetManifestResourceStream(resourceName);

        if (resource is null)
        {
            var debug = request.CreateResponse(HttpStatusCode.NotFound);
            await debug.WriteStringAsync(
                $"Resource found but stream could not be opened: {resourceName}");
            return debug;
        }

        var response = request.CreateResponse(HttpStatusCode.OK);
        response.Headers.Add("Content-Type", "image/png");
        response.Headers.Add("Cache-Control", "public, max-age=86400, immutable");
        response.Headers.Add("X-Content-Type-Options", "nosniff");

        await resource.CopyToAsync(response.Body);
        return response;
    }

}
