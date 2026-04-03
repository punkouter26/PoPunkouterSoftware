// ─── Utility ──────────────────────────────────────────────────────────────────
const $ = id => document.getElementById(id);
const esc = s => String(s ?? '').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
const fmtCost = n => `$${Number(n ?? 0).toFixed(2)}`;
const emptyRow = msg => `<tr><td colspan="99" class="empty">${msg}</td></tr>`;
const cmd = text => `<span class="cmd" title="Click to copy">${esc(text)}</span>`;

// Click-to-copy on .cmd spans
document.addEventListener('click', e => {
    if (e.target.classList.contains('cmd')) {
        navigator.clipboard?.writeText(e.target.textContent.trim()).catch(()=>{});
        const prev = e.target.style.outline;
        e.target.style.outline = '1px solid #32ffa7';
        setTimeout(()=>{ e.target.style.outline = prev; }, 800);
    }
});

// ─── Badge helpers ────────────────────────────────────────────────────────────
function httpBadge(status) {
    if (status === 'active')      return `<span class="b b-ok">active</span>`;
    if (status === 'broken')      return `<span class="b b-bad">broken</span>`;
    if (status === 'unreachable') return `<span class="b b-warn">unreachable</span>`;
    if (status === 'no-url')      return `<span class="b b-dim">no url</span>`;
    return `<span class="b b-dim">${esc(status ?? '—')}</span>`;
}
function healthBadge(rh) {
    if (!rh) return '—';
    const s = rh.availabilityState ?? '';
    if (s === 'Available')   return `<span class="b b-ok">Available</span>`;
    if (s === 'Unavailable') return `<span class="b b-bad">Unavailable</span>`;
    if (s === 'Degraded')    return `<span class="b b-warn">Degraded</span>`;
    return `<span class="b b-dim">${esc(s || '—')}</span>`;
}
function freeBadge(ft) {
    if (!ft) return '—';
    if (ft.isOnFreeTier) return `<span class="b b-ok">Free ✓</span>`;
    if (ft.canGoFree)    return `<span class="b b-warn" title="Currently ${esc(ft.currentSku)} → free: ${esc(ft.freeSku)}">Can go free</span>`;
    return `<span class="b b-dim">Paid only</span>`;
}
function typeBadge(t) {
    const m = { 'Microsoft.Web/sites':'App Service','Microsoft.App/containerApps':'Container App','Microsoft.Web/staticSites':'Static Web App' };
    return `<span class="b b-info">${esc(m[t] ?? t?.split('/').pop() ?? t ?? '—')}</span>`;
}
function platformBadge(s) {
    if (!s || s === 'Running') return `<span class="b b-ok">Running</span>`;
    if (s === 'Stopped')       return `<span class="b b-bad">Stopped</span>`;
    return `<span class="b b-warn">${esc(s)}</span>`;
}

// ─── "Safe to Remove" aggregation ─────────────────────────────────────────────
function buildSafeToRemove(report) {
    const items = [];

    // 1. Explicit unused resources (empty plans, orphaned IPs, Advisor Cost recs)
    for (const u of (report.unusedResources ?? [])) {
        items.push({
            name:       u.resourceName ?? u.name ?? '—',
            source:     u.source ?? 'Heuristic',
            reason:     u.issue ?? u.recommendation ?? '',
            confidence: u.impact === 'High' ? 'high' : u.impact === 'Medium' ? 'medium' : 'low',
            command:    u.recommendation?.startsWith('az ') ? u.recommendation : null,
            saving:     u.potentialSavingPerMonth ?? null,
        });
    }

    // 2. Zombie apps — 0 HTTP requests in 7 days
    for (const z of (report.zombieApps ?? [])) {
        if (items.some(i => i.name === z.name)) continue;
        items.push({
            name:       z.name,
            source:     'Zombie detection',
            reason:     `0 HTTP requests in the last 7 days. HTTP: ${z.httpStatus}. Platform: ${z.platformState ?? 'Running'}.`,
            confidence: z.httpStatus === 'broken' ? 'high' : 'medium',
            command:    z.recommendation ?? null,
            saving:     null,
        });
    }

    // 3. Services that are broken/stopped AND have 0 7-day requests
    for (const svc of (report.webServices?.services ?? [])) {
        if (items.some(i => i.name === svc.name)) continue;
        const req     = svc.metrics7Days?.Requests ?? svc.metrics7Days?.requests;
        const zerReq  = req === 0;
        const broken      = svc.httpStatus === 'broken';
        const unreachable = svc.httpStatus === 'unreachable';
        const unavail = svc.resourceHealth?.availabilityState === 'Unavailable';
        const stopped = svc.platformState === 'Stopped';
        const azErr   = svc.connectivity?.isAzureErrorPage === true;
        if ((broken || unreachable || unavail || stopped || azErr) && zerReq) {
            items.push({
                name:       svc.name,
                source:     'Connectivity + Metrics',
                reason:     [
                    broken      ? 'HTTP broken'              : null,
                    unreachable ? 'Unreachable (timeout)'    : null,
                    unavail ? 'Azure reports Unavailable' : null,
                    stopped ? 'Platform Stopped'          : null,
                    azErr   ? 'Serving Azure error page'  : null,
                    zerReq  ? '0 requests in 7 days'      : null,
                ].filter(Boolean).join(', '),
                confidence: (broken || unavail) ? 'high' : 'medium',
                command:    svc.resourceType === 'Microsoft.Web/sites'
                    ? `az webapp delete --name "${svc.name}" --resource-group "${svc.resourceGroup}"`
                    : null,
                saving: null,
            });
        }
    }

    // 4. Azure Advisor Cost highlights not already captured
    for (const adv of (report.advisorHighlights ?? [])) {
        if (adv.category !== 'Cost') continue;
        const name = adv.resourceMetadata?.resourceId?.split('/').pop() ?? 'Unknown';
        if (items.some(i => i.name === name)) continue;
        items.push({
            name,
            source:     'Azure Advisor',
            reason:     adv.shortDescription?.problem ?? '',
            confidence: adv.impact === 'High' ? 'high' : adv.impact === 'Medium' ? 'medium' : 'low',
            command:    null,
            saving:     adv.extendedProperties?.annualSavingsAmount
                ? `$${(adv.extendedProperties.annualSavingsAmount / 12).toFixed(2)}/mo`
                : null,
        });
    }

    const order = { high:0, medium:1, low:2 };
    items.sort((a, b) => order[a.confidence] - order[b.confidence]);
    return items;
}

// ─── Section renderers ────────────────────────────────────────────────────────
function renderSummary(r, strCount) {
    const ws   = r.webServices ?? {};
    const cost = r.cost ?? {};
    const cards = [
        { lbl:'Web Services',    num: ws.total ?? 0,                        cls:'' },
        { lbl:'Active',          num: ws.byStatus?.active ?? 0,              cls:'ok' },
        { lbl:'Broken',          num: ws.byStatus?.broken ?? 0,              cls: ws.byStatus?.broken > 0 ? 'danger' : '' },
        { lbl:'Azure Resources', num: r.allResourceSummary?.total ?? 0,      cls:'' },
        { lbl:'On Free Tier',    num: r.freeTier?.onFree?.length ?? 0,       cls:'ok' },
        { lbl:'Can Go Free',     num: r.freeTier?.canGoFree?.length ?? 0,    cls: r.freeTier?.canGoFree?.length > 0 ? 'warn' : '' },
        { lbl:'30-Day Cost',     num: cost.totalFormatted ?? fmtCost(cost.totalCost30Days), cls:'' },
        { lbl:'Safe to Remove',  num: strCount,                              cls: strCount > 0 ? 'danger' : 'ok' },
    ];
    $('summary-strip').innerHTML = cards.map(c =>
        `<div class="s-card ${c.cls}"><div class="num">${esc(c.num)}</div><div class="lbl">${esc(c.lbl)}</div></div>`
    ).join('');
}

function renderDelta(delta) {
    if (!delta) return;
    $('sec-delta').style.display = '';
    $('delta-age-badge').textContent = delta.previousReportAge ?? '';
    $('m-delta-age').innerHTML = ` &nbsp;|&nbsp; Previous run: <strong>${esc(delta.previousReportAge)}</strong>`;
    const mkList = (arr, cls) => arr.length
        ? `<ul>${arr.map(n => `<li class="${cls}">${esc(n)}</li>`).join('')}</ul>`
        : '<p class="muted" style="margin:0">None</p>';
    const boxes = [
        { label:'🔴 Degraded',          html: mkList(delta.degradedSites   ?? [], 'd-bad') },
        { label:'💚 Recovered',         html: mkList(delta.recoveredSites  ?? [], 'd-ok')  },
        { label:'🆕 New Sites',         html: mkList(delta.newSites        ?? [], 'd-new') },
        { label:'🗑 Removed',           html: mkList(delta.removedSites    ?? [], 'd-bad') },
    ];
    if (delta.resourceCountDelta != null) {
        const sign = delta.resourceCountDelta >= 0 ? '+' : '';
        boxes.push({ label:'📦 Resource Count Δ', html:`<p style="font-size:1.4rem;font-weight:700;color:${delta.resourceCountDelta > 0?'#ffd700':'#32ffa7'};margin:0">${sign}${delta.resourceCountDelta}</p>` });
    }
    $('delta-grid').innerHTML = boxes.map(b =>
        `<div class="delta-box"><div class="dlabel">${b.label}</div>${b.html}</div>`
    ).join('');
}

function renderStr(items) {
    $('str-cnt').textContent = items.length;
    if (!items.length) {
        $('str-list').innerHTML = `<div class="ok-box">✅ No resources flagged as safe to remove. Your subscription looks clean.</div>`;
        return;
    }
    const rows = items.map(item => `
        <div class="str-row">
            <div>
                <div class="str-name">${esc(item.name)} <span class="b b-dim" style="font-size:.68rem;vertical-align:middle">${esc(item.source)}</span></div>
                <div class="str-reason">${esc(item.reason)}</div>
                ${item.saving ? `<div class="str-saving">💰 Potential saving: ${esc(item.saving)}</div>` : ''}
                ${item.command ? cmd(item.command) : '<span class="muted" style="font-size:.76rem">No az command — verify in Azure Portal before removing.</span>'}
            </div>
            <div><span class="conf conf-${item.confidence}">${item.confidence.toUpperCase()}</span></div>
        </div>`
    ).join('');
    $('str-list').innerHTML = `<div class="str-panel"><div class="str-hdr">⚠ ${items.length} resource(s) flagged — review before deleting</div><div class="str-body">${rows}</div></div>`;
}

function renderWebServices(services) {
    $('ws-cnt').textContent = services.length;
    function rows(filter) {
        const list = filter === 'all' ? services : services.filter(s => {
            if (filter === 'active')      return s.httpStatus === 'active';
            if (filter === 'broken')      return s.httpStatus === 'broken';
            if (filter === 'unreachable') return s.httpStatus !== 'active' && s.httpStatus !== 'broken';
            return true;
        });
        if (!list.length) return emptyRow('No services match this filter.');
        return list.map(s => {
            const conn = s.connectivity ?? {};
            const m    = s.metrics7Days ?? {};
            const req  = m.Requests ?? m.requests;
            const h5   = m.Http5xx  ?? m.http5xx;
            const rt   = m.AverageResponseTime ?? m.averageResponseTime;
            const reqTd = req != null ? (req === 0 ? `<span style="color:#ffd700">0 ⚠</span>` : esc(req)) : '—';
            const h5Td  = h5  != null ? (h5 > 0   ? `<span class="imp-high">${esc(h5)}</span>` : '0') : '—';
            const rtTd  = rt  != null ? Math.round(rt) : '—';
            const azErr = conn.isAzureErrorPage ? ` <span class="b b-bad" title="Serving Azure default error page">AZ-page</span>` : '';
            const urlHtml = s.url
                ? `<a class="link-ext" href="${esc(s.url)}" target="_blank" rel="noopener noreferrer">${esc(s.url)}</a>`
                : '<span class="muted">—</span>';
            return `<tr>
                <td><strong>${esc(s.friendlyName ?? s.name)}</strong><br><span class="muted">${esc(s.resourceGroup ?? '')}</span></td>
                <td>${typeBadge(s.resourceType)}</td>
                <td style="max-width:220px;overflow:hidden;text-overflow:ellipsis">${urlHtml}</td>
                <td>${httpBadge(s.httpStatus)}${azErr}<br><span class="muted">${conn.statusCode ? `(${conn.statusCode})` : ''}</span></td>
                <td>${platformBadge(s.platformState)}</td>
                <td>${healthBadge(s.resourceHealth)}${s.resourceHealth?.summary ? `<br><span class="muted" style="font-size:.7rem">${esc(s.resourceHealth.summary)}</span>` : ''}</td>
                <td>${reqTd}</td><td>${h5Td}</td><td>${esc(rtTd)}</td>
                <td>${freeBadge(s.freeTierCheck)}${s.freeTierCheck?.canGoFree && s.freeTierCheck.freeSku ? `<br><span class="muted" style="font-size:.7rem">${esc(s.freeTierCheck.currentSku)} → ${esc(s.freeTierCheck.freeSku)}</span>` : ''}</td>
                <td><span class="muted">${esc(s.sku ?? '—')}</span></td>
            </tr>`;
        }).join('');
    }
    $('ws-tbody').innerHTML = rows('all');
    document.querySelectorAll('#ws-tabs .tab').forEach(tab => {
        tab.addEventListener('click', () => {
            document.querySelectorAll('#ws-tabs .tab').forEach(t => t.classList.remove('active'));
            tab.classList.add('active');
            $('ws-tbody').innerHTML = rows(tab.dataset.filter);
        });
    });
}

function renderAppInsights(metrics) {
    if (!metrics?.length) return;
    $('sec-ai').style.display = '';
    $('ai-cnt').textContent = metrics.length;
    $('ai-tbody').innerHTML = metrics.map(ai => {
        const excCls  = ai.exceptions7Days > 0   ? 'imp-high' : '';
        const failCls = ai.failedRequests7Days > 0 ? 'imp-high' : '';
        return `<tr>
            <td><strong>${esc(ai.name)}</strong></td>
            <td class="muted">${esc(ai.resourceGroup)}</td>
            <td>${ai.requests7Days ?? '—'}</td>
            <td class="${failCls}">${ai.failedRequests7Days ?? '—'}</td>
            <td class="${excCls}">${ai.exceptions7Days ?? '—'}</td>
        </tr>`;
    }).join('');
}

function renderZombies(zombies) {
    if (!zombies?.length) return;
    $('sec-zombie').style.display = '';
    $('zombie-cnt').textContent = zombies.length;
    $('zombie-tbody').innerHTML = zombies.map(z => `<tr>
        <td><strong>${esc(z.name)}</strong></td>
        <td class="muted">${esc(z.resourceGroup)}</td>
        <td>${httpBadge(z.httpStatus)}</td>
        <td>${platformBadge(z.platformState)}</td>
        <td>${z.recommendation ? cmd(z.recommendation) : '<span class="muted">—</span>'}</td>
    </tr>`).join('');
}

function renderFreeTier(ft) {
    const data = ft ?? { onFree:[], canGoFree:[], noFreeTier:[] };
    $('ft-on').innerHTML = data.onFree.length
        ? `<div class="sh" style="font-size:.9rem;margin-top:.5rem">✅ On Free Tier <span class="cnt green">${data.onFree.length}</span></div>
           <div style="overflow-x:auto"><table><thead><tr><th>Name</th><th>Type</th><th>SKU</th><th>Resource Group</th></tr></thead><tbody>` +
          data.onFree.map(r =>
            `<tr><td>${esc(r.name)}</td><td class="muted">${esc(r.label)}</td><td><span class="b b-ok">${esc(r.currentSku)}</span></td><td class="muted">${esc(r.resourceGroup)}</td></tr>`
          ).join('') + `</tbody></table></div>`
        : `<div class="muted" style="margin-bottom:.4rem">No resources currently on the free tier.</div>`;

    $('ft-can').innerHTML = data.canGoFree.length
        ? `<div class="sh" style="font-size:.9rem;margin-top:.5rem">🔧 Can Move to Free Tier <span class="cnt gold">${data.canGoFree.length}</span></div>
           <div style="overflow-x:auto"><table><thead><tr><th>Name</th><th>Type</th><th>Current SKU</th><th>Free SKU</th><th>Recommendation</th></tr></thead><tbody>` +
          data.canGoFree.map(r =>
            `<tr><td>${esc(r.name)}</td><td class="muted">${esc(r.label)}</td>
             <td><span class="b b-warn">${esc(r.currentSku)}</span></td>
             <td><span class="b b-ok">${esc(r.freeSku ?? r.freeSkuLabel ?? '—')}</span></td>
             <td class="muted" style="font-size:.76rem">${esc(r.recommendation ?? '—')}</td></tr>`
          ).join('') + `</tbody></table></div>`
        : `<div class="ok-box" style="margin-bottom:.5rem">All eligible resources are on the free tier already.</div>`;

    $('ft-no').innerHTML = data.noFreeTier.length
        ? `<div class="sh" style="font-size:.9rem;margin-top:.5rem">ℹ No Free Tier Available <span class="cnt">${data.noFreeTier.length}</span></div>
           <div style="overflow-x:auto"><table><thead><tr><th>Name</th><th>Type</th><th>SKU</th><th>Free Quota / Note</th></tr></thead><tbody>` +
          data.noFreeTier.map(r =>
            `<tr><td>${esc(r.name)}</td><td class="muted">${esc(r.label)}</td>
             <td><span class="b b-dim">${esc(r.currentSku)}</span></td>
             <td class="muted" style="font-size:.76rem">${esc(r.freeQuota ?? r.recommendation ?? '—')}</td></tr>`
          ).join('') + `</tbody></table></div>`
        : '';
}

function renderDependencies(depMap) {
    if (!depMap) return;
    const hasPlans = depMap.sharedPlans?.length > 0;
    const hasSql   = depMap.sqlServers?.length > 0;
    if (!hasPlans && !hasSql) return;
    $('sec-dep').style.display = '';
    let html = '';
    if (hasPlans) {
        html += `<p class="muted" style="margin-bottom:.4rem">App Service Plans — apps sharing the same plan share compute and failure domain.</p>
        <table><thead><tr><th>Plan Name</th><th>Apps Sharing</th><th>Resource Group</th></tr></thead><tbody>` +
        depMap.sharedPlans.map(p =>
            `<tr><td><strong>${esc(p.planName)}</strong></td>
             <td>${p.apps.map(a => `<span class="b b-info" style="margin:1px">${esc(a)}</span>`).join(' ')}</td>
             <td class="muted">${esc(p.resourceGroup)}</td></tr>`
        ).join('') + `</tbody></table>`;
    }
    if (hasSql) {
        html += `<p class="muted" style="margin:.7rem 0 .4rem">SQL Servers and their databases.</p>
        <table><thead><tr><th>SQL Server</th><th>Databases</th><th>Resource Group</th></tr></thead><tbody>` +
        depMap.sqlServers.map(s =>
            `<tr><td><strong>${esc(s.serverName)}</strong></td>
             <td>${s.databases.map(d => `<span class="b b-purple" style="margin:1px">${esc(d)}</span>`).join(' ')}</td>
             <td class="muted">${esc(s.resourceGroup)}</td></tr>`
        ).join('') + `</tbody></table>`;
    }
    $('dep-content').innerHTML = html;
}

function renderAllResources(summary) {
    if (!summary) return;
    $('res-cnt').textContent = summary.total ?? 0;
    const entries = Object.entries(summary.byType ?? {}).sort((a,b) => b[1] - a[1]);
    const max = entries[0]?.[1] ?? 1;
    $('res-bars').innerHTML = entries.length
        ? entries.map(([type, count]) => {
            const pct = Math.max(2, Math.round((count / max) * 100));
            const ns    = type.split('/')[0]?.replace('Microsoft.','') ?? type;
            const short = type.split('/').pop() ?? type;
            return `<div class="type-bar">
                <span class="bar-label">${count}</span>
                <div class="bar-bg"><div class="bar-fill" style="width:${pct}%"></div></div>
                <span class="bar-name" title="${esc(type)}">${esc(ns)} / ${esc(short)}</span>
            </div>`;
          }).join('')
        : `<p class="muted">No resource breakdown available.</p>`;
}

function renderCost(cost) {
    if (!cost) return;
    $('cost-source').textContent = cost.source ?? '';
    if (cost.note) {
        $('cost-note').innerHTML = `<div class="note-box">📝 ${esc(cost.note)}</div>`;
    }

    const allDrivers = (cost.topCostDrivers ?? []).slice().sort((a,b) => (b.cost ?? 0) - (a.cost ?? 0));

    function showPeriod(days) {
        const factor = days / 30;
        const isEstimate = days < 30;
        const drivers = allDrivers.map(d => ({ ...d, cost: (d.cost ?? 0) * factor }));
        const total = drivers.reduce((s, d) => s + d.cost, 0);
        const labels = { 1:'Last 24 Hours', 3:'Last 3 Days', 7:'Last 7 Days', 30:'Last 30 Days' };

        $('cost-heading-period').textContent = `— ${labels[days] ?? `Last ${days} Days`}`;

        const estNote = $('cost-estimate-note');
        if (isEstimate) {
            estNote.textContent = `⚡ Estimated from 30-day data (÷${30/days}). Actual usage may vary.`;
            estNote.style.display = '';
        } else {
            estNote.style.display = 'none';
        }

        $('cost-tbody').innerHTML = drivers.length
            ? drivers.map(d => `<tr>
                <td>${esc(d.name)}</td>
                <td style="text-align:right;color:${d.cost > 0 ? '#ffd700' : 'var(--text-secondary)'};font-weight:600">${fmtCost(d.cost)}</td>
              </tr>`).join('')
            : emptyRow('No cost breakdown available — credits subscription may show $0.');
        $('cost-total').textContent = fmtCost(total);
    }

    // Wire up tabs
    document.querySelectorAll('#cost-tabs .tab').forEach(tab => {
        tab.addEventListener('click', () => {
            document.querySelectorAll('#cost-tabs .tab').forEach(t => t.classList.remove('active'));
            tab.classList.add('active');
            showPeriod(Number(tab.dataset.days));
        });
    });

    showPeriod(30);
}

function renderDiff(diff) {
    if (!diff) { $('diff-content').innerHTML = '<p class="muted">No diff data in report.</p>'; return; }
    const na = diff.newApps     ?? [];
    const rm = diff.removedApps ?? [];
    const up = diff.updatedApps ?? [];
    let html = `<p class="muted">apps.json: <strong>${diff.currentCount ?? '—'}</strong> app(s) &nbsp;|&nbsp; Azure discovered: <strong>${diff.discoveredCount ?? '—'}</strong></p>`;
    if (!na.length && !rm.length) {
        html += `<div class="ok-box">✅ apps.json is in sync with Azure.</div>`;
    } else {
        if (na.length) html += `<div class="note-box">➕ ${na.length} new app(s) in Azure not yet in apps.json:<br>${na.map(n=>`<strong>${esc(n)}</strong>`).join(', ')}</div>`;
        if (rm.length) html += `<div class="err-box">➖ ${rm.length} app(s) in apps.json no longer found in Azure:<br>${rm.map(n=>`<strong>${esc(n)}</strong>`).join(', ')}</div>`;
    }
    if (up.length) html += `<div class="note-box" style="color:#00c6ff;border-color:rgba(0,198,255,.3)">🔄 ${up.length} app(s) have updated metadata: ${up.map(n=>`<strong>${esc(n)}</strong>`).join(', ')}</div>`;
    $('diff-content').innerHTML = html;
}

// ─── Section renderers — new sections ────────────────────────────────────────
function renderSecurity(sec) {
    if (!sec) return;
    const cnt = sec.total ?? 0;
    if (!cnt) return;
    $('sec-security').style.display = '';
    $('sec-sec-cnt').textContent = cnt;
    const sev = { High:'b-bad', Medium:'b-warn', Low:'b-dim', Info:'b-info' };
    const rows = sec.findings.map(f => `<tr>
        <td><span class="b ${sev[f.impact] ?? 'b-dim'}">${esc(f.impact)}</span></td>
        <td>${esc(f.title)}</td>
        <td class="muted">${esc(f.resourceName)}</td>
        <td class="muted" style="font-size:.8rem">${esc(f.remedy)}</td>
    </tr>`).join('');
    $('sec-security-content').innerHTML = `<div style="overflow-x:auto"><table>
        <thead><tr><th>Severity</th><th>Finding</th><th>Resource</th><th>Recommendation</th></tr></thead>
        <tbody>${rows}</tbody>
    </table></div>`;
}

function renderCostTrend(trend) {
    if (!trend?.months?.length) return;
    $('sec-trend').style.display = '';
    const months = trend.months;
    const max    = Math.max(...months.map(m => m.cost), 0.01);
    const bars   = months.map(m => {
        const pct  = Math.round((m.cost / max) * 100);
        const col  = m.cost === max ? '#ffd700' : '#00c6ff';
        return `<div style="display:flex;flex-direction:column;align-items:center;gap:4px;min-width:60px">
            <span style="font-size:.75rem;color:#aaa">${esc(m.month)}</span>
            <div style="width:40px;height:${Math.max(pct,2)}px;background:${col};border-radius:3px 3px 0 0" title="$${m.cost.toFixed(2)}"></div>
            <span style="font-size:.75rem;color:#ffd700">$${m.cost.toFixed(0)}</span>
        </div>`;
    }).join('');
    $('trend-chart').innerHTML = `<div style="display:flex;align-items:flex-end;gap:8px;padding:1rem 0 0;height:160px">${bars}</div>`;
}

function renderSsl(certs) {
    if (!certs?.length) {
        $('ssl-tbody').innerHTML = emptyRow('No SSL data in report.');
        return;
    }
    $('ssl-tbody').innerHTML = certs.map(c => {
        let statusBadge;
        if (c.error)              statusBadge = `<span class="b b-dim">${esc(c.error)}</span>`;
        else if (c.daysLeft < 0)  statusBadge = `<span class="b b-bad">EXPIRED</span>`;
        else if (c.daysLeft < 14) statusBadge = `<span class="b b-bad">Critical (${c.daysLeft}d)</span>`;
        else if (c.daysLeft < 30) statusBadge = `<span class="b b-warn">Expiring soon</span>`;
        else                      statusBadge = `<span class="b b-ok">OK</span>`;
        return `<tr>
            <td><strong>${esc(c.name)}</strong></td>
            <td><a class="link-ext" href="${esc(c.url ?? '')}" target="_blank" rel="noopener noreferrer">${esc(c.url ?? '—')}</a></td>
            <td class="muted">${esc(c.subject ?? '—')}</td>
            <td class="muted">${esc(c.expiry ?? '—')}</td>
            <td>${c.daysLeft != null ? esc(c.daysLeft) : '—'}</td>
            <td>${statusBadge}</td>
        </tr>`;
    }).join('');
}

function renderConfigDrift(drift) {
    if (!drift?.length) return;
    const withIssues = drift.filter(a => a.issueCount > 0);
    if (!withIssues.length) return;
    $('sec-drift').style.display = '';
    $('drift-cnt').textContent = withIssues.length;
    const sevCol = { high:'#ff4d4d', medium:'#ffd700', low:'#aaa', info:'#00c6ff' };
    $('drift-tbody').innerHTML = withIssues.map(a => {
        const pills = a.issues.map(i =>
            `<span class="b" style="background:rgba(0,0,0,.3);border:1px solid ${sevCol[i.severity]??'#aaa'};color:${sevCol[i.severity]??'#aaa'};margin:2px">${esc(i.issue)}</span>`
        ).join(' ');
        return `<tr>
            <td><strong>${esc(a.friendlyName ?? a.name)}</strong><br><span class="muted">${esc(a.resourceGroup)}</span></td>
            <td><span class="cnt ${a.issueCount > 2 ? 'red' : ''}">${a.issueCount}</span></td>
            <td style="max-width:420px">${pills}</td>
        </tr>`;
    }).join('');
}

function renderStorage(storage) {
    if (!storage?.length) return;
    $('sec-storage').style.display = '';
    $('storage-cnt').textContent = storage.length;
    $('storage-tbody').innerHTML = storage.map(sa => {
        const pub = sa.publicBlobAccess === true
            ? `<span class="b b-bad">YES ⚠</span>`
            : sa.publicBlobAccess === false
            ? `<span class="b b-ok">No</span>`
            : `<span class="b b-dim">—</span>`;
        const https = sa.httpsOnly === true
            ? `<span class="b b-ok">✓</span>`
            : sa.httpsOnly === false
            ? `<span class="b b-bad">No</span>`
            : `<span class="b b-dim">—</span>`;
        const tls = sa.minTls
            ? (sa.minTls >= 'TLS1_2' ? `<span class="b b-ok">${esc(sa.minTls)}</span>` : `<span class="b b-warn">${esc(sa.minTls)}</span>`)
            : '—';
        return `<tr>
            <td><strong>${esc(sa.name)}</strong><br><span class="muted">${esc(sa.resourceGroup)}</span></td>
            <td class="muted">${esc(sa.sku)}</td>
            <td class="muted">${esc(sa.location)}</td>
            <td>${pub}</td>
            <td>${https}</td>
            <td>${tls}</td>
            <td>${sa.issueCount > 0 ? `<span class="cnt ${sa.issueCount > 1 ? 'red' : ''}">${sa.issueCount}</span>` : '<span class="b b-ok">Clean</span>'}</td>
        </tr>`;
    }).join('');
}

function renderQuickActions(report) {
    const actions = [];
    // Downgrade candidates
    for (const r of (report.freeTier?.canGoFree ?? [])) {
        if (r.type === 'Microsoft.Web/serverFarms')
            actions.push({ label:`Downgrade App Service Plan "${r.name}" to F1 (free tier)`, az:`az appservice plan update --name "${r.name}" --resource-group "${r.resourceGroup}" --sku F1` });
        else if (r.type === 'Microsoft.CognitiveServices/accounts')
            actions.push({ label:`Downgrade Cognitive Services "${r.name}" to F0 (free)`, az:`az cognitiveservices account update --name "${r.name}" --resource-group "${r.resourceGroup}" --sku F0` });
        else if (r.type === 'Microsoft.Search/searchServices')
            actions.push({ label:`Downgrade Azure AI Search "${r.name}" to free tier`, az:`az search service update --name "${r.name}" --resource-group "${r.resourceGroup}" --sku free` });
    }
    // Unused resource commands
    for (const u of (report.unusedResources ?? [])) {
        if (u.recommendation?.startsWith('az '))
            actions.push({ label: `Remove unused: ${u.resourceName ?? u.name}`, az: u.recommendation });
    }
    // Zombie stop commands
    for (const z of (report.zombieApps ?? [])) {
        if (z.recommendation)
            actions.push({ label:`Zombie app "${z.name}" (0 requests/7d)`, az: z.recommendation });
    }
    if (!actions.length) return;
    $('sec-qa').style.display = '';
    $('qa-cnt').textContent = actions.length;
    $('qa-list').innerHTML = actions.map(a =>
        `<p class="muted" style="margin:.6rem 0 2px">${esc(a.label)}</p>${cmd(a.az)}`
    ).join('');
}

// ─── Load & bootstrap ─────────────────────────────────────────────────────────
async function load() {
    try {
        const res = await fetch('data/azure-full-report.json');
        if (!res.ok) throw new Error(
            `azure-full-report.json not found (HTTP ${res.status}). ` +
            `Run "npm run azure-report" locally, then copy the output to ` +
            `PoPunkouterSoftware/wwwroot/data/azure-full-report.json, and redeploy.`
        );
        const r = await res.json();

        $('loading-state').style.display = 'none';

        if (!r.generatedAt) {
            $('note-box').style.display = '';
            $('note-box').innerHTML =
                '<strong>No report data yet.</strong> Run <code>npm run azure-report</code> locally, ' +
                'then copy <code>azure-full-report.json</code> to <code>PoPunkouterSoftware/wwwroot/data/</code> and redeploy. ' +
                'Or wait for the next scheduled CI run.';
            return;
        }

        $('content').style.display = '';

        $('m-date').textContent   = new Date(r.generatedAt).toLocaleString();
        $('m-sub').textContent    = r.subscription?.name ?? r.subscription?.id ?? '—';
        $('m-tenant').textContent = r.subscription?.tenantId ?? '—';

        const strItems = buildSafeToRemove(r);

        renderSummary(r, strItems.length);
        renderDelta(r.delta);
        renderStr(strItems);
        renderWebServices(r.webServices?.services ?? []);
        renderAppInsights(r.appInsightsMetrics);
        renderZombies(r.zombieApps);
        renderFreeTier(r.freeTier);
        renderDependencies(r.dependencyMap);
        renderAllResources(r.allResourceSummary);
        renderCost(r.cost);
        renderDiff(r.appsJsonDiff);
        renderSecurity(r.securityPosture);
        renderCostTrend(r.costTrend);
        renderSsl(r.sslExpiry);
        renderConfigDrift(r.configDrift);
        renderStorage(r.storageInventory);
        renderQuickActions(r);

    } catch(err) {
        $('loading-state').style.display = 'none';
        $('err-box').style.display = '';
        $('err-box').textContent = err.message;
    }
}

load();