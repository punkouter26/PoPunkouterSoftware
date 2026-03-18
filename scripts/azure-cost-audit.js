/**
 * Azure Cost & Usage Audit Script
 * Finds unused resources and verifies free-tier eligibility across your subscription.
 *
 * Usage:
 *   npm run audit-azure
 *
 * Prerequisites:
 *   - Azure CLI installed and authenticated: `az login`
 *   - Contributor or Reader role on the subscription
 */

import { exec } from 'child_process';
import { promisify } from 'util';
import { writeFile } from 'fs/promises';

const execAsync = promisify(exec);

// ─── Configuration ─────────────────────────────────────────────────────────────

const CONFIG = {
    outputFile: 'azure-cost-audit-report.json',
    // Days with zero requests/traffic before a resource is flagged as unused
    unusedThresholdDays: 30,
};

// ─── Free-tier knowledge base ──────────────────────────────────────────────────
// Maps each known resource type to its free-tier SKU (if one exists) and the
// recommended action when the subscription is on a paid SKU.

const FREE_TIER_MAP = {
    // ---------- App Hosting ----------
    'Microsoft.Web/sites': {
        label: 'App Service (Web App)',
        freeSku: 'F1',
        freeSkuLabel: 'Free (F1)',
        paidSkus: ['B1', 'B2', 'B3', 'S1', 'S2', 'S3', 'P1V2', 'P2V2', 'P3V2', 'P1V3', 'P2V3', 'P3V3'],
        note: 'Free F1 tier provides 60 CPU-min/day, 1 GB RAM, shared infrastructure. Switch to F1 if site traffic is low.',
    },
    'Microsoft.Web/serverFarms': {
        label: 'App Service Plan',
        freeSku: 'F1',
        freeSkuLabel: 'Free (F1)',
        paidSkus: ['B1', 'B2', 'B3', 'S1', 'S2', 'S3', 'P1V2', 'P2V2'],
        note: 'Downgrade to F1 plan if all hosted apps have low traffic. Delete the plan if no apps are hosted on it.',
    },
    'Microsoft.Web/staticSites': {
        label: 'Static Web App',
        freeSku: 'Free',
        freeSkuLabel: 'Free',
        paidSkus: ['Standard'],
        note: 'Free tier includes 100 GB bandwidth/month and custom domains. Sufficient for most personal/hobby sites.',
    },
    // ---------- Containers ----------
    'Microsoft.App/containerApps': {
        label: 'Container App',
        freeSku: null, // no persistent free tier, but has generous free quota
        freeSkuLabel: 'No persistent free tier',
        paidSkus: ['Consumption'],
        note: 'Container Apps uses consumption pricing. 180,000 vCPU-seconds and 360,000 GiB-seconds free per month. Scale to zero when idle.',
        freeQuota: '180k vCPU-s + 360k GiB-s free/month — ensure min-replicas=0',
    },
    'Microsoft.ContainerRegistry/registries': {
        label: 'Container Registry',
        freeSku: null,
        freeSkuLabel: 'No free tier',
        paidSkus: ['Basic', 'Standard', 'Premium'],
        note: 'Basic is the cheapest paid tier (~$5/mo). Delete unused images. Consider using GitHub Container Registry (ghcr.io) for free private images.',
    },
    // ---------- Databases ----------
    'Microsoft.DocumentDB/databaseAccounts': {
        label: 'Cosmos DB',
        freeSku: 'Free',
        freeSkuLabel: 'Free tier (1000 RU/s + 25 GB)',
        paidSkus: ['Standard', 'Provisioned'],
        note: 'Only ONE free-tier Cosmos DB account is allowed per subscription. Ensure that account has the "enableFreeTier" property set to true.',
    },
    'Microsoft.Sql/servers/databases': {
        label: 'Azure SQL Database',
        freeSku: 'Free',
        freeSkuLabel: 'Free offer (32 GB, serverless)',
        paidSkus: ['Basic', 'Standard', 'Premium', 'GeneralPurpose', 'BusinessCritical'],
        note: 'The free Azure SQL offer provides 32 GB serverless database. One per subscription. Check currentServiceObjectiveName.',
    },
    'Microsoft.DBforPostgreSQL/flexibleServers': {
        label: 'Azure Database for PostgreSQL',
        freeSku: 'Burstable_B1ms',
        freeSkuLabel: 'Free offer (Burstable B1ms)',
        paidSkus: ['Burstable_B2s', 'GeneralPurpose_D2s_v3', 'MemoryOptimized_E2s_v3'],
        note: '12 months free for new Azure accounts: 750 hrs Burstable B1ms, 32 GB storage. After free period, consider Burstable tier.',
    },
    'Microsoft.DBforMySQL/flexibleServers': {
        label: 'Azure Database for MySQL',
        freeSku: 'Burstable_B1ms',
        freeSkuLabel: 'Free offer (Burstable B1ms)',
        paidSkus: ['Burstable_B2s', 'GeneralPurpose_D2s_v3'],
        note: '12 months free for new Azure accounts: 750 hrs Burstable B1ms, 32 GB storage.',
    },
    // ---------- Storage ----------
    'Microsoft.Storage/storageAccounts': {
        label: 'Storage Account',
        freeSku: null,
        freeSkuLabel: 'No free tier — but 5 GB Blob free/month for 12 months (new accounts)',
        paidSkus: ['Standard_LRS', 'Standard_GRS', 'Standard_ZRS', 'Premium_LRS'],
        note: 'Use LRS (locally redundant) for lowest cost. Delete unused accounts or blobs. Lifecycle policies archive/delete old data automatically.',
    },
    // ---------- Functions & Logic ----------
    'Microsoft.Web/sites/functions': {
        label: 'Azure Functions (site type)',
        freeSku: 'Consumption',
        freeSkuLabel: 'Consumption plan (1M executions free/month)',
        paidSkus: ['Premium', 'Dedicated'],
        note: '1 million function executions and 400,000 GB-s compute free every month on Consumption plan.',
    },
    // ---------- AI / Cognitive ----------
    'Microsoft.CognitiveServices/accounts': {
        label: 'Cognitive Services / Azure AI',
        freeSku: 'F0',
        freeSkuLabel: 'Free (F0)',
        paidSkus: ['S0', 'S1', 'S2', 'S3'],
        note: 'F0 offers limited free calls per minute/month. Sufficient for dev/hobby. Check per-service limits — some services do not expose F0.',
    },
    'Microsoft.MachineLearningServices/workspaces': {
        label: 'Azure Machine Learning Workspace',
        freeSku: null,
        freeSkuLabel: 'No workspace-level free tier',
        paidSkus: ['Basic', 'Enterprise'],
        note: 'Storage, compute, and inference are billed separately. Delete idle compute clusters. Use serverless compute where available.',
    },
    'Microsoft.Search/searchServices': {
        label: 'Azure AI Search',
        freeSku: 'free',
        freeSkuLabel: 'Free (1 service, 3 indexes, 50 MB)',
        paidSkus: ['basic', 'standard', 'standard2', 'standard3'],
        note: 'One free search service per subscription. Sufficient for small apps. Downgrade or consolidate if using paid tiers.',
    },
    // ---------- Communication ----------
    'Microsoft.NotificationHubs/namespaces': {
        label: 'Notification Hubs Namespace',
        freeSku: 'Free',
        freeSkuLabel: 'Free (1M pushes/month)',
        paidSkus: ['Basic', 'Standard'],
        note: 'Free tier allows 1 million push notifications/month. Sufficient for small apps.',
    },
    'Microsoft.SignalRService/SignalR': {
        label: 'Azure SignalR Service',
        freeSku: 'Free',
        freeSkuLabel: 'Free (20 concurrent connections)',
        paidSkus: ['Standard'],
        note: 'Free tier: 20 concurrent connections, 20,000 messages/day. Upgrade only if limits are hit.',
    },
    // ---------- Monitoring ----------
    'microsoft.insights/components': {
        label: 'Application Insights',
        freeSku: null,
        freeSkuLabel: 'Effectively free up to 5 GB/month data ingestion',
        paidSkus: ['pergb2018'],
        note: 'First 5 GB/month free. Ensure sampling is enabled to stay under limit. Set daily cap to avoid surprise charges.',
    },
    'Microsoft.OperationalInsights/workspaces': {
        label: 'Log Analytics Workspace',
        freeSku: 'Free',
        freeSkuLabel: 'Free (500 MB/day, 7-day retention)',
        paidSkus: ['PerGB2018', 'Standard', 'Premium'],
        note: 'Free SKU provides 500 MB ingestion/day with 7-day retention. If more retention is needed, use PerGB2018 with data caps.',
    },
    // ---------- Networking ----------
    'Microsoft.Network/publicIPAddresses': {
        label: 'Public IP Address',
        freeSku: null,
        freeSkuLabel: 'First 5 public IPs free (Basic static)',
        paidSkus: ['Standard'],
        note: 'Delete public IPs not attached to any resource (orphaned IPs still incur cost if Standard or reserved).',
    },
    'Microsoft.Network/virtualNetworks': {
        label: 'Virtual Network',
        freeSku: null,
        freeSkuLabel: 'VNet creation is free; data egress is billed',
        paidSkus: [],
        note: 'Delete empty VNets. Minimize cross-region peering to reduce egress costs.',
    },
    // ---------- Key Vault ----------
    'Microsoft.KeyVault/vaults': {
        label: 'Key Vault',
        freeSku: null,
        freeSkuLabel: 'No free tier — first 10k operations/month ~$0.03',
        paidSkus: ['standard', 'premium'],
        note: 'Standard vault is cost-effective for secrets. Consolidate multiple vaults when possible.',
    },
    // ---------- Service Bus / Event Hub ----------
    'Microsoft.ServiceBus/namespaces': {
        label: 'Service Bus Namespace',
        freeSku: null,
        freeSkuLabel: 'No free tier — Basic tier ~$0.05/million operations',
        paidSkus: ['Basic', 'Standard', 'Premium'],
        note: 'Use Basic tier for simple queues. Downgrade from Standard/Premium if advanced features (topics, sessions) are not used.',
    },
    'Microsoft.EventHub/namespaces': {
        label: 'Event Hubs Namespace',
        freeSku: null,
        freeSkuLabel: 'No free tier — Basic from ~$9.28/month',
        paidSkus: ['Basic', 'Standard', 'Premium', 'Dedicated'],
        note: 'Delete namespace if no event hubs are actively receiving events.',
    },
    // ---------- CDN ----------
    'Microsoft.Cdn/profiles': {
        label: 'Azure CDN Profile',
        freeSku: null,
        freeSkuLabel: 'No free tier',
        paidSkus: ['Standard_Microsoft', 'Standard_Akamai', 'Premium_Verizon'],
        note: 'Consider using Static Web Apps built-in CDN (free) instead of a standalone CDN profile.',
    },
};

// ─── Helpers ───────────────────────────────────────────────────────────────────

async function az(command) {
    try {
        const { stdout, stderr } = await execAsync(`az ${command}`);
        if (stderr && !stderr.toLowerCase().includes('warning')) {
            console.warn('  ⚠  Azure CLI stderr:', stderr.trim().split('\n')[0]);
        }
        return JSON.parse(stdout || '[]');
    } catch (err) {
        // Return empty so callers can handle gracefully
        return null;
    }
}

function sku(resource) {
    return (
        resource.sku?.name ||
        resource.sku?.tier ||
        resource.properties?.sku?.name ||
        resource.kind ||
        null
    );
}

function skuUpper(resource) {
    const s = sku(resource);
    return s ? s.toUpperCase() : null;
}

function checkFreeTier(resource) {
    const info = FREE_TIER_MAP[resource.type] || FREE_TIER_MAP[resource.type?.toLowerCase()];
    if (!info) return { known: false };

    const currentSku = skuUpper(resource);
    const isOnFreeTier =
        info.freeSku &&
        currentSku &&
        currentSku.toUpperCase() === info.freeSku.toUpperCase();

    const isOnPaidTier =
        info.paidSkus.length > 0 &&
        currentSku &&
        info.paidSkus.map(s => s.toUpperCase()).includes(currentSku.toUpperCase());

    return {
        known: true,
        label: info.label,
        currentSku: currentSku || 'unknown',
        freeSku: info.freeSku,
        freeSkuLabel: info.freeSkuLabel,
        isOnFreeTier: !!isOnFreeTier,
        isOnPaidTier: !!isOnPaidTier,
        freeQuota: info.freeQuota || null,
        recommendation: info.note,
    };
}

function formatCurrency(val) {
    if (val == null) return 'n/a';
    return `$${Number(val).toFixed(2)}`;
}

// ─── Resource Fetching ─────────────────────────────────────────────────────────

async function getAllResources() {
    console.log('  Fetching all resources via Resource Graph...');
    const result = await az(
        `graph query -q "Resources | project id, name, type, resourceGroup, location, sku, kind, tags, properties" --output json`
    );
    if (!result) {
        console.warn('  Resource Graph unavailable — falling back to resource list...');
        return az('resource list --output json') || [];
    }
    return result.data || result || [];
}

async function getSubscriptionInfo() {
    return az('account show --output json');
}

async function getCostForLast30Days() {
    console.log('  Fetching cost data for last 30 days (Consumption API)...');
    const today = new Date();
    const start = new Date(today);
    start.setDate(today.getDate() - 30);

    const fmt = d => d.toISOString().split('T')[0];

    const result = await az(
        `consumption usage list --start-date ${fmt(start)} --end-date ${fmt(today)} --output json`
    );
    if (!result) return [];
    return Array.isArray(result) ? result : [];
}

async function getAdvisorRecommendations() {
    console.log('  Fetching Azure Advisor recommendations...');
    const result = await az(`advisor recommendation list --output json`);
    return Array.isArray(result) ? result : [];
}

async function getAppServicePlans() {
    return az('appservice plan list --output json') || [];
}

async function getContainerApps() {
    return az('containerapp list --output json') || [];
}

async function getStaticWebApps() {
    return az('staticwebapp list --output json') || [];
}

// ─── Analysis Functions ────────────────────────────────────────────────────────

/**
 * Identify unused / idle resources using Advisor + usage heuristics.
 */
function analyzeUnused(allResources, advisorRecs) {
    const unused = [];

    // 1. From Advisor — look for shutdown / right-size / delete recommendations
    const advisorUnused = advisorRecs.filter(r => {
        const cat = (r.category || '').toLowerCase();
        const impact = (r.impact || '').toLowerCase();
        return cat === 'cost' || cat === 'highavailability';
    });

    for (const rec of advisorUnused) {
        unused.push({
            source: 'Azure Advisor',
            resourceId: rec.resourceMetadata?.resourceId || rec.id,
            resourceName: rec.shortDescription?.solution || rec.shortDescription?.problem || 'Unknown',
            category: rec.category,
            impact: rec.impact,
            recommendation: rec.shortDescription?.solution || rec.problem || '',
            potentialSaving: rec.extendedProperties?.annualSavingsAmount
                ? formatCurrency(rec.extendedProperties.annualSavingsAmount / 12) + '/mo'
                : null,
        });
    }

    // 2. App Service Plans with zero apps
    const plans = allResources.filter(r => r.type === 'Microsoft.Web/serverFarms');
    for (const plan of plans) {
        const numberOfSites = plan.properties?.numberOfSites ?? plan.numberOfSites ?? null;
        if (numberOfSites === 0 || numberOfSites === '0') {
            unused.push({
                source: 'Heuristic',
                resourceId: plan.id,
                resourceName: plan.name,
                resourceType: plan.type,
                issue: 'App Service Plan has 0 hosted apps — safe to delete',
                impact: 'high',
                recommendation: `Run: az appservice plan delete --name "${plan.name}" --resource-group "${plan.resourceGroup}" --yes`,
            });
        }
    }

    // 3. Unattached public IPs
    const pips = allResources.filter(r => r.type === 'Microsoft.Network/publicIPAddresses');
    for (const pip of pips) {
        const attached = pip.properties?.ipConfiguration || pip.properties?.natGateway;
        if (!attached) {
            unused.push({
                source: 'Heuristic',
                resourceId: pip.id,
                resourceName: pip.name,
                resourceType: pip.type,
                issue: 'Public IP is not attached to any resource',
                impact: 'medium',
                recommendation: `Run: az network public-ip delete --name "${pip.name}" --resource-group "${pip.resourceGroup}"`,
            });
        }
    }

    // 4. Empty resource groups
    const resourceGroupCounts = {};
    for (const r of allResources) {
        const rg = (r.resourceGroup || '').toLowerCase();
        resourceGroupCounts[rg] = (resourceGroupCounts[rg] || 0) + 1;
    }
    const allRGs = allResources.map(r => r.resourceGroup).filter(Boolean);
    // (actual empty RGs need a separate list call — flag any with count = 0 if they appear)

    // 5. Storage accounts with no blobs / tables / queues (check via properties if available)
    const storageAccounts = allResources.filter(r => r.type === 'Microsoft.Storage/storageAccounts');
    for (const sa of storageAccounts) {
        const provisioning = sa.properties?.provisioningState;
        if (provisioning && provisioning !== 'Succeeded') {
            unused.push({
                source: 'Heuristic',
                resourceId: sa.id,
                resourceName: sa.name,
                resourceType: sa.type,
                issue: `Storage account provisioning state: ${provisioning}`,
                impact: 'low',
                recommendation: 'Verify this storage account is needed or delete it.',
            });
        }
    }

    return unused;
}

/**
 * Check every resource against the free-tier map.
 */
function analyzeFreeTiers(allResources) {
    const onFreeTier = [];
    const canUpgradeToFree = [];
    const noFreeTierAvailable = [];
    const unknown = [];

    for (const resource of allResources) {
        const check = checkFreeTier(resource);

        if (!check.known) {
            unknown.push({ name: resource.name, type: resource.type, resourceGroup: resource.resourceGroup });
            continue;
        }

        const entry = {
            name: resource.name,
            type: resource.type,
            label: check.label,
            resourceGroup: resource.resourceGroup,
            location: resource.location,
            currentSku: check.currentSku,
            freeSku: check.freeSku,
            freeSkuLabel: check.freeSkuLabel,
            freeQuota: check.freeQuota,
            recommendation: check.recommendation,
        };

        if (check.isOnFreeTier) {
            onFreeTier.push(entry);
        } else if (check.freeSku && !check.isOnFreeTier) {
            canUpgradeToFree.push({ ...entry, action: `Downgrade to free SKU: ${check.freeSku}` });
        } else if (!check.freeSku) {
            noFreeTierAvailable.push(entry);
        }
    }

    return { onFreeTier, canUpgradeToFree, noFreeTierAvailable, unknown };
}

/**
 * Build a cost summary from consumption data (best-effort).
 */
function buildCostSummary(usageRecords) {
    const byResource = {};
    let totalCost = 0;

    for (const record of usageRecords) {
        const cost = parseFloat(record.pretaxCost || record.cost || 0);
        const name = record.instanceName || record.resourceId || 'Unknown';
        byResource[name] = (byResource[name] || 0) + cost;
        totalCost += cost;
    }

    const topResources = Object.entries(byResource)
        .map(([name, cost]) => ({ name, cost: parseFloat(cost.toFixed(4)) }))
        .sort((a, b) => b.cost - a.cost)
        .slice(0, 20);

    return {
        totalCost30Days: parseFloat(totalCost.toFixed(4)),
        totalCostFormatted: formatCurrency(totalCost),
        currency: usageRecords[0]?.currency || 'USD',
        topCostDrivers: topResources,
    };
}

// ─── Report Printing ────────────────────────────────────────────────────────────

function printSection(title) {
    console.log('\n' + '═'.repeat(70));
    console.log(`  ${title}`);
    console.log('═'.repeat(70));
}

function printReport(report) {
    const { subscription, resourceSummary, freeTierAnalysis, unusedResources, costSummary, advisorHighlights } = report;

    printSection('SUBSCRIPTION');
    console.log(`  Name        : ${subscription?.name || 'n/a'}`);
    console.log(`  ID          : ${subscription?.id || 'n/a'}`);
    console.log(`  Tenant      : ${subscription?.tenantId || 'n/a'}`);
    console.log(`  State       : ${subscription?.state || 'n/a'}`);

    printSection(`RESOURCE SUMMARY  (${resourceSummary.total} total resources)`);
    for (const [type, count] of Object.entries(resourceSummary.byType).sort((a, b) => b[1] - a[1])) {
        console.log(`  ${count.toString().padStart(4)}  ${type}`);
    }

    printSection('COST SUMMARY (last 30 days)');
    if (costSummary.totalCost30Days === 0) {
        console.log('  ⚠  No consumption data returned. This can happen with free/sponsored subscriptions,');
        console.log('     or if the Consumption API is not enabled. Check Azure Cost Management in the portal.');
    } else {
        console.log(`  Total cost     : ${costSummary.totalCostFormatted} ${costSummary.currency}`);
        console.log(`\n  Top cost drivers:`);
        for (const item of costSummary.topCostDrivers) {
            console.log(`    ${formatCurrency(item.cost).padStart(10)}  ${item.name}`);
        }
    }

    printSection('FREE TIER STATUS');
    console.log(`\n  ✅  Already on free tier (${freeTierAnalysis.onFreeTier.length}):`);
    for (const r of freeTierAnalysis.onFreeTier) {
        console.log(`        ${r.name}  [${r.label}]  SKU: ${r.currentSku}`);
    }

    console.log(`\n  🔧  Can be moved to free tier (${freeTierAnalysis.canUpgradeToFree.length}):`);
    for (const r of freeTierAnalysis.canUpgradeToFree) {
        console.log(`        ${r.name}  [${r.label}]`);
        console.log(`          Current SKU : ${r.currentSku}`);
        console.log(`          Free SKU    : ${r.freeSku}  (${r.freeSkuLabel})`);
        console.log(`          Action      : ${r.action}`);
        console.log(`          Note        : ${r.recommendation}`);
    }

    console.log(`\n  ℹ️   No free tier available (${freeTierAnalysis.noFreeTierAvailable.length}):`);
    for (const r of freeTierAnalysis.noFreeTierAvailable) {
        console.log(`        ${r.name}  [${r.label}]  SKU: ${r.currentSku}`);
        if (r.freeQuota) console.log(`          Free quota: ${r.freeQuota}`);
        console.log(`          Note: ${r.recommendation}`);
    }

    printSection(`UNUSED / IDLE RESOURCES  (${unusedResources.length} flagged)`);
    if (unusedResources.length === 0) {
        console.log('  No unused resources detected.');
    }
    for (const item of unusedResources) {
        console.log(`\n  [${item.impact?.toUpperCase() || 'INFO'}]  ${item.resourceName}`);
        console.log(`    Source     : ${item.source}`);
        if (item.issue) console.log(`    Issue      : ${item.issue}`);
        if (item.recommendation) console.log(`    Action     : ${item.recommendation}`);
        if (item.potentialSaving) console.log(`    Est. saving: ${item.potentialSaving}`);
    }

    if (advisorHighlights.length > 0) {
        printSection(`AZURE ADVISOR HIGHLIGHTS  (cost + reliability)`);
        for (const rec of advisorHighlights) {
            console.log(`\n  [${(rec.impact || '').toUpperCase() || 'INFO'}]  ${rec.shortDescription?.problem || rec.shortDescription?.solution || 'See report'}`);
            console.log(`    Category : ${rec.category}`);
            if (rec.extendedProperties?.annualSavingsAmount) {
                console.log(`    Est. annual saving: ${formatCurrency(rec.extendedProperties.annualSavingsAmount)}`);
            }
        }
    }

    printSection('QUICK ACTIONS');
    if (freeTierAnalysis.canUpgradeToFree.length > 0) {
        console.log('\n  To move App Service plans to free F1 tier:');
        const plans = freeTierAnalysis.canUpgradeToFree.filter(r => r.type === 'Microsoft.Web/serverFarms');
        for (const plan of plans) {
            console.log(`    az appservice plan update --name "${plan.name}" --resource-group "${plan.resourceGroup}" --sku F1`);
        }
        console.log('\n  To move AI/Cognitive Services to F0:');
        const ai = freeTierAnalysis.canUpgradeToFree.filter(r => r.type === 'Microsoft.CognitiveServices/accounts');
        for (const svc of ai) {
            console.log(`    az cognitiveservices account update --name "${svc.name}" --resource-group "${svc.resourceGroup}" --sku F0`);
        }
    }

    const ips = unusedResources.filter(r => r.resourceType === 'Microsoft.Network/publicIPAddresses');
    if (ips.length > 0) {
        console.log('\n  To delete orphaned public IPs:');
        for (const ip of ips) {
            console.log(`    ${ip.recommendation}`);
        }
    }

    console.log('\n');
}

// ─── Main ──────────────────────────────────────────────────────────────────────

async function main() {
    console.log('\n🔍  Azure Cost & Usage Audit');
    console.log('    ' + new Date().toLocaleString());
    console.log('='.repeat(70));

    // 1. Subscription info
    console.log('\n[1/5] Loading subscription info...');
    const subscription = await getSubscriptionInfo();
    if (!subscription) {
        console.error('\n❌  Could not reach Azure CLI. Run `az login` first.\n');
        process.exit(1);
    }
    console.log(`  Subscription: ${subscription.name} (${subscription.id})`);

    // 2. All resources
    console.log('\n[2/5] Loading all resources...');
    const allResources = await getAllResources();
    console.log(`  Found ${allResources.length} resources.`);

    // 3. Cost data
    console.log('\n[3/5] Loading cost data...');
    const usageRecords = await getCostForLast30Days();
    console.log(`  Got ${usageRecords.length} usage records.`);

    // 4. Advisor recommendations
    console.log('\n[4/5] Loading Azure Advisor recommendations...');
    const advisorRecs = await getAdvisorRecommendations();
    console.log(`  Got ${advisorRecs.length} recommendations.`);

    // 5. Analysis
    console.log('\n[5/5] Analysing...');

    const byType = {};
    for (const r of allResources) {
        byType[r.type] = (byType[r.type] || 0) + 1;
    }

    const resourceSummary = { total: allResources.length, byType };
    const freeTierAnalysis = analyzeFreeTiers(allResources);
    const unusedResources = analyzeUnused(allResources, advisorRecs);
    const costSummary = buildCostSummary(usageRecords);
    const advisorHighlights = advisorRecs
        .filter(r => r.category === 'Cost' || r.impact === 'High')
        .slice(0, 15);

    const report = {
        generatedAt: new Date().toISOString(),
        subscription: {
            name: subscription.name,
            id: subscription.id,
            tenantId: subscription.tenantId,
            state: subscription.state,
        },
        resourceSummary,
        freeTierAnalysis,
        unusedResources,
        costSummary,
        advisorHighlights,
    };

    // Print to console
    printReport(report);

    // Save JSON report
    await writeFile(CONFIG.outputFile, JSON.stringify(report, null, 2));
    console.log(`\n✅  Full report saved to: ${CONFIG.outputFile}\n`);
}

main().catch(err => {
    console.error('\n❌  Audit failed:', err.message);
    process.exit(1);
});
