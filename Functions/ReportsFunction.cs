using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using ProjectPulse.Api.Services;

namespace ProjectPulse.Api.Functions;

public sealed class ReportsFunction(
    ApiReportStore reportStore)
{
    [Function("PulseApiReportsList")]
    public async Task<HttpResponseData> ListAsync(
        [HttpTrigger(
            AuthorizationLevel.Anonymous,
            "get",
            Route = "reports")]
        HttpRequestData request,
        CancellationToken cancellationToken)
    {
        var items =
            await reportStore.ListAsync(
                100,
                cancellationToken);

        var response =
            request.CreateResponse(
                HttpStatusCode.OK);

        response.Headers.Add(
            "Content-Type",
            "application/json; charset=utf-8");

        response.Headers.Add(
            "Cache-Control",
            "no-store");

        var metadata =
            items.Select(
                x => new
                {
                    x.Id,
                    x.CreatedAtUtc,
                    x.Method,
                    x.Route,
                    x.HttpStatusCode,
                    x.UniqueId,
                    x.ClaimCount
                });

        await response.WriteStringAsync(
            JsonSerializer.Serialize(metadata),
            Encoding.UTF8);

        return response;
    }

    [Function("PulseApiReportDetail")]
    public async Task<HttpResponseData> DetailAsync(
        [HttpTrigger(
            AuthorizationLevel.Anonymous,
            "get",
            Route = "reports/{id}")]
        HttpRequestData request,
        string id,
        CancellationToken cancellationToken)
    {
        var item =
            await reportStore.GetAsync(
                id,
                cancellationToken);

        if (item is null)
            return request.CreateResponse(
                HttpStatusCode.NotFound);

        var response =
            request.CreateResponse(
                HttpStatusCode.OK);

        response.Headers.Add(
            "Content-Type",
            "application/json; charset=utf-8");

        response.Headers.Add(
            "Cache-Control",
            "no-store");

        await response.WriteStringAsync(
            JsonSerializer.Serialize(item),
            Encoding.UTF8);

        return response;
    }

    [Function("PulseApiReportsPage")]
    public async Task<HttpResponseData> PageAsync(
        [HttpTrigger(
            AuthorizationLevel.Anonymous,
            "get",
            Route = "reports.html")]
        HttpRequestData request)
    {
        var response =
            request.CreateResponse(
                HttpStatusCode.OK);

        response.Headers.Add(
            "Content-Type",
            "text/html; charset=utf-8");

        response.Headers.Add(
            "Cache-Control",
            "no-store");

        response.Headers.Add(
            "X-Content-Type-Options",
            "nosniff");

        response.Headers.Add(
            "X-Frame-Options",
            "DENY");

        response.Headers.Add(
            "Content-Security-Policy",
            "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'");

        const string html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>Project Pulse API Reports</title>
<style>
:root{color-scheme:dark;--bg:#05070b;--panel:#0b111a;--border:#1c2a3a;--text:#e5eefc;--muted:#8295ad;--cyan:#19ddff;--green:#54e39b;--purple:#b58cff;--amber:#ffc857;--red:#ff627d}
*{box-sizing:border-box}
body{margin:0;background:linear-gradient(180deg,#05070b,#07101a 55%,#05070b);color:var(--text);font-family:Inter,Segoe UI,system-ui,sans-serif}
header{position:sticky;top:0;z-index:10;padding:18px 24px;border-bottom:1px solid var(--border);background:rgba(5,7,11,.92);display:flex;justify-content:space-between;align-items:center}
h1{margin:0;font-size:19px;letter-spacing:.08em}.sub{color:var(--muted);font-size:12px;margin-top:4px}a{color:var(--cyan);text-decoration:none}
main{padding:20px 24px 40px;max-width:1700px;margin:0 auto}
.toolbar{display:grid;grid-template-columns:minmax(260px,1fr) auto auto;gap:10px;align-items:center;margin-bottom:14px}
input,select,button{background:#0b1520;border:1px solid #24405a;color:var(--text);padding:9px 11px;border-radius:7px}
button{cursor:pointer}.muted{color:var(--muted)}
.summary{display:flex;gap:10px;flex-wrap:wrap;margin-bottom:14px}.pill{padding:5px 9px;border:1px solid var(--border);border-radius:999px;background:#09111a;font-size:12px;color:var(--muted)}
.table-wrap{border:1px solid var(--border);border-radius:10px;overflow:auto;background:var(--panel)}
table{width:100%;border-collapse:collapse;min-width:1000px}th,td{padding:10px 12px;border-bottom:1px solid var(--border);font-size:12px;vertical-align:top}
th{background:#0c1621;color:#9fb4cf;text-align:left;white-space:nowrap}tr.data-row{cursor:pointer}tr.data-row:hover{background:#0f1a27}
.method{font-weight:700}.GET{color:var(--green)}.POST{color:var(--purple)}.PUT{color:var(--amber)}.code-ok{color:var(--green)}.code-bad{color:var(--red)}
.id{font-family:ui-monospace,SFMono-Regular,Consolas,monospace;max-width:320px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.expand-row{display:none;background:#07101a}.expand-row.open{display:table-row}.expand-cell{padding:16px}
.payload-grid{display:grid;grid-template-columns:1fr 1fr;gap:14px}.payload-title{font-size:12px;font-weight:700;margin:0 0 7px;color:#c7d9f2}
pre{margin:0;white-space:pre-wrap;word-break:break-word;background:#020407;border:1px solid var(--border);padding:12px;border-radius:7px;max-height:420px;overflow:auto;font:12px/1.5 ui-monospace,SFMono-Regular,Consolas,monospace}
.empty{padding:28px;text-align:center;color:var(--muted)}
@media(max-width:900px){.toolbar{grid-template-columns:1fr}.payload-grid{grid-template-columns:1fr}}
</style>
</head>
<body>
<header><div><h1>PROJECT PULSE API REPORTS</h1><div class="sub">Mock API GET, POST, and PUT activity</div></div><a href="/">Home</a></header>
<main>
<div class="toolbar">
<input id="search" type="search" placeholder="Search method, route, ID, status..." oninput="render()">
<select id="method" onchange="render()"><option value="">All methods</option><option value="GET">GET</option><option value="POST">POST</option><option value="PUT">PUT</option></select>
<button onclick="load()">Refresh</button>
</div>
<div id="summary" class="summary"></div>
<div class="table-wrap"><table>
<thead><tr><th>Time</th><th>Method</th><th>Route</th><th>HTTP</th><th>Claim Count</th><th>Unique ID</th></tr></thead>
<tbody id="rows"></tbody>
</table></div>
</main>
<script>
let items=[];const detailCache=new Map();
const esc=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
function pretty(value){if(value===null||value===undefined||value==='')return 'No body recorded';let t=String(value);try{t=JSON.stringify(JSON.parse(t),null,2)}catch{}return esc(t)}
function filtered(){const q=document.getElementById('search').value.trim().toLowerCase();const method=document.getElementById('method').value;return items.filter(x=>(!method||x.Method===method)&&(!q||[x.Method,x.Route,x.HttpStatusCode,x.UniqueId,x.ClaimCount].some(v=>String(v??'').toLowerCase().includes(q))))}
function render(){const list=filtered();const gets=list.filter(x=>x.Method==='GET').length,posts=list.filter(x=>x.Method==='POST').length,puts=list.filter(x=>x.Method==='PUT').length;document.getElementById('summary').innerHTML='<span class="pill">'+list.length+' visible</span><span class="pill">'+gets+' GET</span><span class="pill">'+posts+' POST</span><span class="pill">'+puts+' PUT</span>';const tbody=document.getElementById('rows');if(!list.length){tbody.innerHTML='<tr><td colspan="6" class="empty">No API activity recorded yet</td></tr>';return}tbody.innerHTML=list.map((x,i)=>{const row='r-'+i;return '<tr class="data-row" onclick="toggle(\''+row+'\',\''+esc(x.Id)+'\')"><td>'+new Date(x.CreatedAtUtc).toLocaleString()+'</td><td class="method '+esc(x.Method)+'">'+esc(x.Method)+'</td><td class="id" title="'+esc(x.Route)+'">'+esc(x.Route)+'</td><td class="'+(Number(x.HttpStatusCode)<400?'code-ok':'code-bad')+'">'+esc(x.HttpStatusCode)+'</td><td>'+esc(x.ClaimCount??'')+'</td><td class="id" title="'+esc(x.UniqueId??'')+'">'+esc(x.UniqueId??'')+'</td></tr><tr id="'+row+'" class="expand-row"><td colspan="6" class="expand-cell"><div class="muted">Loading...</div></td></tr>'}).join('')}
async function toggle(rowId,id){const row=document.getElementById(rowId);const opening=!row.classList.contains('open');document.querySelectorAll('.expand-row.open').forEach(r=>r.classList.remove('open'));if(!opening)return;row.classList.add('open');let x=detailCache.get(id);if(!x){const r=await fetch('/reports/'+encodeURIComponent(id),{cache:'no-store'});if(!r.ok){row.querySelector('td').innerHTML='<div class="muted">Could not load report details.</div>';return}x=await r.json();detailCache.set(id,x)}row.querySelector('td').innerHTML='<div class="payload-grid"><div><div class="payload-title">Request Body</div><pre>'+pretty(x.RequestBody)+'</pre></div><div><div class="payload-title">Response Body</div><pre>'+pretty(x.ResponseBody)+'</pre></div></div>'}
async function load(){detailCache.clear();const r=await fetch('/reports',{cache:'no-store'});items=await r.json();render()}
load();setInterval(load,30000);
</script>
</body>
</html>
""";

        await response.WriteStringAsync(
            html,
            Encoding.UTF8);

        return response;
    }
}
