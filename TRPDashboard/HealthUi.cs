using System;
using System.Fabric;
using System.Net;
using System.Threading;
using System.Xml.Linq;

namespace TRPDashboard
{
    /// <summary>
    /// Shared dark "Service Fabric Explorer" theme for every HealthMonitoring page - the same
    /// look as the sibling FabricShark app (dark command bar + orange SF hexagon, Segoe UI with a
    /// form-control reset, dark panels/cards, SFX-blue accents). Layout() wraps page content in
    /// the command bar + document scaffold; Styles() is the single source of CSS. Existing pages
    /// keep their original CSS class names, which are simply restyled here for the dark theme.
    /// </summary>
    public static class HealthUi
    {
        // Command-bar label (left, next to the logo). The department name moves to the right.
        public const string BrandName = "Health Dashboard";

        // Cluster identity shown in every tab title; set once at startup by the listener.
        public static string ClusterName = "";

        // Public URL prefix the app is served under (e.g. "/HealthMonitoring/TRPDashboard" behind the
        // SF reverse proxy, or "" at the root). Set once at startup by the listener. Emitted as a
        // <base href> so every relative link/form/fetch resolves under it regardless of the current
        // route's depth — the one knob that makes the UI prefix-correct behind the proxy.
        public static string PathBase = "";

        // The real Service Fabric orange-hexagon logo (favicon.png, 24x24, transparent), as served
        // by the local Service Fabric Explorer. Embedded as a base64 PNG data URI and reused for
        // both the browser-tab favicon and the command-bar brand mark - no binary asset, no route.
        private const string SfLogoDataUri = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAABgAAAAYCAYAAADgdz34AAAACXBIWXMAAA7EAAAOxAGVKw4bAAAB/UlEQVR4XqXSMUhbURTH4Zdr0GJIIaLQLlJSKCG6ZNHVDi2BLAodOhQzBANqu5gOgWRso0JLoLRFKVIKhXYsSLHQQdDFtkIHQbGDkSwp2QNBWtrfg8MbDtz3LviHjyOee8/1XW/Mc8jZg1vXKS9RkMZnPEy//9WJ2htzGD5I+Ymsah0jxyEXYfsNolLQw0VWeuRyB4yH9y5/wFl0z85E3P8kpQlbmrIm+gAWXkUGV2T4PcoBbmIDqzgRq9iQ3oGs9fy9MiMZvCKaceozLCOOHr5iFhdY4qVsWb6wRHmNQXzCHSTwB6/w2D/gCY0adH5jluHfIq5xWoZfg85TQ1m07G2FDheypmVpLxrKiKU5CteMWRojhnJoaf6Aa75bGoeGsoK+anRRh2vq6KpGHxXDHe7TmMIbnEqzxu9brtNlbU0apzJrihl7RhYcoUxjHn7uwjlqz7w/C0ceMdD33kGe5zfkOlnW5tHR/zujPvUfZRtJzMA1M7JnW2YEiUPnC8r4wFecUz/iORv/qr96gFLBfdwI9qoYtWmY0pBeCjmsYxM6m9LLISWNhszQBwSKyECnxMa0J5GfS9DJoBh2RROwpcrgY2lkYctE2AFt2LIAl7TDrugtutDZwZyyA52uzAgSg37TWcoL3EYP71DlFfXUugRlDUUksItHrDuhBvkPjpKgs3+0TEoAAAAASUVORK5CYII=";

        private const string FaviconLink = "<link rel='icon' type='image/png' href='" + SfLogoDataUri + "' />";
        private const string LogoImg = "<img class='logo' width='24' height='24' alt='' src='" + SfLogoDataUri + "' />";

        private static string H(object value) => WebUtility.HtmlEncode(value?.ToString() ?? string.Empty);

        // Cluster label for the tab title: the live cluster manifest's <ClusterManifest Name="...">
        // (e.g. "DevCluster"), else the node FQDN, else the machine name. Bounded + best-effort so
        // it never blocks listener startup. Set once into ClusterName at startup.
        public static string ResolveClusterName(FabricClient fc, StatelessServiceContext ctx)
        {
            if (fc != null)
            {
                try
                {
                    var xml = fc.ClusterManager
                        .GetClusterManifestAsync(TimeSpan.FromSeconds(10), CancellationToken.None)
                        .GetAwaiter().GetResult();
                    var name = string.IsNullOrWhiteSpace(xml) ? null : XDocument.Parse(xml).Root?.Attribute("Name")?.Value;
                    if (!string.IsNullOrWhiteSpace(name))
                        return name.Trim();
                }
                catch { /* fall through to node identity */ }
            }
            return ctx?.NodeContext?.IPAddressOrFQDN ?? Environment.MachineName;
        }

        // The public URL prefix for emitted links. Precedence: explicit HealthMonitoring_PublicPathBase
        // override -> the service's own Fabric name path (which is exactly the SF reverse-proxy prefix,
        // e.g. fabric:/HealthMonitoring/TRPDashboard -> "/HealthMonitoring/TRPDashboard") -> empty
        // (direct access). Normalized to a leading slash and no trailing slash; "" means "served at root".
        public static string ResolvePathBase(StatelessServiceContext ctx)
        {
            var raw = Environment.GetEnvironmentVariable("HealthMonitoring_PublicPathBase");
            if (string.IsNullOrWhiteSpace(raw))
                raw = ctx?.ServiceName?.AbsolutePath;

            raw = (raw ?? "").Trim().Trim('/');
            return raw.Length == 0 ? "" : "/" + raw;
        }

        // <base> so every relative link/form/fetch resolves under the public prefix. Always ends in a
        // slash ("/HealthMonitoring/TRPDashboard/" or "/"), which is what relative resolution needs.
        private static string BaseTag() => $"<base href='{H((PathBase ?? "").TrimEnd('/') + "/")}'>";

        public static string Layout(string title, string body, string headExtra = "")
        {
            return $@"<!DOCTYPE html>
<html lang='en'>
<head>
    <meta charset='UTF-8'>
    <meta name='viewport' content='width=device-width, initial-scale=1.0'>
    {BaseTag()}
    <title>Health{(string.IsNullOrEmpty(ClusterName) ? "" : " | " + H(ClusterName))}</title>
    {FaviconLink}
    <style>{Styles()}</style>
    {headExtra}
</head>
<body>
    <header class='topbar'>
        <div class='brand'>{LogoImg}<span class='brand-name'>{BrandName}</span></div>
        <div class='env'>{H(DashboardService.DEPARTMENT_NAME)}</div>
    </header>
    <main class='main'>
        {body}
    </main>
</body>
</html>";
        }

        public static string Styles() => @"
:root{
  --accent-bg:#191919;--canvas:#262626;--panel:#2d2d2d;
  --line:#3a3a3a;--line2:#4c4c4c;
  --blue:#0075c9;--blue-light:#70daf8;--blue-hover:#1a86d8;
  --text:#fff;--text2:#d4d4d4;--text3:#c9c9c9;--muted:#9d9b9b;
  --ok:#3fb950;--warn:#e3b341;--err:#f85149;
  --radius:5px;--shadow:rgba(0,0,0,.24) 0 3px 8px;
}
*{margin:0;padding:0;box-sizing:border-box}
html,body{height:100%}
body{font-family:'Segoe UI',-apple-system,'Helvetica Neue','Lucida Grande',Trebuchet,Arial,sans-serif;color:var(--text2);background:var(--canvas);line-height:1.42857;font-size:10pt;min-height:100vh}
button,input,select,textarea{font-family:inherit;font-size:inherit;line-height:inherit}
a{color:var(--blue-light);text-decoration:none}
a:hover{text-decoration:underline}

/* command bar */
.topbar{height:48px;background:var(--accent-bg);display:flex;align-items:center;justify-content:space-between;padding:0 16px;position:sticky;top:0;z-index:20;border-bottom:1px solid var(--line2)}
.topbar .brand{display:flex;align-items:center;gap:8px}
.topbar .logo{display:block}
.brand-name{font-size:16px;font-weight:600;letter-spacing:.2px;color:#fff}
.topbar .env{font-size:12px;color:var(--text2)}

.main{background:var(--canvas)}
.container{max-width:1400px;margin:0 auto;padding:20px 24px}

/* page heading */
.page-head{display:flex;justify-content:space-between;align-items:center;gap:16px;margin-bottom:18px}
.page-head h1{font-size:24px;font-weight:300;color:#fff;letter-spacing:.2px}
.page-head .sub{color:var(--muted);font-size:13px;margin-top:4px}

/* generic primitives */
.btn{display:inline-block;background:var(--blue);color:#fff;border:1px solid var(--blue);border-radius:var(--radius);padding:8px 18px;font-size:13px;font-weight:600;cursor:pointer;text-decoration:none}
.btn:hover{background:var(--blue-hover);border-color:var(--blue-hover);text-decoration:none}
.banner{background:rgba(227,179,65,.12);border:1px solid rgba(227,179,65,.3);border-left:4px solid var(--warn);color:#e8d9a8;padding:10px 14px;border-radius:var(--radius);margin-bottom:14px;font-size:12.5px}
.banner.err{background:rgba(248,81,73,.12);border-color:rgba(248,81,73,.3);border-left-color:var(--err);color:#f5b5b1}
.card{background:var(--panel);border:1px solid var(--line);border-radius:var(--radius);padding:18px 20px;box-shadow:var(--shadow)}
.card h3{font-size:14px;font-weight:600;color:#fff;margin-bottom:8px}
.card p{color:var(--text2);font-size:13px;line-height:1.55;margin-bottom:8px}
.muted{color:var(--muted);font-size:12px}
.no-items{padding:20px;text-align:center;color:var(--muted);font-style:italic;background:rgba(255,255,255,.02);border:1px dashed var(--line2);border-radius:var(--radius)}

/* status cards grid */
.status-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(290px,1fr));gap:16px;margin-bottom:20px}
.status-card{background:var(--panel);border:1px solid var(--line);border-left:3px solid var(--blue);border-radius:var(--radius);padding:16px 18px;box-shadow:var(--shadow)}
.status-card h3{color:#fff;margin-bottom:12px;font-size:14px;font-weight:600}

.info-value{font-size:26px;font-weight:300;color:#fff;margin-bottom:4px}
.info-value-small{font-size:13px;font-weight:500;color:var(--text2);word-break:break-all}
.info-label{font-size:12px;color:var(--muted);margin-bottom:4px}

.node-details{margin-top:12px;display:grid;grid-template-columns:repeat(auto-fit,minmax(120px,1fr));gap:10px}
.detail-item{display:flex;flex-direction:column;gap:2px}
.detail-label{font-size:11px;color:var(--muted)}
.detail-value{font-size:13px;font-weight:500;color:var(--text2)}

.info-grid-compact{display:grid;grid-template-columns:1fr;gap:2px}
.info-item-compact{display:flex;justify-content:space-between;align-items:center;gap:12px;padding:6px 0;border-bottom:1px solid var(--line)}
.info-item-compact:last-child{border-bottom:none}

/* health pills */
.health-status{display:inline-flex;align-items:center;gap:6px;padding:3px 11px;border-radius:20px;font-weight:500;font-size:12px}
.health-ok{background:rgba(63,185,80,.12);color:var(--ok);border:1px solid rgba(63,185,80,.4)}
.health-warning{background:rgba(227,179,65,.12);color:var(--warn);border:1px solid rgba(227,179,65,.4)}
.health-error{background:rgba(248,81,73,.12);color:var(--err);border:1px solid rgba(248,81,73,.4)}
.health-unknown{background:rgba(157,155,155,.12);color:var(--muted);border:1px solid rgba(157,155,155,.4)}
.status-indicator{width:8px;height:8px;border-radius:50%;display:inline-block}
.health-ok .status-indicator{background:var(--ok)}
.health-warning .status-indicator{background:var(--warn)}
.health-error .status-indicator{background:var(--err)}
.health-unknown .status-indicator{background:var(--muted)}

.health-overview{margin-top:8px}
.health-summary{display:inline-block;padding:2px 9px;border-radius:12px;font-size:12px;font-weight:500;margin-right:5px}
.health-summary.ok{background:rgba(63,185,80,.12);color:var(--ok)}
.health-summary.warning{background:rgba(227,179,65,.12);color:var(--warn)}
.health-summary.error{background:rgba(248,81,73,.12);color:var(--err)}
.health-summary.unknown{background:rgba(157,155,155,.12);color:var(--muted)}

/* sections */
.applications-section,.nodes-section{background:var(--panel);border:1px solid var(--line);border-radius:var(--radius);padding:20px;box-shadow:var(--shadow);margin-bottom:20px}
.applications-section h2,.nodes-section h2{color:#fff;margin-bottom:16px;font-size:18px;font-weight:300}

.application-item{background:rgba(255,255,255,.02);border:1px solid var(--line);border-left:3px solid var(--blue);border-radius:var(--radius);padding:16px;margin-bottom:12px}
.application-header{display:flex;justify-content:space-between;align-items:center;margin-bottom:6px;gap:10px}
.application-name{font-size:15px;font-weight:600;color:#fff}
.application-details{margin-bottom:10px}
.app-type{font-size:12px;color:var(--muted)}
.service-list{margin-left:14px}
.service-item{display:grid;grid-template-columns:1fr auto auto;gap:14px;align-items:center;padding:8px 0;border-bottom:1px solid var(--line)}
.service-item:last-child{border-bottom:none}
.service-name{font-weight:500;color:var(--text2)}
.service-type{font-size:12px;color:var(--muted)}

.node-item{background:rgba(255,255,255,.02);border:1px solid var(--line);border-radius:var(--radius);padding:16px;margin-bottom:12px}
.node-main{display:flex;justify-content:space-between;align-items:center;margin-bottom:12px;gap:10px}
.node-name{font-weight:600;color:#fff;font-size:14px}
.node-details-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(150px,1fr));gap:12px}
.node-detail{display:flex;flex-direction:column;gap:3px}
.node-status.up{color:var(--ok);font-weight:600}
.node-status.down{color:var(--err);font-weight:600}

/* floating refresh */
.refresh-button{position:fixed;bottom:24px;right:24px;background:var(--blue);color:#fff;border:1px solid var(--blue);border-radius:50px;padding:11px 20px;font-size:13px;font-weight:600;cursor:pointer;box-shadow:var(--shadow);transition:transform .3s ease,background .2s ease}
.refresh-button:hover{background:var(--blue-hover);border-color:var(--blue-hover)}

.last-updated{text-align:center;color:var(--muted);font-size:12px;margin-top:16px;padding:12px;background:var(--panel);border:1px solid var(--line);border-radius:var(--radius)}

.copyable{cursor:pointer;padding:2px 5px;border-radius:4px;transition:background-color .15s ease}
.copyable:hover{background:rgba(0,117,201,.18);color:#fff}

/* export panel: always shown, sits in the page-head row aligned with the title */
.export-panel{display:inline-flex;align-items:center;gap:10px;flex:0 0 auto;background:var(--panel);border:1px solid var(--line);border-radius:var(--radius);padding:8px 12px;box-shadow:var(--shadow)}
.export-fmt{display:flex;align-items:center;gap:6px;font-size:12px;color:var(--muted)}
.export-fmt select{padding:4px 6px;background:#1f1f1f;color:#fff;border:1px solid var(--line2);border-radius:var(--radius)}

/* landing cards */
.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(280px,1fr));gap:16px}
.info-grid{display:grid;grid-template-columns:repeat(auto-fit,minmax(240px,1fr));gap:14px;margin:16px 0}
.info-item{padding:14px 16px;background:rgba(255,255,255,.02);border:1px solid var(--line);border-left:3px solid var(--blue);border-radius:var(--radius);color:var(--text2);font-size:13px}
.nav-links{margin-top:18px;display:flex;gap:10px;flex-wrap:wrap}

@media (max-width:768px){
  .container{padding:14px}
  .status-grid,.cards{grid-template-columns:1fr}
  .node-details-grid{grid-template-columns:1fr}
  .application-header,.node-main{flex-direction:column;align-items:flex-start;gap:6px}
  .service-item{grid-template-columns:1fr;gap:5px}
  .refresh-button{bottom:16px;right:16px}
}
";
    }
}
