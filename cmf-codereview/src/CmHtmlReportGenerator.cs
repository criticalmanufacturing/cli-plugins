using System.Net;
using System.Text;
using Cmf.Cli.Plugin.CodeReview.Models;
using Cmf.Cli.Plugin.CodeReview.Enums;

namespace Cmf.Cli.Plugin.CodeReview;

/// <summary>
/// Renders findings into a single, self-contained HTML page (no external assets — opens straight from
/// disk): severity-colored collapsible rule sections, a live client-side text filter, and light/dark
/// theming. Builds directly from the typed <see cref="Finding"/> list — the same source the Markdown
/// generator uses — reusing <see cref="CmReportGenerator"/>'s formatting/escalation helpers so the two
/// formats stay in sync.
/// </summary>
public static class CmHtmlReportGenerator
{
    private const string FindingTableHead =
        "<thead><tr><th>File name and line numbers</th><th>Method / Endpoint</th><th>Endpoint Source</th>" +
        "<th>Cross-Reference</th><th>Description</th><th>Code snippet</th></tr></thead>";

    /// <summary>HTML counterpart of <see cref="CmReportGenerator.BuildDetailedReport"/>: rule summary plus severity-grouped, collapsible per-rule sections.</summary>
    public static string BuildDetailedReport(IReadOnlyList<Finding> findings, string title, string summary)
    {
        var pageTitle = $"Detailed Remediation Backlog — {title}";
        var body = new StringBuilder();

        body.Append("<h1>").Append(Enc(pageTitle)).Append("</h1>");
        body.Append("<p class=\"summary\">").Append(Enc(summary)).Append("</p>");

        // Rule Summary table.
        body.Append("<h2 class=\"sev-h\">Rule Summary</h2>");
        body.Append("<div class=\"tablewrap\"><table class=\"summary\"><thead><tr>")
            .Append("<th>Rule</th><th>Severity</th><th class=\"num\">Count</th><th>Suggested direction</th>")
            .Append("</tr></thead><tbody>");

        var ruleSeverityGroups = findings
            .GroupBy(f => (f.Rule, f.Severity))
            .OrderBy(g => g.Key.Rule, StringComparer.Ordinal)
            .ThenBy(g => Array.IndexOf(CmReportGenerator.SeverityOrder, g.Key.Severity))
            .ToList();

        if (ruleSeverityGroups.Count == 0)
        {
            body.Append("<tr><td colspan=\"4\">No findings.</td></tr>");
        }
        else
        {
            foreach (var ruleGroup in ruleSeverityGroups)
            {
                var sample = ruleGroup.First();
                var clause = CmReportGenerator.BuildEscalationReasonClause(ruleGroup.ToList(), sample.Severity, sample.Rule);
                body.Append("<tr>")
                    .Append("<td>").Append(Enc(sample.Rule)).Append("</td>")
                    .Append("<td>").Append(Badge(sample.Severity)).Append("</td>")
                    .Append("<td class=\"num\">").Append(ruleGroup.Count()).Append("</td>")
                    .Append("<td>").Append(Enc(sample.SuggestedFix + clause)).Append("</td>")
                    .Append("</tr>");
            }
        }
        body.Append("</tbody></table></div>");

        // Severity sections with collapsible per-rule detail.
        foreach (var severity in CmReportGenerator.SeverityOrder)
        {
            var severityFindings = findings.Where(f => f.Severity == severity).ToList();
            body.Append("<h2 class=\"sev-h ").Append(SeverityClass(severity)).Append("\">")
                .Append(CmReportGenerator.SeverityIcons[severity]).Append(' ')
                .Append(severity.ToString().ToUpperInvariant()).Append("</h2>");

            if (severityFindings.Count == 0)
            {
                body.Append("<p class=\"dir\">No findings.</p>");
                continue;
            }

            foreach (var ruleGroup in severityFindings.GroupBy(f => f.Rule).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                var sample = ruleGroup.First();
                body.Append("<details class=\"rule ").Append(SeverityClass(severity)).Append("\" open>")
                    .Append("<summary>").Append(Enc(ruleGroup.Key))
                    .Append("<span class=\"cnt\">").Append(ruleGroup.Count()).Append("</span></summary>")
                    .Append("<div class=\"rulebody\">")
                    .Append("<p class=\"dir\">").Append(Enc(sample.SuggestedFix)).Append("</p>")
                    .Append("<div class=\"tablewrap\"><table class=\"findings\">").Append(FindingTableHead).Append("<tbody>");

                foreach (var finding in ruleGroup.OrderBy(f => f.RelativePath, StringComparer.Ordinal).ThenBy(f => f.LineNumber))
                {
                    AppendFindingRow(body, finding, includeRuleAndFix: false);
                }

                body.Append("</tbody></table></div></div></details>");
            }
        }

        return RenderPage(pageTitle, body.ToString());
    }

    /// <summary>HTML counterpart of <see cref="CmReportGenerator.BuildStandardReport"/>: one findings table per severity band.</summary>
    public static string BuildStandardReport(IReadOnlyList<Finding> findings, string title, string summary)
    {
        var body = new StringBuilder();
        body.Append("<h1>").Append(Enc(title)).Append("</h1>");
        body.Append("<p class=\"summary\">").Append(Enc(summary)).Append("</p>");

        foreach (var severity in CmReportGenerator.SeverityOrder)
        {
            body.Append("<h2 class=\"sev-h ").Append(SeverityClass(severity)).Append("\">")
                .Append(CmReportGenerator.SeverityIcons[severity]).Append(' ')
                .Append(severity.ToString().ToUpperInvariant()).Append("</h2>");
            body.Append("<div class=\"tablewrap\"><table class=\"findings\">").Append(FindingTableHead).Append("<tbody>");

            var group = findings.Where(f => f.Severity == severity).ToList();
            if (group.Count == 0)
            {
                body.Append("<tr><td colspan=\"6\">None</td></tr>");
            }
            else
            {
                foreach (var finding in group)
                {
                    AppendFindingRow(body, finding, includeRuleAndFix: true);
                }
            }

            body.Append("</tbody></table></div>");
        }

        return RenderPage(title, body.ToString());
    }

    private static void AppendFindingRow(StringBuilder sb, Finding f, bool includeRuleAndFix)
    {
        var location = $"{CmReportGenerator.FormatPath(f.RelativePath)} (line {f.LineNumber})";
        var context = CmReportGenerator.FormatContext(f.MethodName, f.ServiceEndpoint);
        var endpointSource = CmReportGenerator.FormatEndpointSource(f.ServiceEndpointSource);
        var crossReference = CmReportGenerator.FormatCrossReference(f.CrossReference);
        var description = includeRuleAndFix ? $"[{f.Rule}] {f.Description} Fix: {f.SuggestedFix}" : f.Description;

        sb.Append("<tr class=\"frow\">")
          .Append("<td>").Append(Enc(location)).Append("</td>")
          .Append("<td>").Append(Enc(context)).Append("</td>")
          .Append("<td>").Append(Enc(endpointSource)).Append("</td>")
          .Append("<td>").Append(Enc(crossReference)).Append("</td>")
          .Append("<td>").Append(Enc(description)).Append("</td>")
          .Append("<td class=\"code\"><code>").Append(Enc(f.Snippet.Trim())).Append("</code></td>")
          .Append("</tr>");
    }

    private static string Badge(Severity severity) =>
        $"<span class=\"badge {SeverityClass(severity)}\">{CmReportGenerator.SeverityIcons[severity]} {severity}</span>";

    private static string SeverityClass(Severity severity) => severity switch
    {
        Severity.Critical => "crit",
        Severity.High => "high",
        _ => "med"
    };

    private static string Enc(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string RenderPage(string pageTitle, string bodyHtml) =>
        PageTemplate.Replace("%TITLE%", Enc(pageTitle)).Replace("%BODY%", bodyHtml);

    // Self-contained page shell: inline CSS (theme-aware via prefers-color-scheme + a manual toggle) and
    // inline vanilla JS (live filter + expand/collapse). No external requests, so it renders offline.
    private const string PageTemplate =
"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>%TITLE%</title>
<style>
:root{
  --bg:#fdfdfc; --fg:#1c1c1e; --muted:#6b6b70; --line:#e3e3e0; --card:#ffffff;
  --crit:#c0362c; --crit-bg:#fbe9e7; --high:#b25a00; --high-bg:#fdf0e2; --med:#8a7300; --med-bg:#faf6df;
  --code-bg:#f4f4f2; --accent:#2f6f9f;
}
@media (prefers-color-scheme: dark){
  :root{--bg:#1a1a1c;--fg:#e6e6e6;--muted:#9a9aa0;--line:#333338;--card:#232326;
    --crit:#ff8a80;--crit-bg:#3a1f1d;--high:#ffb870;--high-bg:#3a2c17;--med:#e9d770;--med-bg:#33301a;
    --code-bg:#2a2a2e;--accent:#7fb4de;}
}
:root[data-theme=dark]{--bg:#1a1a1c;--fg:#e6e6e6;--muted:#9a9aa0;--line:#333338;--card:#232326;
  --crit:#ff8a80;--crit-bg:#3a1f1d;--high:#ffb870;--high-bg:#3a2c17;--med:#e9d770;--med-bg:#33301a;--code-bg:#2a2a2e;--accent:#7fb4de;}
:root[data-theme=light]{--bg:#fdfdfc;--fg:#1c1c1e;--muted:#6b6b70;--line:#e3e3e0;--card:#fff;
  --crit:#c0362c;--crit-bg:#fbe9e7;--high:#b25a00;--high-bg:#fdf0e2;--med:#8a7300;--med-bg:#faf6df;--code-bg:#f4f4f2;--accent:#2f6f9f;}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--fg);
  font:15px/1.5 -apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,Helvetica,Arial,sans-serif;}
/* Content is comfortably capped on laptops/desktops but stretches to fill wide and ultra-wide
   displays, so the finding tables get more room and need far less horizontal scrolling. */
.container{width:100%;max-width:1400px;margin:0 auto;padding:0 clamp(16px,2.5vw,40px) 80px}
@media (min-width:1600px){.container{max-width:1600px}}
@media (min-width:2000px){.container{max-width:94vw}}
h1{font-size:24px;margin:28px 0 6px}
p.summary{color:var(--muted);margin:6px 0 12px}
h2.sev-h{font-size:14px;letter-spacing:.08em;text-transform:uppercase;margin:34px 0 12px;padding-bottom:6px;border-bottom:2px solid var(--line)}
h2.crit{color:var(--crit)} h2.high{color:var(--high)} h2.med{color:var(--med)}
p.dir{color:var(--muted);margin:6px 0 12px;font-size:13.5px}
.toolbar{position:sticky;top:0;z-index:10;background:var(--bg);border-bottom:1px solid var(--line);
  padding:12px 0;margin-bottom:8px;display:flex;gap:12px;align-items:center;flex-wrap:wrap}
.toolbar input{flex:1;min-width:200px;padding:8px 12px;border:1px solid var(--line);border-radius:8px;
  background:var(--card);color:var(--fg);font-size:14px}
.toolbar .stat{color:var(--muted);font-size:13px;white-space:nowrap}
.toolbar button{padding:7px 12px;border:1px solid var(--line);border-radius:8px;background:var(--card);
  color:var(--fg);cursor:pointer;font-size:13px}
details.rule{border:1px solid var(--line);border-radius:10px;margin:10px 0;background:var(--card);overflow:hidden}
details.rule>summary{cursor:pointer;padding:10px 14px;font-weight:600;list-style:none;display:flex;
  align-items:center;gap:10px;border-left:4px solid var(--line)}
details.rule.crit>summary{border-left-color:var(--crit)} details.rule.high>summary{border-left-color:var(--high)}
details.rule.med>summary{border-left-color:var(--med)}
details.rule>summary::-webkit-details-marker{display:none}
details.rule>summary::before{content:"\25B8";color:var(--muted);transition:transform .15s}
details.rule[open]>summary::before{display:inline-block;transform:rotate(90deg)}
.cnt{margin-left:auto;background:var(--code-bg);color:var(--muted);border-radius:20px;padding:2px 10px;font-size:12px;font-weight:600}
.rulebody{padding:4px 14px 14px}
.tablewrap{overflow-x:auto;border:1px solid var(--line);border-radius:8px;margin:8px 0}
table{border-collapse:collapse;width:100%;font-size:13px}
th,td{text-align:left;padding:7px 10px;border-bottom:1px solid var(--line);vertical-align:top;overflow-wrap:anywhere}
th{position:sticky;top:0;background:var(--card);font-weight:600;white-space:normal;z-index:1}
th.num,td.num{text-align:right}
th.sortable{cursor:pointer;padding-right:20px}
th.sortable:hover{color:var(--accent)}
th .arrow{position:absolute;right:20px;top:50%;transform:translateY(-50%);font-size:10px;color:var(--muted);opacity:.45}
th .arrow.active{opacity:1;color:var(--accent)}
th .rz{position:absolute;top:0;right:0;width:9px;height:100%;cursor:col-resize;user-select:none;touch-action:none}
th .rz:hover,th .rz.dragging{background:var(--accent);opacity:.5}
tbody tr:hover{background:var(--code-bg)}
td.code code,code{font-family:ui-monospace,SFMono-Regular,Consolas,"Liberation Mono",monospace;
  font-size:12px;background:var(--code-bg);padding:1px 5px;border-radius:5px;white-space:pre-wrap;word-break:break-word}
/* Fixed layout + explicit PER-COLUMN PIXEL widths (set by JS on load from the rendered layout) make
   drag-to-resize deterministic. Mixing % and px column widths under table-layout:fixed is a known
   cross-browser footgun -- browsers reconcile the two inconsistently, so a drag on one column can
   visibly fight with the others. JS below measures once, converts every column to px, and drives the
   table's own width from the column-width sum so the wrapper's overflow-x:auto scrolls instead. */
table.findings,table.summary{table-layout:fixed}
.badge{display:inline-block;padding:2px 9px;border-radius:20px;font-size:12px;font-weight:600;white-space:nowrap}
.badge.crit{color:var(--crit);background:var(--crit-bg)} .badge.high{color:var(--high);background:var(--high-bg)}
.badge.med{color:var(--med);background:var(--med-bg)}
.hidden{display:none !important}
</style>
</head>
<body>
<div class="container">
<div class="toolbar">
  <input id="q" type="search" placeholder="Filter findings (file, method, snippet, rule)..." autocomplete="off">
  <span class="stat" id="stat"></span>
  <button id="expand" type="button">Expand all</button>
  <button id="collapse" type="button">Collapse all</button>
  <button id="theme" type="button">Theme</button>
</div>
%BODY%
</div>
<script>
(function(){
  var q=document.getElementById('q'), stat=document.getElementById('stat');
  var rules=[].slice.call(document.querySelectorAll('details.rule'));
  var rows=[].slice.call(document.querySelectorAll('tr.frow'));
  var total=rows.length;
  function apply(){
    var term=q.value.trim().toLowerCase(), shown=0;
    rows.forEach(function(tr){
      var ok = !term || tr.textContent.toLowerCase().indexOf(term)>=0;
      tr.classList.toggle('hidden', !ok); if(ok) shown++;
    });
    rules.forEach(function(d){
      var vis=d.querySelectorAll('tr.frow:not(.hidden)').length;
      d.classList.toggle('hidden', !!term && vis===0);
      if(term) d.open = vis>0;
    });
    stat.textContent = term ? (shown+' of '+total+' findings') : (total+' findings');
  }
  q.addEventListener('input', apply);
  var ex=document.getElementById('expand'); if(ex) ex.onclick=function(){rules.forEach(function(d){if(!d.classList.contains('hidden'))d.open=true;});};
  var col=document.getElementById('collapse'); if(col) col.onclick=function(){rules.forEach(function(d){d.open=false;});};
  var th=document.getElementById('theme'); if(th) th.onclick=function(){
    var r=document.documentElement, cur=r.getAttribute('data-theme');
    var dark=matchMedia('(prefers-color-scheme: dark)').matches;
    r.setAttribute('data-theme', cur==='dark'?'light':(cur==='light'?'dark':(dark?'light':'dark')));
  };

  // Per-column sort (click header, toggles asc/desc) and resize (drag the right edge).
  function cellText(row, idx){ return row.cells[idx] ? row.cells[idx].textContent.trim() : ''; }
  function sortTable(table, idx, header){
    var tb=table.tBodies[0]; if(!tb) return;
    var data=[].slice.call(tb.rows).filter(function(r){ return r.cells.length>idx && r.cells[0].colSpan<2; });
    if(data.length<2) return;
    var dir = header.getAttribute('data-sort')==='asc' ? 'desc' : 'asc';
    [].forEach.call(header.parentNode.cells, function(c){
      if(c!==header){ c.removeAttribute('data-sort'); var a=c.querySelector('.arrow'); if(a){ a.textContent='⇅'; a.classList.remove('active'); } }
    });
    header.setAttribute('data-sort', dir);
    var arrow=header.querySelector('.arrow');
    if(arrow){ arrow.textContent = dir==='asc' ? '▲' : '▼'; arrow.classList.add('active'); }
    var mult = dir==='asc' ? 1 : -1;
    data.sort(function(a,b){ return cellText(a,idx).localeCompare(cellText(b,idx), undefined, {numeric:true, sensitivity:'base'}) * mult; });
    data.forEach(function(r){ tb.appendChild(r); });
  }
  // Explicit per-column PIXEL widths, driven by the table's own width (not the ambient 100%),
  // are what make table-layout:fixed resize predictably -- see the CSS comment above table.findings.
  function pinColumnWidths(table){
    var headers = [].slice.call(table.tHead.rows[0].cells);
    var total = 0;
    headers.forEach(function(h){ var w = Math.max(48, h.getBoundingClientRect().width); h.style.width = w + 'px'; total += w; });
    table.style.width = total + 'px';
  }
  [].slice.call(document.querySelectorAll('table.findings, table.summary')).forEach(function(table){
    if(!table.tHead) return;
    pinColumnWidths(table);
    [].slice.call(table.tHead.rows[0].cells).forEach(function(header, idx){
      header.classList.add('sortable');
      var arrow=document.createElement('span'); arrow.className='arrow'; arrow.textContent='⇅'; header.appendChild(arrow);
      var handle=document.createElement('span'); handle.className='rz'; header.appendChild(handle);
      var suppress=false;
      header.addEventListener('click', function(){ if(!suppress) sortTable(table, idx, header); });
      handle.addEventListener('mousedown', function(e){
        e.preventDefault(); e.stopPropagation();
        var startX=e.clientX, startW=header.getBoundingClientRect().width;
        handle.classList.add('dragging');
        function mm(ev){
          var newW = Math.max(48, startW + (ev.clientX - startX));
          var delta = newW - header.getBoundingClientRect().width;
          header.style.width = newW + 'px';
          table.style.width = (table.getBoundingClientRect().width + delta) + 'px';
        }
        function mu(){
          document.removeEventListener('mousemove', mm); document.removeEventListener('mouseup', mu);
          document.body.style.userSelect=''; handle.classList.remove('dragging');
          suppress=true; setTimeout(function(){ suppress=false; }, 0);
        }
        document.addEventListener('mousemove', mm); document.addEventListener('mouseup', mu);
        document.body.style.userSelect='none';
      });
    });
  });

  apply();
})();
</script>
</body>
</html>
""";
}
