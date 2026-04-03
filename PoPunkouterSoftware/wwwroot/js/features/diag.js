// Use same-origin API in dotnet-dev and production; fall back to :5050 when
// the page is served by the separate live-server task (port 3000).
const API = window.location.port === '3000'
    ? 'http://localhost:5050/api'
    : `${window.location.origin}/api`;

    // ── Helpers ──────────────────────────────────────────────────────────────

    function showError(msg) {
        const el = document.getElementById('err-box');
        el.textContent = msg;
        el.style.display = 'block';
    }
    function hideError() { document.getElementById('err-box').style.display = 'none'; }
    function showNote(msg) {
        const el = document.getElementById('note-box');
        el.textContent = msg;
        el.style.display = 'block';
    }
    function hideNote() { document.getElementById('note-box').style.display = 'none'; }

    const BTNS = ['btn-refresh','btn-audit','btn-spend','btn-discover','btn-load','btn-ps-run'];
    function showSpinner(msg = 'Running\u2026') {
        document.getElementById('spinner-msg').textContent = msg;
        document.getElementById('spinner-overlay').classList.add('visible');
        BTNS.forEach(id => { const el = document.getElementById(id); if (el) el.disabled = true; });
    }
    function hideSpinner() {
        document.getElementById('spinner-overlay').classList.remove('visible');
        BTNS.forEach(id => { const el = document.getElementById(id); if (el) el.disabled = false; });
    }

    function badge(text, cls) { return `<span class="b b-${cls}">${text}</span>`; }

    function copyCmd(el) {
        navigator.clipboard.writeText(el.textContent.trim());
        const orig = el.dataset.origAfter || el.getAttribute('data-orig');
        el.style.opacity = '.5';
        setTimeout(() => el.style.opacity = '', 700);
    }
    document.addEventListener('click', e => {
        if (e.target.classList.contains('cmd')) copyCmd(e.target);
    });

    // ── Fetch with client-side timeout ───────────────────────────────────────
    function fetchWithTimeout(url, options = {}, timeoutMs = 30_000) {
        const controller = new AbortController();
        const timerId = setTimeout(
            () => controller.abort(new Error(`Request timed out after ${timeoutMs / 1000}s`)),
            timeoutMs
        );
        return fetch(url, { ...options, signal: controller.signal })
            .finally(() => clearTimeout(timerId));
    }

    // ── Az CLI status ────────────────────────────────────────────────────────

    async function checkAzStatus() {
        const badge = document.getElementById('az-status-badge');
        badge.textContent = 'Checking\u2026';
        badge.className = 'status-unk';
        try {
            const r = await fetchWithTimeout(`${API}/diag/az-status`, {}, 10_000);
            const d = await r.json();
            if (d.loggedIn) {
                const sub = d.account?.name || d.account?.id || 'Logged in';
                badge.textContent = `\u2713 ${sub}`;
                badge.className = 'status-ok';
            } else {
                badge.textContent = '\u2717 Not logged in \u2013 run: az login';
                badge.className = 'status-bad';
            }
        } catch {
            badge.textContent = `\u26A0 API not reachable (${new URL(API).host})`;
            badge.className = 'status-bad';
        }
    }

    // ── Load cached report ───────────────────────────────────────────────────

    async function loadReport() {
        hideError(); hideNote();
        showSpinner('Loading cached report\u2026');
        try {
            const r = await fetchWithTimeout(`${API}/diag/report`, {}, 30_000);
            if (!r.ok) {
                const d = await r.json().catch(() => ({}));
                showError(d.error || `HTTP ${r.status}`);
                return;
            }
            const data = await r.json();
            renderReport(data);
            showNote('Cached report loaded. Use "Run Full Report" to refresh from Azure.');
        } catch (ex) {
            showError(`Cannot reach API \u2013 is dotnet running? (${ex.message})`);
        } finally {
            hideSpinner();
        }
    }

    // ── Run full report via API ──────────────────────────────────────────────

    async function runReport() {
        hideError(); hideNote();
        showSpinner('Running azure-full-report.js \u2014 this may take 30\u201390 seconds\u2026');
        try {
            const r = await fetchWithTimeout(`${API}/diag/refresh`, { method: 'POST' }, 120_000);
            const d = await r.json().catch(() => ({}));
            if (!r.ok) { showError(d.detail || d.title || `HTTP ${r.status}`); return; }
            // Load the freshly generated report
            await loadReport();
        } catch (ex) {
            showError(`Cannot reach API \u2013 is dotnet running? (${ex.message})`);
        } finally {
            hideSpinner();
        }
    }

    // ── Cost audit ───────────────────────────────────────────────────────────

    async function runAudit() {
        hideError(); hideNote();
        showSpinner('Running cost audit\u2026');
        try {
            const r = await fetchWithTimeout(`${API}/diag/audit`, { method: 'POST' }, 120_000);
            const d = await r.json().catch(() => ({}));
            if (!r.ok) { showError(d.detail || `HTTP ${r.status}`); return; }
            renderAuditData(d);
            showNote('Cost audit complete. Scroll down to see improvements and removable resources.');
        } catch (ex) {
            showError(`Cannot reach API \u2013 is dotnet running? (${ex.message})`);
        } finally {
            hideSpinner();
        }
    }

    // ── Spend detail ─────────────────────────────────────────────────────────

    async function runSpend() {
        hideError(); hideNote();
        showSpinner('Fetching spend detail (7 days)\u2026');
        try {
            const r = await fetchWithTimeout(`${API}/diag/spend`, { method: 'POST' }, 120_000);
            const d = await r.json().catch(() => ({}));
            if (!r.ok) { showError(d.detail || `HTTP ${r.status}`); return; }
            renderCostData(d.topDrivers || [], 'cost');
            showNote('Spend detail loaded.');
        } catch (ex) {
            showError(`Cannot reach API \u2013 is dotnet running? (${ex.message})`);
        } finally {
            hideSpinner();
        }
    }

    // ── Discover apps ────────────────────────────────────────────────────────

    async function runDiscover() {
        hideError(); hideNote();
        showSpinner('Discovering Azure apps\u2026');
        try {
            const r = await fetchWithTimeout(`${API}/diag/discover`, { method: 'POST' }, 120_000);
            const d = await r.json().catch(() => ({}));
            if (!r.ok) { showError(d.detail || `HTTP ${r.status}`); return; }
            const apps = d.apps ?? d.webApps ?? [];
            renderDiscoverResults(apps);
            showNote(`Discovered ${apps.length} app${apps.length !== 1 ? 's' : ''}.`);
        } catch (ex) {
            showError(`Cannot reach API \u2013 is dotnet running? (${ex.message})`);
        } finally {
            hideSpinner();
        }
    }

    // ── Render helpers ───────────────────────────────────────────────────────

    function renderReport(data) {
        // Meta + stale-data warning
        const meta = document.getElementById('report-meta');
        if (data.generatedAt || data.subscription) {
            let staleHtml = '';
            if (data.generatedAt) {
                const ageMs  = Date.now() - new Date(data.generatedAt).getTime();
                const ageDays = Math.floor(ageMs / 86400000);
                if (ageDays >= 7) {
                    staleHtml = `<div class="note-box" style="margin-top:.5rem">&#x26A0;&#xFE0F; Report is <strong>${ageDays} day${ageDays!==1?'s':''} old</strong> \u2014 consider running a fresh report for current data.</div>`;
                }
            }
            meta.innerHTML = `Generated: <strong>${escHtml(data.generatedAt ?? '\u2014')}</strong> &nbsp;|&nbsp; Sub: <strong>${escHtml(data.subscription?.name ?? data.subscription?.id ?? '\u2014')}</strong>${staleHtml}`;
            meta.style.display = 'block';
        }

        // Normalise field paths to actual report JSON shape
        const allResourceSummary = data.allResourceSummary ?? {};
        const totalResources     = allResourceSummary.total ?? 0;
        const webServices        = data.webServices?.services ?? data.webApps ?? [];
        const safeRemove         = data.unusedResources ?? data.safeToRemove ?? [];
        const advisor            = data.advisorHighlights ?? data.advisor?.recommendations ?? [];
        const improvements       = buildImprovements(data);

        const strip = document.getElementById('summary');
        strip.innerHTML = `
            <div class="stat-card"><div class="num">${totalResources}</div><div class="lbl">Total Resources</div></div>
            <div class="stat-card ok"><div class="num">${webServices.length}</div><div class="lbl">Web Services</div></div>
            <div class="stat-card danger"><div class="num">${safeRemove.length}</div><div class="lbl">Safe to Remove</div></div>
            <div class="stat-card warn"><div class="num">${advisor.length}</div><div class="lbl">Advisor Flags</div></div>
            <div class="stat-card warn"><div class="num">${improvements.length}</div><div class="lbl">Improvements</div></div>
        `;
        strip.style.display = 'grid';

        renderSafeToRemove(safeRemove);
        renderImprovements(improvements);
        renderWebServices(webServices);
        renderAllResourcesByType(allResourceSummary);
        renderCostData(data.cost?.topCostDrivers ?? [], 'cost');
        renderAdvisor(advisor);

        // Extra sections if data present
        if ((data.sslExpiry?.length ?? 0) > 0)    renderSSL(data.sslExpiry);
        if ((data.configDrift?.length ?? 0) > 0)  renderConfigDrift(data.configDrift);
        if (data.securityPosture?.findings?.length) renderSecurity(data.securityPosture);
    }

    function buildImprovements(data) {
        const items = [];
        // Free tier opportunities
        (data.freeTier?.canGoFree ?? []).forEach(r => {
            items.push({
                title: `Downgrade ${r.name} (${r.label ?? r.type})`,
                detail: r.recommendation ?? '',
                cmd: r.upgradeAction ?? null,
            });
        });
        // Config drift issues
        (data.configDrift ?? []).forEach(r => {
            (r.issues ?? []).filter(i => i.severity === 'high' || i.severity === 'medium').forEach(issue => {
                items.push({
                    title: `[${r.friendlyName ?? r.name}] ${issue.issue}`,
                    detail: `Severity: ${issue.severity}`,
                    cmd: null,
                });
            });
        });
        return items;
    }

    function renderSafeToRemove(items) {
        const sec  = document.getElementById('sec-remove');
        const list = document.getElementById('remove-list');
        const cnt  = document.getElementById('remove-cnt');
        cnt.textContent = items.length;
        if (!items.length) {
            list.innerHTML = '<div class="empty">No removable resources detected \u2014 great shape!</div>';
        } else {
            list.innerHTML = items.map(i => {
                const conf = i.confidence?.toLowerCase() ?? 'medium';
                return `<div class="str-panel">
                    <div class="str-hdr">\u26A0\uFE0F ${escHtml(i.name ?? i.resourceName ?? 'Unknown')}
                        <span class="conf conf-${conf}">${escHtml(i.confidence ?? 'Medium')}</span>
                    </div>
                    <div class="str-body">
                        <div class="str-row">
                            <div>
                                <div class="str-reason">${escHtml(i.reason ?? '')}</div>
                                ${i.estimatedMonthlySavings ? `<div class="str-saving">\u{1F4B0} Est. saving: $${i.estimatedMonthlySavings}/mo</div>` : ''}
                                ${i.deleteCommand ? `<code class="cmd">${escHtml(i.deleteCommand)}</code>` : ''}
                            </div>
                        </div>
                    </div>
                </div>`;
            }).join('');
        }
        sec.style.display = 'block';
    }

    function renderImprovements(items) {
        const sec  = document.getElementById('sec-improve');
        const list = document.getElementById('improve-list');
        const cnt  = document.getElementById('improve-cnt');
        cnt.textContent = items.length;
        if (!items.length) {
            list.innerHTML = '<div class="empty">No improvements suggested.</div>';
        } else {
            list.innerHTML = items.map(i => `
                <div class="improve-item">
                    <div class="imp-title">${escHtml(i.title ?? '')}</div>
                    <div class="imp-detail">${escHtml(i.detail ?? '')}</div>
                    ${i.cmd ? `<code class="cmd imp-cmd">${escHtml(i.cmd)}</code>` : ''}
                </div>`).join('');
        }
        sec.style.display = 'block';
    }

    function renderWebServices(items) {
        const sec   = document.getElementById('sec-services');
        const tbody = document.getElementById('services-tbody');
        const cnt   = document.getElementById('services-cnt');
        cnt.textContent = items.length;
        if (!items.length) {
            tbody.innerHTML = '<tr><td colspan="7" class="empty">No web services found.</td></tr>';
        } else {
            tbody.innerHTML = items.map(s => {
                // connectivity.statusCode is the real HTTP code; httpStatus is "active"/"stopped" etc
                const statusCode = s.connectivity?.statusCode;
                const ok = s.connectivity?.success === true;
                const httpBadge = statusCode
                    ? badge(statusCode, ok ? 'ok' : 'bad')
                    : (s.httpStatus ? badge(s.httpStatus, ok ? 'ok' : 'warn') : badge('?', 'dim'));
                const ft = s.freeTierCheck;
                const freeBadge = ft?.isOnFreeTier ? badge('Free', 'ok')
                    : (ft?.canGoFree ? badge('Can go Free', 'warn')
                    : (ft?.currentSku ? badge(ft.currentSku, 'info') : badge('—', 'dim')));
                const healthBadge = ok ? badge('Healthy', 'ok') : badge('Unreachable', 'bad');
                const typeShort = (s.resourceType ?? s.type ?? '').split('/').pop() || '—';
                const rt = s.connectivity?.responseTime ? ` ${s.connectivity.responseTime}ms` : '';
                return `<tr>
                    <td><strong>${escHtml(s.friendlyName ?? s.name ?? '')}</strong><br><span class="muted">${escHtml(s.name ?? '')}</span></td>
                    <td>${escHtml(typeShort)}</td>
                    <td>${s.url ? `<a class="link-ext" href="${encodeURI(s.url)}" target="_blank" rel="noopener">${escHtml(s.url)}</a>` : '—'}</td>
                    <td>${httpBadge}<span class="muted">${rt}</span></td>
                    <td>${escHtml(s.sku ?? ft?.currentSku ?? '—')}</td>
                    <td>${freeBadge}</td>
                    <td>${healthBadge}</td>
                </tr>`;
            }).join('');
        }
        sec.style.display = 'block';
    }

    // Renders the allResourceSummary.byType breakdown as a type count table
    function renderAllResourcesByType(summary) {
        const sec   = document.getElementById('sec-resources');
        const tbody = document.getElementById('resources-tbody');
        const cnt   = document.getElementById('resources-cnt');
        const byType = summary.byType ?? {};
        const entries = Object.entries(byType).sort((a,b) => b[1]-a[1]);
        cnt.textContent = summary.total ?? entries.reduce((s,[,c])=>s+c,0);
        if (!entries.length) {
            tbody.innerHTML = '<tr><td colspan="6" class="empty">No resources.</td></tr>';
        } else {
            tbody.innerHTML = entries.map(([type, count]) => {
                const typeShort = type.split('/').pop() || type;
                return `<tr>
                    <td><strong>${escHtml(typeShort)}</strong></td>
                    <td><span class="b b-info">${escHtml(type)}</span></td>
                    <td colspan="2" class="muted">—</td>
                    <td colspan="2"><strong style="color:var(--accent-color)">${count}</strong> instance${count>1?'s':''}</td>
                </tr>`;
            }).join('');
        }
        sec.style.display = 'block';
    }

    function renderSSL(items) {
        // Inject SSL section dynamically after advisor section
        let sec = document.getElementById('sec-ssl');
        if (!sec) {
            sec = document.createElement('div');
            sec.id = 'sec-ssl';
            document.querySelector('main').appendChild(sec);
        }
        const expiringSoon = items.filter(i => i.daysLeft != null && i.daysLeft < 60 && !i.error);
        const errors       = items.filter(i => i.error);
        sec.innerHTML = `
            <h2 class="section-head">&#x1F512; SSL Certificates <span class="cnt-badge${expiringSoon.length?'.red':''}">${items.length}</span></h2>
            <div style="overflow-x:auto">
            <table><thead><tr><th>Name</th><th>URL</th><th>Expires</th><th>Days Left</th><th>Status</th></tr></thead>
            <tbody>${items.map(i=>{
                const cls = i.error ? 'bad' : i.daysLeft < 30 ? 'bad' : i.daysLeft < 60 ? 'warn' : 'ok';
                const status = i.error ? badge('Error','bad') : badge(i.daysLeft+'d', cls);
                return `<tr>
                    <td><strong>${escHtml(i.name??'')}</strong></td>
                    <td><a class="link-ext" href="${encodeURI(i.url??'')}" target="_blank" rel="noopener">${escHtml(i.url??'')}</a></td>
                    <td>${escHtml(i.expiry??'—')}</td>
                    <td>${i.daysLeft??'—'}</td>
                    <td>${status}${i.error?` <span class="muted">${escHtml(i.error)}</span>`:''}</td>
                </tr>`;
            }).join('')}</tbody></table></div>`;
        sec.style.display = 'block';
    }

    function renderConfigDrift(items) {
        let sec = document.getElementById('sec-drift');
        if (!sec) {
            sec = document.createElement('div');
            sec.id = 'sec-drift';
            document.querySelector('main').appendChild(sec);
        }
        sec.innerHTML = `
            <h2 class="section-head">&#x2699;&#xFE0F; Config Drift <span class="cnt-badge gold">${items.length}</span></h2>
            ${items.map(r=>`
                <div class="improve-item">
                    <div class="imp-title">${escHtml(r.friendlyName??r.name??'')} &mdash; ${r.issueCount} issue${r.issueCount!==1?'s':''}</div>
                    ${(r.issues??[]).map(i=>`<div class="imp-detail" style="color:${i.severity==='high'?'#ff5050':i.severity==='medium'?'#ffd700':'#b4b4b4'}">${escHtml(i.issue)}</div>`).join('')}
                </div>`).join('')}`;
        sec.style.display = 'block';
    }

    function renderSecurity(posture) {
        let sec = document.getElementById('sec-security');
        if (!sec) {
            sec = document.createElement('div');
            sec.id = 'sec-security';
            document.querySelector('main').appendChild(sec);
        }
        const findings = posture.findings ?? [];
        sec.innerHTML = `
            <h2 class="section-head">&#x1F6E1;&#xFE0F; Security Posture
                <span class="cnt-badge red">${posture.high??0} high</span>
                <span class="cnt-badge gold">&nbsp;${posture.medium??0} med</span>
            </h2>
            <div style="overflow-x:auto"><table><thead><tr><th>Severity</th><th>Resource</th><th>Finding</th></tr></thead>
            <tbody>${findings.map(f=>{
                const sev=(f.severity??'').toLowerCase();
                const cls=sev==='high'?'bad':sev==='medium'?'warn':'dim';
                return `<tr>
                    <td>${badge(f.severity??'—',cls)}</td>
                    <td>${escHtml(f.resource??f.name??'—')}</td>
                    <td>${escHtml(f.finding??f.issue??'—')}</td>
                </tr>`;
            }).join('')}</tbody></table></div>`;
        sec.style.display = 'block';
    }

    function renderCostData(items, sectionId) {
        const sec   = document.getElementById('sec-cost');
        const tbody = document.getElementById('cost-tbody');
        const cnt   = document.getElementById('cost-cnt');
        const arr = Array.isArray(items) ? items : [];
        cnt.textContent = arr.length;
        if (!arr.length) {
            tbody.innerHTML = '<tr><td colspan="4" class="empty">No cost data available.</td></tr>';
        } else {
            tbody.innerHTML = arr.map(c => `<tr>
                <td><strong>${escHtml(c.name ?? c.resourceName ?? c.resource ?? '—')}</strong></td>
                <td>${escHtml(c.resourceGroup ?? c.resourceGroupName ?? '—')}</td>
                <td><strong style="color:var(--accent-color)">$${Number(c.cost ?? c.totalCost ?? 0).toFixed(4)}</strong></td>
                <td>${escHtml(c.currency ?? 'USD')}</td>
            </tr>`).join('');
        }
        sec.style.display = 'block';
    }

    function renderAdvisor(items) {
        const sec   = document.getElementById('sec-advisor');
        const tbody = document.getElementById('advisor-tbody');
        const cnt   = document.getElementById('advisor-cnt');
        cnt.textContent = items.length;
        if (!items.length) {
            tbody.innerHTML = '<tr><td colspan="4" class="empty">No Advisor recommendations.</td></tr>';
        } else {
            tbody.innerHTML = items.map(a => {
                const imp = (a.impact ?? a.severity ?? '').toLowerCase();
                const impClass = imp === 'high' ? 'bad' : imp === 'medium' ? 'warn' : 'dim';
                return `<tr>
                    <td>${badge(a.impact ?? a.severity ?? '—', impClass)}</td>
                    <td>${escHtml(a.category ?? '—')}</td>
                    <td>${escHtml(a.resourceName ?? a.resource ?? '—')}</td>
                    <td>${escHtml(a.shortDescription?.solution ?? a.recommendation ?? a.problem ?? '—')}</td>
                </tr>`;
            }).join('');
        }
        sec.style.display = 'block';
    }

    function renderAuditData(data) {
        const safeRemove   = data.unusedResources ?? data.safeToRemove ?? [];
        const improvements = data.recommendations ?? data.freeTierOpportunities ?? [];
        renderSafeToRemove(safeRemove);
        renderImprovements(improvements.map(i => ({
            title: i.name ?? i.resourceName ?? i.title ?? '',
            detail: i.note ?? i.message ?? i.detail ?? '',
            cmd: i.command ?? i.cmd ?? null,
        })));
    }

    // ── Render discovered apps (Finding #6) ──────────────────────────────────
    function renderDiscoverResults(apps) {
        let sec = document.getElementById('sec-discover');
        if (!sec) {
            sec = document.createElement('div');
            sec.id = 'sec-discover';
            document.querySelector('main').appendChild(sec);
        }
        const count = apps.length;
        if (count === 0) {
            sec.innerHTML = `<h2 class="section-head">&#x1F50D; Discovered Apps <span class="cnt-badge">0</span></h2>
                <div class="ok-box">No Azure apps found in the current subscription.</div>`;
        } else {
            sec.innerHTML = `<h2 class="section-head">&#x1F50D; Discovered Apps
                <span class="cnt-badge green">${count}</span></h2>
                <div style="overflow-x:auto"><table>
                <thead><tr><th>Name</th><th>Resource Group</th><th>URL</th><th>Status</th><th>Location</th></tr></thead>
                <tbody>${apps.map(a => {
                    const state    = (a.state ?? a.status ?? '').toLowerCase();
                    const stateCls = state === 'running' ? 'ok' : state === 'stopped' ? 'bad' : 'warn';
                    return `<tr>
                        <td><strong>${escHtml(a.name ?? a.friendlyName ?? '\u2014')}</strong></td>
                        <td>${escHtml(a.resourceGroup ?? a.resourceGroupName ?? '\u2014')}</td>
                        <td>${a.url
                            ? `<a class="link-ext" href="${encodeURI(a.url)}" target="_blank" rel="noopener">${escHtml(a.url)}</a>`
                            : '\u2014'}</td>
                        <td><span class="b b-${stateCls}">${escHtml(a.state ?? a.status ?? '\u2014')}</span></td>
                        <td>${escHtml(a.location ?? '\u2014')}</td>
                    </tr>`;
                }).join('')}</tbody></table></div>`;
        }
        sec.style.display = 'block';
        sec.scrollIntoView({ behavior: 'smooth', block: 'start' });
    }

    function escHtml(str) {
        if (!str) return '';
        return String(str)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;');
    }

    // ── Run PS1 full scan ─────────────────────────────────────────────────────

    async function runPsScript() {
        hideError(); hideNote();
        showSpinner('Running PowerShell full scan\u2026 (3\u20138 min, check terminal for progress)');
        try {
            const r = await fetchWithTimeout(`${API}/diag/ps-run`, { method: 'POST' }, 600_000);
            const d = await r.json().catch(() => ({}));
            if (!r.ok) { showError(d.detail || d.title || `HTTP ${r.status}: ${d.error || ''}`); return; }
            await loadPsReport();
            showNote('PS1 full scan complete. Rich HTML report also available at /azure-ps-report.html');
        } catch (ex) {
            showError(`Cannot reach API \u2013 is dotnet running? (${ex.message})`);
        } finally {
            hideSpinner();
        }
    }

    async function loadPsReport() {
        try {
            const r = await fetchWithTimeout(`${API}/diag/ps-report`, {}, 30_000);
            if (!r.ok) return;
            const data = await r.json();
            renderPsReport(data);
        } catch { /* ignore */ }
    }

    function renderPsReport(data) {
        const sec = document.getElementById('sec-ps-report');
        if (!sec) return;

        const sub       = data.Subscription ?? data.subscription ?? {};
        const resSumm   = data.ResourceSummary ?? {};
        const ws        = data.WebServices ?? {};
        const ft        = data.FreeTierAnalysis ?? {};
        const unused    = data.UnusedResources ?? [];
        const cost      = data.Cost ?? {};
        const drift     = data.ConfigDrift ?? [];
        const storage   = data.StorageInventory ?? [];
        const ssl       = data.SslExpiry ?? [];
        const ai        = data.AppInsights ?? [];
        const actions   = data.QuickActions ?? [];

        const totalIssues = unused.filter(u=>(u.Impact??u.impact??'').toLowerCase()==='high').length
            + ssl.filter(s=>s.DaysLeft!=null&&s.DaysLeft<30).length
            + drift.reduce((n,d)=>n+(d.IssueCount??0),0)
            + storage.reduce((n,s)=>n+(s.IssueCount??0),0);

        const ageMs  = data.GeneratedAt ? Date.now() - new Date(data.GeneratedAt).getTime() : 0;
        const ageDays = Math.floor(ageMs / 86400000);
        const staleWarn = ageDays >= 7
            ? `<div class="note-box">&#x26A0;&#xFE0F; PS1 report is ${ageDays} day${ageDays!==1?'s':''} old.</div>`
            : '';

        sec.innerHTML = `
            <h2 class="section-head">&#x26A1; PowerShell Full Scan
                <span class="cnt-badge${totalIssues?'.red':''}">~${totalIssues} issues</span>
                <a href="/azure-ps-report.html" target="_blank" rel="noopener"
                   style="margin-left:auto;font-size:.75rem;color:var(--accent-color);text-decoration:none;">
                   &#x1F5D7; Open Full HTML Report &rarr;</a>
            </h2>
            ${staleWarn}
            <p class="muted" style="margin-bottom:.75rem">Generated: ${data.GeneratedAt??'—'} &nbsp;|&nbsp;
               Sub: <strong>${escHtml(sub.Name??sub.name??'—')}</strong> &nbsp;|&nbsp;
               Duration: ${data.DurationSeconds??'?'}s</p>
            <div class="diag-grid" style="margin-bottom:1.5rem">
                <div class="stat-card"><div class="num">${resSumm.Total??0}</div><div class="lbl">Total Resources</div></div>
                <div class="stat-card ok"><div class="num">${ws.Total??0}</div><div class="lbl">Web Services</div></div>
                <div class="stat-card ok"><div class="num">${ft.OnFree?.length??0}</div><div class="lbl">On Free Tier</div></div>
                <div class="stat-card warn"><div class="num">${ft.CanGoFree?.length??0}</div><div class="lbl">Can Go Free</div></div>
                <div class="stat-card danger"><div class="num">${unused.length}</div><div class="lbl">Unused Flagged</div></div>
                <div class="stat-card warn"><div class="num">${drift.reduce((n,d)=>n+(d.IssueCount??0),0)}</div><div class="lbl">Config Issues</div></div>
                <div class="stat-card danger"><div class="num">${ssl.filter(s=>s.DaysLeft!=null&&s.DaysLeft<30).length}</div><div class="lbl">SSL Expiring</div></div>
                <div class="stat-card warn"><div class="num">${ai.length}</div><div class="lbl">App Insights</div></div>
            </div>
            ${ft.CanGoFree?.length ? `
            <h3 style="color:#ffd700;margin-bottom:.5rem">&#x1F4C9; Can Go Free (${ft.CanGoFree.length})</h3>
            <div style="overflow-x:auto"><table><thead><tr><th>Name</th><th>Type</th><th>Current SKU</th><th>Free SKU</th><th>Note</th></tr></thead><tbody>
            ${(ft.CanGoFree??[]).map(r=>`<tr>
                <td><strong>${escHtml(r.Name??r.name??'')}</strong></td>
                <td>${escHtml(r.Label??r.label??r.Type??r.type??'')}</td>
                <td><span class="b b-warn">${escHtml(r.CurrentSku??r.currentSku??'?')}</span></td>
                <td><span class="b b-ok">${escHtml(r.FreeSku??r.freeSku??'?')}</span></td>
                <td class="muted">${escHtml(r.Note??r.note??'')}</td>
            </tr>`).join('')}
            </tbody></table></div>` : '<div class="ok-box">&#x2713; All applicable resources are on the free tier!</div>'}
            ${unused.length ? `
            <h3 style="color:#ff5050;margin:1rem 0 .5rem">&#x26A0; Unused / Idle (${unused.length})</h3>
            <div style="overflow-x:auto"><table><thead><tr><th>Impact</th><th>Resource</th><th>Issue</th><th>Remediation</th></tr></thead><tbody>
            ${unused.map(u=>{const imp=(u.Impact??u.impact??'INFO').toUpperCase();const cls=imp==='HIGH'?'bad':imp==='MEDIUM'?'warn':'dim';
                return `<tr><td>${badge(imp,cls)}</td><td><strong>${escHtml(u.ResourceName??u.resourceName??'')}</strong></td><td>${escHtml(u.Issue??u.issue??'')}</td>
                <td>${u.Recommendation?`<code class="cmd">${escHtml(u.Recommendation)}</code>`:'—'}</td></tr>`;
            }).join('')}
            </tbody></table></div>` : ''}
            ${actions.length ? `
            <h3 style="color:var(--primary-color);margin:1rem 0 .5rem">&#x26A1; Quick Actions (${actions.length})</h3>
            ${actions.map(a=>`<code class="cmd">${escHtml(a)}</code>`).join('')}` : ''}
        `;
        sec.style.display = 'block';
    }

    // ── Init ─────────────────────────────────────────────────────────────────

    checkAzStatus();
    loadReport();   // Try to load any cached report on page load
    loadPsReport(); // Also try to load any existing PS1 report