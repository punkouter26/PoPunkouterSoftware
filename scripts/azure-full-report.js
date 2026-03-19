/**
 * azure-full-report.js
 * One-stop Azure audit for PoPunkouterSoftware
 *
 * What it does (single pass):
 *   1. Verifies az login & loads subscription info
 *   2. Discovers every App Service, Container App and Static Web App
 *   3. Live-tests HTTP connectivity to every web service URL
 *   4. Queries ALL Azure resources via Resource Graph
 *   5. Checks every resource against a free-tier knowledge base
 *   6. Detects unused / idle resources + Azure Advisor flags
 *   7. Pulls 30-day consumption cost data
 *   8. Diffs discovered apps against the current apps.json
 *   9. Writes a single azure-full-report.json + prints a console summary
 *
 * Usage:
 *   npm run azure-report
 *
 * Prerequisites:
 *   az login   (Azure CLI authenticated)
 */

import { exec } from 'child_process';
import { promisify } from 'util';
import https from 'https';
import http from 'http';
import tls from 'tls';
import { writeFile, readFile, unlink } from 'fs/promises';
import { existsSync } from 'fs';
import { URL } from 'url';
import { tmpdir } from 'os';
import { join } from 'path';

const execAsync = promisify(exec);

// ─── Config ────────────────────────────────────────────────────────────────────

const CONFIG = {
    outputFile:    './PoPunkouterSoftware/wwwroot/data/azure-full-report.json',
    appsJsonPath:  './PoPunkouterSoftware/wwwroot/data/apps.json',
    httpTimeout:   10_000,
    userAgent:     'PoPunkouterSoftware-Audit/2.0',
};

// ─── Category / name inference (web apps) ─────────────────────────────────────

const CATEGORY_KEYWORDS = {
    games:       ['game', 'quiz', 'tictac', 'connect', 'drop', 'race', 'ragdoll', 'reflex', 'type'],
    ai:          ['ai', 'gpt', 'openai', 'translate', 'trump', 'debate', 'rap'],
    productivity:['tracker', 'review', 'stocks', 'weight', 'remove', 'idea', 'news'],
    creative:    ['image', 'redo', 'joker', 'photo'],
};

function inferCategory(name) {
    const n = name.toLowerCase();
    for (const [cat, kws] of Object.entries(CATEGORY_KEYWORDS)) {
        if (kws.some(k => n.includes(k))) return cat;
    }
    return 'productivity';
}

function inferDescription(name) {
    const base = name.replace(/^(app-|ca-|swa-)?po/i, '').replace(/-?(web|api|server|app|prod)$/i, '');
    const cat  = inferCategory(name);
    const desc = { games: `Interactive ${base} game`, ai: `AI-powered ${base} app`, productivity: `${base} productivity tool`, creative: `Creative ${base} app` };
    return desc[cat] || `${base} web application`;
}

function inferTechnologies(resourceType, tags = {}) {
    const map = {
        'Microsoft.Web/sites':         ['Azure App Service', 'Blazor'],
        'Microsoft.App/containerApps': ['Azure Container Apps', 'Docker'],
        'Microsoft.Web/staticSites':   ['Azure Static Web Apps', 'JavaScript'],
    };
    const techs = [...(map[resourceType] || [])];
    if (tags.technologies) techs.push(...tags.technologies.split(',').map(t => t.trim()));
    return [...new Set(techs)];
}

function getCanonicalName(name) {
    return name
        .replace(/^(swa-|stapp-|wa-|app-|api-|ca-)/i, '')
        .replace(/(-api|-web|-server|-app|-prod)$/i, '')
        .replace(/-([a-z0-9]{9,})$/i, (m, seg) =>
            /\d/.test(seg) && /[a-z]/i.test(seg) ? '' : m)  // strip Azure random suffixes
        .toLowerCase();
}

function getFriendlyName(canonicalName) {
    const allParts = canonicalName.split('-').filter(Boolean);
    // Deduplicate consecutive identical segments (e.g. porobotstocks-porobotstocks)
    const deduped  = allParts.filter((p, i) => i === 0 || p !== allParts[i - 1]);
    const joined   = deduped.join('-');
    const clean    = joined.replace(/^po/, '');
    if (!clean) return joined;
    const pascal = clean.split('-').filter(Boolean).map(p => p[0].toUpperCase() + p.slice(1)).join('');
    return 'Po' + pascal;
}

// Use resource group as friendly name when it follows the Po* convention
function friendlyFromContext(rawName, resourceGroup) {
    if (resourceGroup && /^Po[A-Z]/.test(resourceGroup) && resourceGroup !== 'PoShared') {
        return resourceGroup;
    }
    return getFriendlyName(getCanonicalName(rawName));
}

// ─── Free-tier knowledge base ──────────────────────────────────────────────────

const FREE_TIER_MAP = {
    'Microsoft.Web/sites': {
        label: 'App Service (Web App)', freeSku: 'F1', freeSkuLabel: 'Free (F1)',
        paidSkus: ['B1','B2','B3','S1','S2','S3','P1V2','P2V2','P3V2','P1V3','P2V3','P3V3'],
        note: 'Free F1 provides 60 CPU-min/day, 1 GB RAM. Switch if traffic is low.',
    },
    'Microsoft.Web/serverFarms': {
        label: 'App Service Plan', freeSku: 'F1', freeSkuLabel: 'Free (F1)',
        paidSkus: ['B1','B2','B3','S1','S2','S3','P1V2','P2V2'],
        note: 'Downgrade to F1 if all hosted apps have low traffic. Delete if no apps are hosted.',
    },
    'Microsoft.Web/staticSites': {
        label: 'Static Web App', freeSku: 'Free', freeSkuLabel: 'Free',
        paidSkus: ['Standard'],
        note: 'Free tier: 100 GB bandwidth/month + custom domains. Sufficient for personal/hobby sites.',
    },
    'Microsoft.App/containerApps': {
        label: 'Container App', freeSku: null, freeSkuLabel: 'No persistent free tier',
        paidSkus: ['Consumption'],
        freeQuota: '180k vCPU-s + 360k GiB-s free/month — ensure min-replicas=0',
        note: 'Scale to zero (min-replicas=0) to stay in free monthly quota.',
    },
    'Microsoft.ContainerRegistry/registries': {
        label: 'Container Registry', freeSku: null, freeSkuLabel: 'No free tier',
        paidSkus: ['Basic','Standard','Premium'],
        note: 'Basic ~$5/mo. Consider GitHub Container Registry (ghcr.io) for free private images.',
    },
    'Microsoft.DocumentDB/databaseAccounts': {
        label: 'Cosmos DB', freeSku: 'Free', freeSkuLabel: 'Free tier (1 000 RU/s + 25 GB)',
        paidSkus: ['Standard','Provisioned'],
        note: 'One free-tier Cosmos DB per subscription. Ensure enableFreeTier=true.',
    },
    'Microsoft.Sql/servers/databases': {
        label: 'Azure SQL Database', freeSku: 'Free', freeSkuLabel: 'Free offer (32 GB serverless)',
        paidSkus: ['Basic','Standard','Premium','GeneralPurpose','BusinessCritical'],
        note: 'One free Azure SQL offer per subscription (serverless 32 GB).',
    },
    'Microsoft.DBforPostgreSQL/flexibleServers': {
        label: 'Azure DB for PostgreSQL', freeSku: 'Burstable_B1ms', freeSkuLabel: 'Free offer (B1ms)',
        paidSkus: ['Burstable_B2s','GeneralPurpose_D2s_v3','MemoryOptimized_E2s_v3'],
        note: '12 months free (new accounts): 750 hrs B1ms, 32 GB storage.',
    },
    'Microsoft.DBforMySQL/flexibleServers': {
        label: 'Azure DB for MySQL', freeSku: 'Burstable_B1ms', freeSkuLabel: 'Free offer (B1ms)',
        paidSkus: ['Burstable_B2s','GeneralPurpose_D2s_v3'],
        note: '12 months free (new accounts): 750 hrs B1ms, 32 GB storage.',
    },
    'Microsoft.Storage/storageAccounts': {
        label: 'Storage Account', freeSku: null, freeSkuLabel: '5 GB Blob free/month (12 mo, new accounts)',
        paidSkus: ['Standard_LRS','Standard_GRS','Standard_ZRS','Premium_LRS'],
        note: 'Use LRS for lowest cost. Enable lifecycle policies to archive/delete old data.',
    },
    'Microsoft.CognitiveServices/accounts': {
        label: 'Cognitive Services / Azure AI', freeSku: 'F0', freeSkuLabel: 'Free (F0)',
        paidSkus: ['S0','S1','S2','S3'],
        note: 'F0 provides limited free calls/month — sufficient for dev/hobby use.',
    },
    'Microsoft.Search/searchServices': {
        label: 'Azure AI Search', freeSku: 'free', freeSkuLabel: 'Free (1 svc, 3 indexes, 50 MB)',
        paidSkus: ['basic','standard','standard2','standard3'],
        note: 'One free search service per subscription.',
    },
    'Microsoft.SignalRService/SignalR': {
        label: 'Azure SignalR Service', freeSku: 'Free', freeSkuLabel: 'Free (20 connections)',
        paidSkus: ['Standard'],
        note: 'Free tier: 20 concurrent connections, 20k messages/day.',
    },
    'Microsoft.NotificationHubs/namespaces': {
        label: 'Notification Hubs', freeSku: 'Free', freeSkuLabel: 'Free (1M pushes/month)',
        paidSkus: ['Basic','Standard'],
        note: 'Free tier allows 1 million push notifications/month.',
    },
    'microsoft.insights/components': {
        label: 'Application Insights', freeSku: null, freeSkuLabel: '5 GB/month free ingestion',
        paidSkus: ['pergb2018'],
        freeQuota: '5 GB/month free — enable sampling + set a daily cap',
        note: 'Enable adaptive sampling to stay under 5 GB/month free limit.',
    },
    'Microsoft.OperationalInsights/workspaces': {
        label: 'Log Analytics Workspace', freeSku: 'Free', freeSkuLabel: 'Free (500 MB/day, 7-day retention)',
        paidSkus: ['PerGB2018','Standard','Premium'],
        note: 'Free SKU: 500 MB/day, 7-day retention. Set a data cap on paid SKUs.',
    },
    'Microsoft.Network/publicIPAddresses': {
        label: 'Public IP Address', freeSku: null, freeSkuLabel: 'First 5 Basic static IPs free',
        paidSkus: ['Standard'],
        note: 'Delete Public IPs not attached to any resource — Standard IPs cost even when idle.',
    },
    'Microsoft.Network/virtualNetworks': {
        label: 'Virtual Network', freeSku: null, freeSkuLabel: 'VNet creation is free; egress is billed',
        paidSkus: [],
        note: 'Delete empty VNets. Minimise cross-region peering to reduce egress costs.',
    },
    'Microsoft.KeyVault/vaults': {
        label: 'Key Vault', freeSku: null, freeSkuLabel: '~$0.03 per 10k operations',
        paidSkus: ['standard','premium'],
        note: 'Standard vault is cost-effective. Consolidate multiple vaults when possible.',
    },
    'Microsoft.ServiceBus/namespaces': {
        label: 'Service Bus Namespace', freeSku: null, freeSkuLabel: 'No free tier — Basic from ~$0.05/M ops',
        paidSkus: ['Basic','Standard','Premium'],
        note: 'Use Basic if only simple queues are needed; downgrade from Standard/Premium.',
    },
    'Microsoft.EventHub/namespaces': {
        label: 'Event Hubs Namespace', freeSku: null, freeSkuLabel: 'No free tier — Basic from ~$9/mo',
        paidSkus: ['Basic','Standard','Premium','Dedicated'],
        note: 'Delete namespace if no active event hubs are receiving events.',
    },
    'Microsoft.Cdn/profiles': {
        label: 'Azure CDN Profile', freeSku: null, freeSkuLabel: 'No free tier',
        paidSkus: ['Standard_Microsoft','Standard_Akamai','Premium_Verizon'],
        note: 'Static Web Apps include a built-in CDN (free) — consider removing standalone CDN.',
    },
};

// ─── Azure CLI helpers ─────────────────────────────────────────────────────────

async function az(args, fallback = null) {
    try {
        const { stdout, stderr } = await execAsync(`az ${args}`);
        if (stderr && !stderr.toLowerCase().includes('warning')) {
            process.stderr.write(`  az warning: ${stderr.trim().split('\n')[0]}\n`);
        }
        return JSON.parse(stdout || (fallback === null ? 'null' : JSON.stringify(fallback)));
    } catch {
        return fallback;
    }
}

// ─── HTTP connectivity test ────────────────────────────────────────────────────

// #3 helpers — status classification and Azure default-page detection
function getStatusClass(code) {
    if (!code) return 'error';
    if (code < 300) return '2xx-ok';
    if (code < 400) return '3xx-redirect';
    if (code < 500) return '4xx-client-error';
    return '5xx-server-error';
}

function detectAzureErrorPage(body) {
    const b = (body || '').toLowerCase();
    return b.includes('this web app is stopped') ||
           b.includes('hey, this is the default web app page') ||
           b.includes('error 404 - web app not found') ||
           (b.includes('application error') && b.includes('azure')) ||
           (b.includes('service unavailable') && b.includes('azure'));
}

// #3 — Enriched HTTP check: HEAD first, GET fallback on 405, Azure-error-page detection
function testUrl(rawUrl) {
    return new Promise(resolve => {
        const start = Date.now();
        let urlObj;
        try { urlObj = new URL(rawUrl); } catch {
            return resolve({ success: false, statusCode: null, responseTime: 0, error: 'Invalid URL', statusClass: 'error', isAzureErrorPage: false });
        }
        const lib = urlObj.protocol === 'https:' ? https : http;

        function makeRequest(method, cb) {
            const req = lib.request(
                { method, hostname: urlObj.hostname, path: urlObj.pathname || '/',
                  timeout: CONFIG.httpTimeout, headers: { 'User-Agent': CONFIG.userAgent } },
                res => {
                    let body = '';
                    if (method === 'GET') {
                        res.on('data', chunk => { if (body.length < 3000) body += chunk; });
                        res.on('end', () => cb(null, res.statusCode, body));
                    } else {
                        res.resume();
                        cb(null, res.statusCode, '');
                    }
                }
            );
            req.on('error', e => cb(e, null, ''));
            req.on('timeout', () => { req.destroy(); cb(new Error('Timeout'), null, ''); });
            req.end();
        }

        makeRequest('HEAD', (err, code, _body) => {
            if (err) {
                return resolve({ success: false, statusCode: null, responseTime: Date.now() - start,
                    error: err.message, statusClass: 'error', isAzureErrorPage: false });
            }
            // HEAD not supported or redirect that needs body — retry with GET
            if (code === 405 || code === 0) {
                makeRequest('GET', (err2, code2, body2) => {
                    const elapsed = Date.now() - start;
                    if (err2) return resolve({ success: false, statusCode: null, responseTime: elapsed,
                        error: err2.message, statusClass: 'error', isAzureErrorPage: false });
                    const azureError = detectAzureErrorPage(body2);
                    resolve({ success: code2 >= 200 && code2 < 400 && !azureError,
                        statusCode: code2, responseTime: elapsed,
                        error: azureError ? 'Azure default/stopped page' : null,
                        statusClass: getStatusClass(code2), isAzureErrorPage: azureError });
                });
            } else {
                resolve({ success: code >= 200 && code < 400, statusCode: code,
                    responseTime: Date.now() - start, error: null,
                    statusClass: getStatusClass(code), isAzureErrorPage: false });
            }
        });
    });
}

// ─── Step 1 — Subscription ────────────────────────────────────────────────────

async function getSubscription() {
    return az('account show --output json');
}

// ─── Step 2 — Discover web services ──────────────────────────────────────────

async function discoverWebServices() {
    // Query at subscription level — avoids dependence on az group list permissions
    process.stdout.write('  Querying web apps ...');
    const [webApps, containerApps, staticApps] = await Promise.all([
        az('webapp list --output json', []),
        az('containerapp list --output json', []),
        az('staticwebapp list --output json', []),
    ]);
    process.stdout.write(` done\n`);

    return [
        ...webApps.map(a => ({
            name: a.name,
            resourceGroup: a.resourceGroup,
            resourceType: 'Microsoft.Web/sites',
            url: a.defaultHostName ? `https://${a.defaultHostName}` : null,
            sku: a.sku?.name || null,
            state: a.state || null,              // #4 — platform state: Running/Stopped/Starting
            id: a.id || null,                    // #2/#5 — ARM id for health + metrics calls
            serverFarmId: a.serverFarmId || null, // #9 — for shared plan dependency map
            tags: a.tags || {},
        })),
        ...containerApps.map(a => ({
            name: a.name,
            resourceGroup: a.resourceGroup,
            resourceType: 'Microsoft.App/containerApps',
            url: a.properties?.configuration?.ingress?.fqdn
                ? `https://${a.properties.configuration.ingress.fqdn}` : null,
            sku: 'Consumption',
            state: a.properties?.runningStatus || a.properties?.provisioningState || null,
            id: a.id || null,
            tags: a.tags || {},
            minReplicas: a.properties?.template?.scale?.minReplicas ?? null,
        })),
        ...staticApps.map(a => ({
            name: a.name,
            resourceGroup: a.resourceGroup,
            resourceType: 'Microsoft.Web/staticSites',
            url: a.defaultHostname ? `https://${a.defaultHostname}` : null,
            sku: a.sku?.name || 'Free',
            state: 'Running',
            id: a.id || null,
            tags: a.tags || {},
        })),
    ];
}

// ─── Step 3 — Connectivity tests ──────────────────────────────────────────────

// #2 — Azure Resource Health: platform-side state from Azure's fabric (Available / Unavailable / Degraded)
async function getResourceHealth(resourceId) {
    if (!resourceId) return null;
    try {
        const result = await az(
            `rest --method GET --url "https://management.azure.com${resourceId}/providers/Microsoft.ResourceHealth/availabilityStatuses/current?api-version=2023-07-01-preview"`,
            null
        );
        if (!result?.properties) return null;
        return {
            availabilityState: result.properties.availabilityState || 'Unknown',
            summary:           result.properties.summary || '',
            reasonType:        result.properties.reasonType || null,
        };
    } catch { return null; }
}

async function testConnectivity(services) {
    const results = [];
    for (const svc of services) {
        if (!svc.url) {
            results.push({ ...svc, connectivity: { success: false, statusCode: null, responseTime: 0, error: 'No URL', statusClass: 'error', isAzureErrorPage: false }, httpStatus: 'no-url', resourceHealth: null });
            continue;
        }
        process.stdout.write(`  ${svc.name} ...`);
        // #2 + #3: HTTP check and Resource Health run in parallel
        const [conn, health] = await Promise.all([
            testUrl(svc.url),
            getResourceHealth(svc.id),
        ]);
        const httpStatus = conn.success ? 'active' : (conn.statusCode != null && conn.statusCode >= 400 ? 'broken' : 'unreachable');
        const icon = conn.success ? '✅' : '❌';
        const healthStr = health ? ` [Health: ${health.availabilityState}]` : '';
        const stateStr  = svc.state && svc.state !== 'Running' ? ` (platform: ${svc.state})` : '';
        process.stdout.write(` ${icon} ${conn.statusCode ?? conn.error} (${conn.responseTime}ms)${stateStr}${healthStr}\n`);
        if (conn.isAzureErrorPage) process.stdout.write(`       ⚠  Azure default/stopped page detected\n`);
        results.push({ ...svc, connectivity: conn, httpStatus, resourceHealth: health });
    }
    return results;
}

// ─── Step 4 — All Azure resources ─────────────────────────────────────────────

// #5 — App Service metrics for the past 7 days (Requests, Http5xx, AverageResponseTime)
async function getAppMetrics7Days(resourceId) {
    if (!resourceId) return null;
    const end   = new Date();
    const start = new Date(end); start.setDate(end.getDate() - 7);
    const fmt   = d => d.toISOString();
    const metrics = await az(
        `monitor metrics list --resource "${resourceId}" --metric "Requests" "Http5xx" "AverageResponseTime" --start-time "${fmt(start)}" --end-time "${fmt(end)}" --interval P1D --output json`,
        null
    );
    if (!metrics?.value) return null;
    const out = {};
    for (const m of metrics.value) {
        const key = m.name?.value;
        const pts = m.timeseries?.[0]?.data || [];
        const total = pts.reduce((s, p) => s + (p.total ?? p.average ?? p.count ?? 0), 0);
        out[key] = +total.toFixed(2);
    }
    return out; // e.g. { Requests: 1234, Http5xx: 5, AverageResponseTime: 234.5 }
}

// #6 — Detect zombie apps: App Services with 0 HTTP requests in the measured period
function detectZombieApps(services, metricsMap) {
    return services
        .filter(svc => svc.resourceType === 'Microsoft.Web/sites' && svc.id && metricsMap.has(svc.id))
        .filter(svc => {
            const m = metricsMap.get(svc.id);
            const req = m?.Requests ?? m?.requests ?? null;
            return req !== null && req === 0;
        })
        .map(svc => ({
            name:          svc.name,
            resourceGroup: svc.resourceGroup,
            url:           svc.url,
            httpStatus:    svc.httpStatus,
            platformState: svc.state,
            requests7Days: 0,
            recommendation: `az webapp stop --name "${svc.name}" --resource-group "${svc.resourceGroup}"`,
        }));
}
async function getAllResources() {
    // Primary: az resource list — reliable, no extension needed, works on all subscriptions
    const resources = await az('resource list --output json', []);
    if (resources.length > 0) return resources;

    // Fallback: Resource Graph (lighter query — no 'properties' to avoid connection reset)
    const graph = await az(
        `graph query -q "Resources | project id, name, type, resourceGroup, location, sku, kind, tags" --output json`,
        null
    );
    return Array.isArray(graph?.data) && graph.data.length > 0 ? graph.data : [];
}

// ─── Step 5 — Free-tier analysis ──────────────────────────────────────────────

function getSku(resource) {
    return resource.sku?.name || resource.sku?.tier || resource.properties?.sku?.name || resource.kind || null;
}

function checkFreeTier(resource) {
    const info = FREE_TIER_MAP[resource.type] || FREE_TIER_MAP[resource.type?.toLowerCase()];
    if (!info) return null;

    const currentSku = getSku(resource);
    const currentUp  = currentSku?.toUpperCase();
    const isOnFree   = info.freeSku && currentUp && currentUp === info.freeSku.toUpperCase();
    const isOnPaid   = info.paidSkus.length > 0 && currentUp && info.paidSkus.map(s => s.toUpperCase()).includes(currentUp);

    return {
        label:         info.label,
        currentSku:    currentSku || 'unknown',
        freeSku:       info.freeSku,
        freeSkuLabel:  info.freeSkuLabel,
        freeQuota:     info.freeQuota || null,
        isOnFreeTier:  !!isOnFree,
        isOnPaidTier:  !!isOnPaid,
        canGoFree:     !!info.freeSku && !isOnFree,
        recommendation: info.note,
    };
}

function analyzeFreeTiers(allResources) {
    const onFree = [], canGoFree = [], noFreeTier = [];
    for (const r of allResources) {
        const check = checkFreeTier(r);
        if (!check) continue;
        const entry = {
            name: r.name, type: r.type, label: check.label,
            resourceGroup: r.resourceGroup, location: r.location,
            currentSku: check.currentSku, freeSku: check.freeSku,
            freeSkuLabel: check.freeSkuLabel, freeQuota: check.freeQuota,
            recommendation: check.recommendation,
        };
        if (check.isOnFreeTier)  onFree.push(entry);
        else if (check.canGoFree) canGoFree.push({ ...entry, upgradeAction: `Downgrade to free SKU: ${check.freeSku}` });
        else                      noFreeTier.push(entry);
    }
    return { onFree, canGoFree, noFreeTier };
}

// ─── Step 6 — Unused resource detection ───────────────────────────────────────

function detectUnused(allResources, advisorRecs) {
    const flagged = [];

    // Azure Advisor — Cost category only (High-impact reliability recs are separate noise)
    for (const rec of advisorRecs.filter(r => r.category === 'Cost')) {
        flagged.push({
            source:         'Azure Advisor',
            resourceId:     rec.resourceMetadata?.resourceId || rec.id,
            resourceName:   rec.shortDescription?.solution || rec.shortDescription?.problem || 'Unknown',
            issue:          rec.shortDescription?.problem || '',
            impact:         rec.impact,
            recommendation: rec.shortDescription?.solution || '',
            potentialSavingPerMonth: rec.extendedProperties?.annualSavingsAmount
                ? `$${(rec.extendedProperties.annualSavingsAmount / 12).toFixed(2)}`
                : null,
        });
    }

    // App Service Plans with 0 apps
    for (const r of allResources.filter(r => r.type === 'Microsoft.Web/serverFarms')) {
        const n = r.properties?.numberOfSites ?? r.numberOfSites ?? null;
        if (n === 0 || n === '0') {
            flagged.push({
                source: 'Heuristic', resourceId: r.id, resourceName: r.name, resourceType: r.type,
                issue: 'App Service Plan has 0 hosted apps',
                impact: 'high',
                recommendation: `az appservice plan delete --name "${r.name}" --resource-group "${r.resourceGroup}" --yes`,
            });
        }
    }

    // Unattached public IPs
    for (const r of allResources.filter(r => r.type === 'Microsoft.Network/publicIPAddresses')) {
        if (!r.properties?.ipConfiguration && !r.properties?.natGateway) {
            flagged.push({
                source: 'Heuristic', resourceId: r.id, resourceName: r.name, resourceType: r.type,
                issue: 'Public IP not attached to any resource',
                impact: 'medium',
                recommendation: `az network public-ip delete --name "${r.name}" --resource-group "${r.resourceGroup}"`,
            });
        }
    }

    // Empty VNets (no subnets or no resources)
    for (const r of allResources.filter(r => r.type === 'Microsoft.Network/virtualNetworks')) {
        const subnets = r.properties?.subnets || [];
        if (subnets.length === 0) {
            flagged.push({
                source: 'Heuristic', resourceId: r.id, resourceName: r.name, resourceType: r.type,
                issue: 'Virtual Network has no subnets',
                impact: 'low',
                recommendation: `az network vnet delete --name "${r.name}" --resource-group "${r.resourceGroup}"`,
            });
        }
    }

    return flagged;
}

// ─── Step 7 — Cost (last 30 days) ─────────────────────────────────────────────

// #1 — Use Cost Management REST API (works on all subscription types incl. VS/sponsored)
async function getCost30Days(subscriptionId) {
    const today = new Date();
    const start = new Date(today); start.setDate(today.getDate() - 30);
    const fmt = d => d.toISOString().split('T')[0];

    // Write query body to temp file to avoid shell-escaping issues on Windows
    const body = {
        type: 'Usage',
        timeframe: 'Custom',
        timePeriod: { from: fmt(start), to: fmt(today) },
        dataset: {
            granularity: 'None',
            aggregation: { totalCost: { name: 'PreTaxCost', function: 'Sum' } },
            grouping: [
                { type: 'Dimension', name: 'ServiceName' },
                { type: 'Dimension', name: 'ResourceGroupName' },
            ],
        },
    };
    const tmpFile = join(tmpdir(), `az-cost-body-${Date.now()}.json`);
    let cmResult = null;
    try {
        await writeFile(tmpFile, JSON.stringify(body));
        cmResult = await az(
            `rest --method POST --url "https://management.azure.com/subscriptions/${subscriptionId}/providers/Microsoft.CostManagement/query?api-version=2023-11-01" --body "@${tmpFile}"`,
            null
        );
    } finally {
        await unlink(tmpFile).catch(() => {});
    }

    if (cmResult?.properties?.rows?.length > 0) {
        const cols    = (cmResult.properties.columns || []).map(c => c.name.toLowerCase());
        const costIdx = cols.findIndex(c => c.includes('pretax') || c.includes('cost'));
        const svcIdx  = cols.findIndex(c => c.includes('service'));
        const rgIdx   = cols.findIndex(c => c.includes('resourcegroup'));
        const currIdx = cols.findIndex(c => c.includes('currency'));
        let total = 0;
        let currency = 'USD';
        const byService = {};
        for (const row of cmResult.properties.rows) {
            const cost = costIdx >= 0 ? (parseFloat(row[costIdx]) || 0) : 0;
            const svc  = svcIdx  >= 0 ? (row[svcIdx]  || 'Unknown') : 'Unknown';
            const rg   = rgIdx   >= 0 ? (row[rgIdx]   || '') : '';
            if (currIdx >= 0 && row[currIdx]) currency = row[currIdx];
            const key  = rg ? `${svc} (${rg})` : svc;
            byService[key] = (byService[key] || 0) + cost;
            total += cost;
        }
        const top = Object.entries(byService)
            .filter(([, c]) => c > 0)
            .map(([name, cost]) => ({ name, cost: +cost.toFixed(4) }))
            .sort((a, b) => b.cost - a.cost)
            .slice(0, 20);
        return {
            totalCost30Days: +total.toFixed(4),
            totalFormatted:  `$${total.toFixed(2)}`,
            currency,
            topCostDrivers:  top,
            source:          'CostManagement',
            rawRecordCount:  cmResult.properties.rows.length,
            note: total === 0
                ? 'All costs $0.00 — subscription is covered by Azure credits (Visual Studio/MSDN/sponsored). Check Cost Management portal for credit utilization.'
                : null,
        };
    }

    // Fallback: legacy consumption list (many fields return "None" on VS subscriptions)
    const records = await az(`consumption usage list --start-date ${fmt(start)} --end-date ${fmt(today)} --output json`, []);
    function parseCost(r) {
        const raw = r.pretaxCost ?? r.costInBillingCurrency ?? r.cost ??
                    r.paygCostInBillingCurrency ?? r.effectivePrice ?? null;
        const val = parseFloat(raw);
        return isNaN(val) ? 0 : val;
    }
    const byResource = {};
    let total2 = 0;
    for (const r of records) {
        const cost = parseCost(r);
        const key  = r.instanceName || r.resourceId || 'Unknown';
        byResource[key] = (byResource[key] || 0) + cost;
        total2 += cost;
    }
    const top2 = Object.entries(byResource)
        .filter(([, c]) => c > 0)
        .map(([name, cost]) => ({ name, cost: +cost.toFixed(4) }))
        .sort((a, b) => b.cost - a.cost)
        .slice(0, 20);
    return {
        totalCost30Days: +total2.toFixed(4),
        totalFormatted:  `$${total2.toFixed(2)}`,
        currency: records[0]?.billingCurrencyCode || 'USD',
        topCostDrivers:  top2,
        source:          'ConsumptionAPI-fallback',
        rawRecordCount:  records.length,
        note: records.length === 0
            ? 'No cost data returned — check Azure Cost Management in the portal.'
            : total2 === 0
            ? 'All costs $0.00 — subscription may be covered by credits.'
            : null,
    };
}

// ─── Step 8 — apps.json diff ──────────────────────────────────────────────────

async function diffAppsJson(webServices) {
    if (!existsSync(CONFIG.appsJsonPath)) {
        return { currentCount: 0, newApps: webServices.map(s => s.name), removedApps: [], updatedApps: [] };
    }
    const existing = JSON.parse(await readFile(CONFIG.appsJsonPath, 'utf-8'));
    const existingApps = existing.apps || [];
    const existingIds  = new Set(existingApps.map(a => a.id));

    // Deduplicate web services by canonical name (SWA wins)
    const canonicalMap = new Map();
    for (const svc of webServices) {
        const canon = getCanonicalName(svc.name);
        const cur   = canonicalMap.get(canon);
        if (!cur
            || svc.resourceType === 'Microsoft.Web/staticSites'
            || (svc.httpStatus === 'active' && cur.httpStatus !== 'active')) {
            canonicalMap.set(canon, svc);
        }
    }

    const discoveredIds = new Set([...canonicalMap.keys()]);
    return {
        currentCount: existingApps.length,
        discoveredCount: canonicalMap.size,
        newApps:     [...canonicalMap.entries()].filter(([c]) => !existingIds.has(c)).map(([, s]) => s.name),
        removedApps: existingApps.filter(a => !discoveredIds.has(a.id)).map(a => a.name),
        updatedApps: [...canonicalMap.entries()].filter(([c]) => existingIds.has(c)).map(([, s]) => s.name),
    };
}

// ─── Section A — Security Posture ─────────────────────────────────────────────
// Queries Azure Security Center assessments and flags high/medium issues

async function getSecurityPosture() {
    const assessments = await az(
        `rest --method GET --url "https://management.azure.com/subscriptions/{subscriptionId}/providers/Microsoft.Security/assessments?api-version=2021-06-01"`,
        null
    );
    // Prefer advisor security category as it works without Defender for Cloud plan
    const securityRecs = await az('advisor recommendation list --category Security --output json', []);
    const findings = securityRecs.map(r => ({
        title:         r.shortDescription?.problem || 'Unknown',
        impact:        r.impact || 'Low',
        resourceName:  r.resourceMetadata?.resourceId?.split('/').pop() ?? 'Subscription',
        resourceId:    r.resourceMetadata?.resourceId ?? null,
        category:      'Security',
        remedy:        r.shortDescription?.solution ?? '',
    })).sort((a, b) => {
        const ord = { High:0, Medium:1, Low:2 };
        return (ord[a.impact] ?? 3) - (ord[b.impact] ?? 3);
    });
    return {
        total:    findings.length,
        high:     findings.filter(f => f.impact === 'High').length,
        medium:   findings.filter(f => f.impact === 'Medium').length,
        low:      findings.filter(f => f.impact === 'Low').length,
        findings,
    };
}

// ─── Section B — 6-Month Cost Trend ───────────────────────────────────────────
// Pulls monthly spend for the last 6 full calendar months

async function getCostTrend6Months(subscriptionId) {
    const today = new Date();
    const start = new Date(today.getFullYear(), today.getMonth() - 6, 1);
    const fmt   = d => d.toISOString().split('T')[0];
    const body  = {
        type: 'Usage',
        timeframe: 'Custom',
        timePeriod: { from: fmt(start), to: fmt(today) },
        dataset: {
            granularity: 'Monthly',
            aggregation: { totalCost: { name: 'PreTaxCost', function: 'Sum' } },
        },
    };
    const tmpFile = join(tmpdir(), `az-cost-trend-${Date.now()}.json`);
    let result = null;
    try {
        await writeFile(tmpFile, JSON.stringify(body));
        result = await az(
            `rest --method POST --url "https://management.azure.com/subscriptions/${subscriptionId}/providers/Microsoft.CostManagement/query?api-version=2023-11-01" --body "@${tmpFile}"`,
            null
        );
    } finally {
        await unlink(tmpFile).catch(() => {});
    }
    if (!result?.properties?.rows?.length) return { months: [] };
    const cols    = (result.properties.columns || []).map(c => c.name.toLowerCase());
    const costIdx = cols.findIndex(c => c.includes('pretax') || c.includes('cost'));
    const dateIdx = cols.findIndex(c => c.includes('billing') || c.includes('date') || c.includes('usage'));
    const months  = result.properties.rows.map(row => {
        const rawDate = dateIdx >= 0 ? String(row[dateIdx]) : '';
        const label   = rawDate.length >= 6
            ? `${rawDate.slice(0,4)}-${rawDate.slice(4,6)}`
            : rawDate;
        return { month: label, cost: +(parseFloat(row[costIdx] ?? 0)).toFixed(2) };
    }).sort((a, b) => a.month.localeCompare(b.month));
    return { months };
}

// ─── Section C — SSL Certificate Expiry ───────────────────────────────────────
// Checks TLS cert expiry for each web service URL using Node's TLS socket

async function checkSslExpiry(services) {
    const results = [];
    for (const svc of services) {
        if (!svc.url || !svc.url.startsWith('https://')) {
            results.push({ name: svc.name, url: svc.url, error: 'Non-HTTPS', daysLeft: null, expiry: null, subject: null });
            continue;
        }
        let urlObj;
        try { urlObj = new URL(svc.url); } catch {
            results.push({ name: svc.name, url: svc.url, error: 'Invalid URL', daysLeft: null, expiry: null, subject: null });
            continue;
        }
        await new Promise(resolve => {
            const sock = tls.connect(
                { host: urlObj.hostname, port: 443, servername: urlObj.hostname, timeout: 8000 },
                () => {
                    try {
                        const cert  = sock.getPeerCertificate();
                        const valid = cert?.valid_to ? new Date(cert.valid_to) : null;
                        const days  = valid ? Math.floor((valid - Date.now()) / 86400000) : null;
                        results.push({
                            name:     svc.name,
                            url:      svc.url,
                            expiry:   valid?.toISOString().split('T')[0] ?? null,
                            daysLeft: days,
                            subject:  cert?.subject?.CN ?? null,
                            error:    null,
                            warning:  days !== null && days < 30 ? `Expires in ${days} days!` : null,
                        });
                    } catch (e) {
                        results.push({ name: svc.name, url: svc.url, error: e.message, daysLeft: null, expiry: null, subject: null });
                    }
                    sock.end();
                    resolve();
                }
            );
            sock.on('error', e => {
                results.push({ name: svc.name, url: svc.url, error: e.message, daysLeft: null, expiry: null, subject: null });
                resolve();
            });
            sock.on('timeout', () => {
                results.push({ name: svc.name, url: svc.url, error: 'Timeout', daysLeft: null, expiry: null, subject: null });
                sock.destroy();
                resolve();
            });
        });
    }
    return results;
}

// ─── Section D — Configuration Drift ──────────────────────────────────────────
// Checks each App Service for common misconfigurations

async function getConfigDrift(services) {
    const appServices = services.filter(s => s.resourceType === 'Microsoft.Web/sites');
    if (!appServices.length) return [];
    const results = [];
    for (const svc of appServices) {
        const [cfg, auth] = await Promise.all([
            az(`webapp config show --name "${svc.name}" --resource-group "${svc.resourceGroup}" --output json`, null),
            az(`webapp auth show    --name "${svc.name}" --resource-group "${svc.resourceGroup}" --output json`, null),
        ]);
        if (!cfg) continue;
        const issues = [];
        if (cfg.ftpsState && cfg.ftpsState !== 'Disabled' && cfg.ftpsState !== 'FtpsOnly')
            issues.push({ severity: 'high',   issue: 'FTP is enabled (not FTPS-only)', field: 'ftpsState', value: cfg.ftpsState });
        if (cfg.http20Enabled === false)
            issues.push({ severity: 'low',    issue: 'HTTP/2 disabled', field: 'http20Enabled', value: false });
        if (cfg.minTlsVersion && cfg.minTlsVersion < '1.2')
            issues.push({ severity: 'high',   issue: `Min TLS ${cfg.minTlsVersion} (should be ≥1.2)`, field: 'minTlsVersion', value: cfg.minTlsVersion });
        if (cfg.alwaysOn === false)
            issues.push({ severity: 'low',    issue: 'Always-On disabled (cold starts)', field: 'alwaysOn', value: false });
        if (cfg.cors?.allowedOrigins?.includes('*'))
            issues.push({ severity: 'medium', issue: 'CORS open to * (all origins)', field: 'cors', value: '*' });
        if (auth && auth.enabled === false)
            issues.push({ severity: 'info',   issue: 'Authentication not configured', field: 'auth', value: false });
        results.push({
            name:          svc.name,
            friendlyName:  svc.friendlyName ?? friendlyFromContext(svc.name, svc.resourceGroup),
            resourceGroup: svc.resourceGroup,
            url:           svc.url,
            issueCount:    issues.length,
            issues,
        });
    }
    return results;
}

// ─── Section E — Storage Account Inventory ────────────────────────────────────
// Enumerates storage accounts and flags public blob access

async function getStorageInventory(allResources) {
    const storageAccounts = allResources.filter(r =>
        r.type?.toLowerCase() === 'microsoft.storage/storageaccounts'
    );
    if (!storageAccounts.length) return [];
    const results = [];
    for (const sa of storageAccounts) {
        const detail = await az(
            `storage account show --name "${sa.name}" --resource-group "${sa.resourceGroup}" --output json`,
            null
        );
        const publicAccess = detail?.allowBlobPublicAccess ?? detail?.properties?.allowBlobPublicAccess ?? null;
        const httpsOnly    = detail?.enableHttpsTrafficOnly ?? detail?.properties?.supportsHttpsTrafficOnly ?? null;
        const tlsMin       = detail?.minimumTlsVersion ?? detail?.properties?.minimumTlsVersion ?? null;
        const lifecycle    = detail?.properties?.deleteRetentionPolicy?.enabled ?? false;
        const sku          = detail?.sku?.name ?? sa.sku?.name ?? 'unknown';
        const issues = [];
        if (publicAccess === true)
            issues.push({ severity: 'high',   issue: '⚠ Public blob access ENABLED — potential data exposure' });
        if (httpsOnly === false)
            issues.push({ severity: 'high',   issue: 'HTTP traffic allowed (HTTPS-only is off)' });
        if (tlsMin && tlsMin < 'TLS1_2')
            issues.push({ severity: 'medium', issue: `Minimum TLS is ${tlsMin} — should be TLS1_2+` });
        if (!lifecycle)
            issues.push({ severity: 'low',    issue: 'No blob lifecycle/delete retention policy' });
        results.push({
            name:             sa.name,
            resourceGroup:    sa.resourceGroup,
            sku,
            location:         sa.location,
            publicBlobAccess: publicAccess,
            httpsOnly,
            minTls:           tlsMin,
            issueCount:       issues.length,
            issues,
        });
    }
    // Sort: most issues first
    return results.sort((a, b) => b.issueCount - a.issueCount);
}

// ─── apps.json fresh writer ────────────────────────────────────────────────────
// Rebuilds apps.json completely from Azure-discovered services.
// No existing file is read — always starts from zero.

async function writeAppsJson(services) {
    // Deduplicate by canonical name: SWA wins, then active wins, then first seen
    const canonicalMap = new Map();
    for (const svc of services) {
        const canon = getCanonicalName(svc.name);
        const cur   = canonicalMap.get(canon);
        if (!cur
            || svc.resourceType === 'Microsoft.Web/staticSites'
            || (svc.httpStatus === 'active' && cur.httpStatus !== 'active')) {
            canonicalMap.set(canon, svc);
        }
    }

    const apps = [...canonicalMap.values()]
        .map(svc => ({
            id:           getCanonicalName(svc.name),
            name:         friendlyFromContext(svc.name, svc.resourceGroup),
            description:  svc.tags?.description || inferDescription(svc.name),
            category:     svc.tags?.category    || inferCategory(svc.name),
            status:       svc.httpStatus === 'active' ? 'active'
                        : svc.httpStatus === 'broken' ? 'broken'
                        : 'disabled',
            technologies: inferTechnologies(svc.resourceType, svc.tags || {}),
            url:          svc.url || '',
        }))
        .sort((a, b) => a.name.localeCompare(b.name));

    await writeFile(CONFIG.appsJsonPath, JSON.stringify({ apps }, null, 2) + '\n');
    console.log(`  ✅  apps.json rebuilt from scratch: ${apps.length} apps → ${CONFIG.appsJsonPath}`);
    return apps.length;
}

// ─── Console report printer ────────────────────────────────────────────────────

// #9 — Shared resource dependency map (which apps share the same App Service Plan or SQL server)
function buildDependencyMap(services, allResources) {
    const planMap = {};
    for (const svc of services) {
        const planName = svc.serverFarmId ? svc.serverFarmId.split('/').pop() : null;
        if (!planName) continue;
        if (!planMap[planName]) planMap[planName] = { planId: svc.serverFarmId, resourceGroup: svc.resourceGroup, apps: [] };
        planMap[planName].apps.push(svc.name);
    }
    const sqlMap = {};
    for (const r of allResources) {
        if (r.type?.toLowerCase() === 'microsoft.sql/servers/databases') {
            const parts   = (r.id || '').split('/');
            const srvIdx  = parts.findIndex(p => p.toLowerCase() === 'servers');
            const srvName = srvIdx >= 0 ? parts[srvIdx + 1] : null;
            if (srvName) {
                if (!sqlMap[srvName]) sqlMap[srvName] = { resourceGroup: r.resourceGroup, databases: [] };
                if (r.name?.toLowerCase() !== 'master') sqlMap[srvName].databases.push(r.name);
            }
        }
    }
    return {
        sharedPlans: Object.entries(planMap)
            .map(([planName, v]) => ({ planName, resourceGroup: v.resourceGroup, appCount: v.apps.length, apps: v.apps }))
            .sort((a, b) => b.appCount - a.appCount),
        sqlServers: Object.entries(sqlMap)
            .map(([serverName, v]) => ({ serverName, resourceGroup: v.resourceGroup, dbCount: v.databases.length, databases: v.databases })),
    };
}

// #7 — App Insights: query exception + request counts for the last 7 days
async function getAppInsightsMetrics(allResources) {
    const aiComponents = allResources.filter(r => r.type?.toLowerCase() === 'microsoft.insights/components');
    if (aiComponents.length === 0) return [];
    const sevenDaysAgo = new Date(); sevenDaysAgo.setDate(sevenDaysAgo.getDate() - 7);
    const fmt = d => d.toISOString();
    return Promise.all(aiComponents.map(async comp => {
        const [exc, req, failed] = await Promise.all([
            az(`monitor app-insights metrics show --app "${comp.name}" --resource-group "${comp.resourceGroup}" --metric exceptions/count --start-time ${fmt(sevenDaysAgo)} --output json`, null),
            az(`monitor app-insights metrics show --app "${comp.name}" --resource-group "${comp.resourceGroup}" --metric requests/count --start-time ${fmt(sevenDaysAgo)} --output json`, null),
            az(`monitor app-insights metrics show --app "${comp.name}" --resource-group "${comp.resourceGroup}" --metric requests/failed --start-time ${fmt(sevenDaysAgo)} --output json`, null),
        ]);
        return {
            name:                comp.name,
            resourceGroup:       comp.resourceGroup,
            exceptions7Days:     exc?.value?.['exceptions/count']?.sum     ?? null,
            requests7Days:       req?.value?.['requests/count']?.sum       ?? null,
            failedRequests7Days: failed?.value?.['requests/failed']?.sum   ?? null,
        };
    }));
}

// #10 — Delta: load previous run + compare statuses
async function loadPreviousReport() {
    try {
        if (!existsSync(CONFIG.outputFile)) return null;
        return JSON.parse(await readFile(CONFIG.outputFile, 'utf-8'));
    } catch { return null; }
}

function buildDelta(current, previous) {
    if (!previous) return null;
    const prevSvc = new Map((previous.webServices?.services || []).map(s => [s.name, s]));
    const currSvc = new Map((current.webServices?.services   || []).map(s => [s.name, s]));
    const statusChanges = [], newSites = [], removedSites = [];
    for (const [name, cur] of currSvc) {
        const prev = prevSvc.get(name);
        if (!prev) { newSites.push(name); continue; }
        if (prev.httpStatus !== cur.httpStatus) {
            statusChanges.push({
                name, from: prev.httpStatus, to: cur.httpStatus,
                direction: cur.httpStatus === 'active' ? 'recovered' : 'degraded',
            });
        }
    }
    for (const [name] of prevSvc) { if (!currSvc.has(name)) removedSites.push(name); }
    const prevTotal = previous.allResourceSummary?.total ?? 0;
    const currTotal = current.allResourceSummary?.total  ?? 0;
    return {
        previousReportAge: previous.generatedAt
            ? `${Math.round((Date.now() - new Date(previous.generatedAt).getTime()) / 60000)} min ago`
            : 'unknown',
        statusChanges,
        newSites,
        removedSites,
        resourceCountDelta: currTotal - prevTotal,
        degradedSites:  statusChanges.filter(c => c.direction === 'degraded').map(c => c.name),
        recoveredSites: statusChanges.filter(c => c.direction === 'recovered').map(c => c.name),
    };
}

function hr(char = '═', len = 72) { return char.repeat(len); }
function section(title) { console.log(`\n${hr()}\n  ${title}\n${hr()}`); }

function printReport(report) {
    const {
        subscription, webServices, allResourceSummary, freeTier,
        unusedResources, zombieApps, cost, appsJsonDiff,
        dependencyMap, appInsightsMetrics, delta,
    } = report;

    section('SUBSCRIPTION');
    console.log(`  Name   : ${subscription.name}`);
    console.log(`  ID     : ${subscription.id}`);
    console.log(`  Tenant : ${subscription.tenantId}`);
    console.log(`  State  : ${subscription.state}`);

    // ── #10 Delta since last run ───────────────────────────────────────────────
    if (delta) {
        section(`DELTA SINCE LAST RUN  (${delta.previousReportAge})`);
        if (!delta.statusChanges.length && !delta.newSites.length && !delta.removedSites.length) {
            console.log('  ✅  No changes since last run.');
        }
        if (delta.degradedSites.length)  console.log(`\n  🔴 DEGRADED (were active, now not):\n${delta.degradedSites.map(n => `     - ${n}`).join('\n')}`);
        if (delta.recoveredSites.length) console.log(`\n  💚 RECOVERED (were down, now active):\n${delta.recoveredSites.map(n => `     - ${n}`).join('\n')}`);
        if (delta.newSites.length)       console.log(`\n  🆕 NEW sites found:\n${delta.newSites.map(n => `     - ${n}`).join('\n')}`);
        if (delta.removedSites.length)   console.log(`\n  🗑  REMOVED sites:\n${delta.removedSites.map(n => `     - ${n}`).join('\n')}`);
        if (delta.resourceCountDelta !== 0)
            console.log(`\n  📦 Resource count: ${delta.resourceCountDelta > 0 ? '+' : ''}${delta.resourceCountDelta} vs last run`);
    }

    // ── Web services ──────────────────────────────────────────────────────────
    section(`WEB SERVICES  (${webServices.total} total)`);
    const typeLabels = { 'Microsoft.Web/sites': 'App Service', 'Microsoft.App/containerApps': 'Container App', 'Microsoft.Web/staticSites': 'Static Web App' };
    console.log(`\n  By status : ✅ active ${webServices.byStatus.active}  ❌ broken ${webServices.byStatus.broken}  ⚠  unreachable/no-url ${webServices.byStatus.other}`);
    console.log(`  By type   : App Service ${webServices.byType.appService}  Container App ${webServices.byType.containerApp}  Static Web App ${webServices.byType.staticWebApp}\n`);

    for (const svc of webServices.services) {
        const icon = svc.httpStatus === 'active' ? '✅' : svc.httpStatus === 'broken' ? '❌' : '⚠ ';
        const conn = svc.connectivity?.statusCode
            ? `HTTP ${svc.connectivity.statusCode} (${svc.connectivity.responseTime}ms)`
            : svc.connectivity?.error || 'no url';
        const typeLabel = typeLabels[svc.resourceType] || svc.resourceType;
        console.log(`  ${icon}  ${svc.name.padEnd(42)} [${typeLabel}]`);
        console.log(`       ${(svc.url || 'n/a').padEnd(56)} ${conn}`);

        // #4 Platform state + #2 Resource Health + #3 Azure error page
        const infoParts = [];
        if (svc.platformState && svc.platformState !== 'Running')
            infoParts.push(`⚠  Platform: ${svc.platformState}`);
        if (svc.resourceHealth) {
            const hs   = svc.resourceHealth.availabilityState;
            const hIcon = hs === 'Available' ? '💚' : hs === 'Unavailable' ? '🔴' : '🟡';
            infoParts.push(`${hIcon} Health: ${hs}${svc.resourceHealth.summary ? ` — ${svc.resourceHealth.summary}` : ''}`);
        }
        if (svc.connectivity?.isAzureErrorPage)
            infoParts.push('⚠  Serving Azure default/stopped page');
        if (svc.connectivity?.statusClass && svc.connectivity.statusClass !== '2xx-ok' && svc.connectivity.statusCode)
            infoParts.push(`Status class: ${svc.connectivity.statusClass}`);
        if (infoParts.length) console.log(`       ${infoParts.join('  |  ')}`);

        // #5 Metrics
        if (svc.metrics7Days) {
            const m   = svc.metrics7Days;
            const req = m.Requests ?? m.requests ?? null;
            const err = m.Http5xx  ?? m.http5xx  ?? null;
            const rt  = m.AverageResponseTime ?? m.averageResponseTime ?? null;
            const mp  = [];
            if (req !== null) mp.push(`Requests: ${req}`);
            if (err !== null) mp.push(err > 0 ? `Http5xx: ${err} ⚠` : `Http5xx: 0`);
            if (rt  !== null) mp.push(`AvgResponse: ${Math.round(rt)}ms`);
            if (mp.length)    console.log(`       📊 7-day: ${mp.join('  |  ')}`);
        }

        if (svc.freeTierCheck) {
            const ft = svc.freeTierCheck;
            if (ft.isOnFreeTier) console.log(`       💚 Free tier  SKU: ${ft.currentSku}`);
            if (ft.canGoFree)    console.log(`       🔧 Can go free  (currently ${ft.currentSku} → free: ${ft.freeSku})`);
            if (ft.freeQuota)    console.log(`       ℹ️  ${ft.freeQuota}`);
        }
    }

    // ── All resources ─────────────────────────────────────────────────────────
    section(`ALL AZURE RESOURCES  (${allResourceSummary.total} total)`);
    for (const [type, count] of Object.entries(allResourceSummary.byType).sort((a, b) => b[1] - a[1])) {
        console.log(`  ${String(count).padStart(4)}  ${type}`);
    }

    // ── Free tier ─────────────────────────────────────────────────────────────
    section('FREE-TIER ANALYSIS');
    console.log(`\n  ✅  On free tier (${freeTier.onFree.length}):`);
    for (const r of freeTier.onFree)
        console.log(`        ${r.name}  [${r.label}]  SKU: ${r.currentSku}`);
    console.log(`\n  🔧  Can be moved to free tier (${freeTier.canGoFree.length}):`);
    for (const r of freeTier.canGoFree) {
        console.log(`        ${r.name}  [${r.label}]`);
        console.log(`          Current : ${r.currentSku}   →   Free : ${r.freeSku} (${r.freeSkuLabel})`);
        console.log(`          Note    : ${r.recommendation}`);
    }
    console.log(`\n  ℹ️   No free tier available (${freeTier.noFreeTier.length}):`);
    for (const r of freeTier.noFreeTier) {
        console.log(`        ${r.name}  [${r.label}]  SKU: ${r.currentSku}`);
        if (r.freeQuota) console.log(`          Free quota: ${r.freeQuota}`);
        console.log(`          Note: ${r.recommendation}`);
    }

    // ── Unused ────────────────────────────────────────────────────────────────
    section(`UNUSED / IDLE RESOURCES  (${unusedResources.length} flagged)`);
    if (unusedResources.length === 0) console.log('  None detected.');
    for (const item of unusedResources) {
        const imp = (item.impact || 'info').toUpperCase();
        console.log(`\n  [${imp}]  ${item.resourceName}`);
        console.log(`    Source : ${item.source}`);
        if (item.issue)          console.log(`    Issue  : ${item.issue}`);
        if (item.recommendation) console.log(`    Action : ${item.recommendation}`);
        if (item.potentialSavingPerMonth) console.log(`    Saving : ~${item.potentialSavingPerMonth}/mo`);
    }

    // ── #6 Zombie apps ────────────────────────────────────────────────────────
    if (zombieApps?.length > 0) {
        section(`ZOMBIE APPS — 0 REQUESTS IN 7 DAYS  (${zombieApps.length})`);
        for (const z of zombieApps) {
            const stateNote = z.platformState && z.platformState !== 'Running' ? ` [platform: ${z.platformState}]` : '';
            console.log(`  💀  ${z.name}${stateNote}`);
            console.log(`       HTTP: ${z.httpStatus}   RG: ${z.resourceGroup}`);
            console.log(`       Stop: ${z.recommendation}`);
        }
    }

    // ── #7 Application Insights ────────────────────────────────────────────────
    if (appInsightsMetrics?.length > 0) {
        section(`APPLICATION INSIGHTS — LAST 7 DAYS  (${appInsightsMetrics.length} component(s))`);
        for (const ai of appInsightsMetrics) {
            const exc = ai.exceptions7Days ?? 'n/a';
            const req = ai.requests7Days   ?? 'n/a';
            const fail = ai.failedRequests7Days ?? 'n/a';
            const alert = (ai.exceptions7Days > 0 || ai.failedRequests7Days > 0) ? ' ⚠' : '';
            console.log(`  ${ai.name}  [${ai.resourceGroup}]${alert}`);
            console.log(`       Requests: ${req}   Failed: ${fail}   Exceptions: ${exc}`);
        }
    }

    // ── #9 Shared resource dependencies ───────────────────────────────────────
    if (dependencyMap) {
        section('SHARED RESOURCE DEPENDENCIES');
        if (dependencyMap.sharedPlans.length > 0) {
            console.log('\n  App Service Plans (shared risk):');
            for (const p of dependencyMap.sharedPlans) {
                console.log(`    ⚠  ${p.planName}  (${p.appCount} apps share this plan)`);
                for (const app of p.apps) console.log(`         - ${app}`);
            }
        }
        if (dependencyMap.sqlServers.length > 0) {
            console.log('\n  SQL Servers:');
            for (const s of dependencyMap.sqlServers) {
                console.log(`    🗄  ${s.serverName}  [${s.resourceGroup}]  —  ${s.dbCount} database(s): ${s.databases.join(', ')}`);
            }
        }
        if (!dependencyMap.sharedPlans.length && !dependencyMap.sqlServers.length)
            console.log('  No shared plans or SQL servers detected.');
    }

    // ── Cost ──────────────────────────────────────────────────────────────────
    section(`COST — LAST 30 DAYS  (source: ${cost.source || 'unknown'})`);
    if (cost.note) {
        console.log(`  ⚠  ${cost.note}`);
    } else {
        console.log(`  Total : ${cost.totalFormatted} ${cost.currency}\n`);
        console.log('  Top cost drivers:');
        for (const d of cost.topCostDrivers)
            console.log(`    ${`$${d.cost.toFixed(2)}`.padStart(10)}  ${d.name}`);
    }
    if (cost.topCostDrivers?.length === 0 && cost.note)
        console.log('  Visit https://portal.azure.com/#view/Microsoft_Azure_CostManagement for credit utilization.');

    // ── apps.json diff ────────────────────────────────────────────────────────
    section('APPS.JSON SYNC STATUS');
    console.log(`  Current apps.json count  : ${appsJsonDiff.currentCount}`);
    console.log(`  Discovered (deduplicated): ${appsJsonDiff.discoveredCount ?? 'n/a'}`);
    if (appsJsonDiff.newApps.length)     console.log(`\n  🆕 New (not in apps.json):\n${appsJsonDiff.newApps.map(n => `     - ${n}`).join('\n')}`);
    if (appsJsonDiff.removedApps.length) console.log(`\n  🗑  In apps.json but gone from Azure:\n${appsJsonDiff.removedApps.map(n => `     - ${n}`).join('\n')}`);
    if (!appsJsonDiff.newApps.length && !appsJsonDiff.removedApps.length) console.log('  ✅  apps.json is in sync with Azure.');

    // ── Quick actions ─────────────────────────────────────────────────────────
    const actions = [];
    for (const r of freeTier.canGoFree) {
        if (r.type === 'Microsoft.Web/serverFarms')
            actions.push(`az appservice plan update --name "${r.name}" --resource-group "${r.resourceGroup}" --sku F1`);
        if (r.type === 'Microsoft.CognitiveServices/accounts')
            actions.push(`az cognitiveservices account update --name "${r.name}" --resource-group "${r.resourceGroup}" --sku F0`);
        if (r.type === 'Microsoft.Search/searchServices')
            actions.push(`az search service update --name "${r.name}" --resource-group "${r.resourceGroup}" --sku free`);
    }
    for (const u of unusedResources.filter(u => u.source === 'Heuristic' && u.recommendation?.startsWith('az ')))
        actions.push(u.recommendation);
    for (const z of (zombieApps || []))
        actions.push(z.recommendation);
    if (actions.length > 0) {
        section('QUICK ACTIONS (copy-paste ready)');
        actions.forEach(a => console.log(`  ${a}`));
    }

    console.log(`\n${hr()}`);
    console.log(`  Full report saved → ${CONFIG.outputFile}`);
    console.log(`  Generated at       ${report.generatedAt}`);
    console.log(`${hr()}\n`);
}

// ─── Main ──────────────────────────────────────────────────────────────────────

async function main() {
    console.log('\n🔍  PoPunkouterSoftware — Azure Full Report');
    console.log(`    ${new Date().toLocaleString()}`);
    console.log(hr('='));

    // Load previous report BEFORE anything else (delta comparison later)
    const previousReport = await loadPreviousReport();
    if (previousReport) console.log(`  ℹ️  Previous report found (${previousReport.generatedAt}) — delta will be computed.\n`);

    // 1. Auth
    console.log('\n[1/9] Subscription info...');
    const sub = await getSubscription();
    if (!sub) {
        console.error('\n❌  Azure CLI not authenticated. Run `az login` first.\n');
        process.exit(1);
    }
    console.log(`  ${sub.name}  (${sub.id})`);

    // 2. Resource groups (informational only)
    console.log('\n[2/9] Resource groups...');
    const rgs = await az('group list --output json', []);
    if (rgs.length > 0) {
        console.log(`  Found ${rgs.length} resource group(s): ${rgs.map(r => r.name).join(', ')}`);
    } else {
        console.log('  az group list returned 0 (limited permissions) — will derive from Resource Graph data.');
    }

    // 3. Discover web services (subscription-level query, no RG iteration needed)
    console.log('\n[3/9] Discovering web services...');
    const rawServices = await discoverWebServices();
    console.log(`  Total web services found: ${rawServices.length}`);

    // 4. Connectivity + Resource Health (parallel per service)
    console.log('\n[4/9] Testing connectivity + resource health...');
    const services = await testConnectivity(rawServices);

    // 5. All resources
    console.log('\n[5/9] Loading all Azure resources...');
    const allResources = await getAllResources();
    console.log(`  ${allResources.length} resources found.`);

    // Attach free-tier check to each web service
    const resourceMap = new Map(allResources.map(r => [r.name?.toLowerCase(), r]));
    for (const svc of services) {
        const fullResource = resourceMap.get(svc.name.toLowerCase()) || svc;
        const check = checkFreeTier({ ...fullResource, type: fullResource.type || svc.resourceType });
        if (check) svc.freeTierCheck = check;
    }

    // 6. App metrics + zombie detection (parallel fetch for all web apps)
    console.log('\n[6/9] Fetching 7-day app metrics + zombie detection...');
    const webAppsWithId = services.filter(s => s.id);
    const metricsResults = await Promise.all(webAppsWithId.map(s => getAppMetrics7Days(s.id)));
    const metricsMap = new Map();
    webAppsWithId.forEach((s, i) => { metricsMap.set(s.name, metricsResults[i]); });
    // Attach metrics to each service
    for (const svc of services) {
        const m = metricsMap.get(svc.name);
        if (m && !m.error) svc.metrics7Days = m;
    }
    const zombieApps = detectZombieApps(services, metricsMap);
    console.log(`  Metrics fetched: ${metricsMap.size}  Zombie candidates: ${zombieApps.length}`);

    // 7. Application Insights
    console.log('\n[7/9] Application Insights health...');
    const appInsightsMetrics = await getAppInsightsMetrics(allResources);
    console.log(`  App Insights components found: ${appInsightsMetrics.length}`);

    // 8. Analysis
    console.log('\n[8/9] Analysing resources...');
    const allByType = {};
    for (const r of allResources) { allByType[r.type] = (allByType[r.type] || 0) + 1; }

    const freeTier        = analyzeFreeTiers(allResources);
    const advisorRecs     = await az('advisor recommendation list --output json', []);
    const unusedResources = detectUnused(allResources, advisorRecs);
    const advisorHighlights = advisorRecs.filter(r => r.category === 'Cost' || r.impact === 'High').slice(0, 10);
    const dependencyMap   = buildDependencyMap(services, allResources);
    console.log(`  Free tier ok: ${freeTier.onFree.length}  can go free: ${freeTier.canGoFree.length}  unused flagged: ${unusedResources.length}`);

    // 9. Cost + apps.json diff + new sections (parallel)
    console.log('\n[9/14] Cost data + apps.json diff...');
    const [cost, appsJsonDiff] = await Promise.all([
        getCost30Days(sub.id),
        diffAppsJson(services),
    ]);

    console.log('\n[10/14] Security posture...');
    const securityPosture = await getSecurityPosture();
    console.log(`  Security findings: ${securityPosture.total}  (High: ${securityPosture.high}  Medium: ${securityPosture.medium})`);

    console.log('\n[11/14] 6-month cost trend...');
    const costTrend = await getCostTrend6Months(sub.id);
    console.log(`  Months returned: ${costTrend.months.length}`);

    console.log('\n[12/14] SSL certificate expiry...');
    const sslExpiry = await checkSslExpiry(services.filter(s => s.url));
    const expiringSoon = sslExpiry.filter(c => c.daysLeft !== null && c.daysLeft < 30).length;
    console.log(`  Certs checked: ${sslExpiry.length}  Expiring < 30 days: ${expiringSoon}`);

    console.log('\n[13/14] Configuration drift...');
    const configDrift = await getConfigDrift(services);
    const totalDriftIssues = configDrift.reduce((s, a) => s + a.issueCount, 0);
    console.log(`  App Services checked: ${configDrift.length}  Issues found: ${totalDriftIssues}`);

    console.log('\n[14/14] Storage account inventory...');
    const storageInventory = await getStorageInventory(allResources);
    const storageIssues = storageInventory.reduce((s, a) => s + a.issueCount, 0);
    console.log(`  Storage accounts: ${storageInventory.length}  Issues: ${storageIssues}`);

    // ─── Assemble report ───────────────────────────────────────────────────────

    const activeCount      = services.filter(s => s.httpStatus === 'active').length;
    const brokenCount      = services.filter(s => s.httpStatus === 'broken').length;
    const otherCount       = services.length - activeCount - brokenCount;
    const appServiceCount  = services.filter(s => s.resourceType === 'Microsoft.Web/sites').length;
    const containerCount   = services.filter(s => s.resourceType === 'Microsoft.App/containerApps').length;
    const staticCount      = services.filter(s => s.resourceType === 'Microsoft.Web/staticSites').length;

    const report = {
        generatedAt: new Date().toISOString(),
        subscription: {
            name:     sub.name,
            id:       sub.id,
            tenantId: sub.tenantId,
            state:    sub.state,
        },
        webServices: {
            total: services.length,
            byStatus: { active: activeCount, broken: brokenCount, other: otherCount },
            byType:   { appService: appServiceCount, containerApp: containerCount, staticWebApp: staticCount },
            services: services.map(s => ({
                name:           s.name,
                friendlyName:   friendlyFromContext(s.name, s.resourceGroup),
                resourceGroup:  s.resourceGroup,
                resourceType:   s.resourceType,
                url:            s.url || null,
                httpStatus:     s.httpStatus,
                platformState:  s.state || null,
                connectivity:   s.connectivity,
                resourceHealth: s.resourceHealth || null,
                metrics7Days:   s.metrics7Days  || null,
                freeTierCheck:  s.freeTierCheck || null,
                sku:            s.sku || null,
                category:       s.tags?.category || inferCategory(s.name),
                description:    s.tags?.description || inferDescription(s.name),
                technologies:   inferTechnologies(s.resourceType, s.tags),
                tags:           s.tags,
            })),
        },
        allResourceSummary: {
            total:  allResources.length,
            byType: allByType,
        },
        freeTier,
        unusedResources,
        zombieApps,
        advisorHighlights,
        dependencyMap,
        appInsightsMetrics,
        cost,
        appsJsonDiff,
        securityPosture,
        costTrend,
        sslExpiry,
        configDrift,
        storageInventory,
    };

    // Build delta against previous report
    const delta = buildDelta(report, previousReport);
    if (delta) report.delta = delta;

    // Print + save
    printReport(report);
    await writeFile(CONFIG.outputFile, JSON.stringify(report, null, 2));

    // 10. Rebuild apps.json from scratch — no merge with existing data
    console.log('\n[10] Rebuilding apps.json from scratch...');
    await writeAppsJson(services);
}

main().catch(err => {
    console.error('\n❌  Fatal error:', err.message);
    process.exit(1);
});
