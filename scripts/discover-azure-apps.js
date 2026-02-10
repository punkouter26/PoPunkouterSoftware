/**
 * Azure Apps Discovery Script
 * Discovers all web apps across Azure resource groups and verifies connectivity
 */

import { exec } from 'child_process';
import { promisify } from 'util';
import https from 'https';
import http from 'http';
import { writeFile } from 'fs/promises';
import { URL } from 'url';

const execAsync = promisify(exec);

// Configuration
const CONFIG = {
    timeout: 10000, // 10 seconds
    userAgent: 'PoPunkouterSoftware-Discovery/1.0',
    outputFile: 'azure-apps-report.json'
};

// Category keywords for inference
const CATEGORY_KEYWORDS = {
    games: ['game', 'quiz', 'tictac', 'connect', 'drop', 'race', 'ragdoll', 'reflex', 'type'],
    ai: ['ai', 'gpt', 'openai', 'translate', 'trump', 'debate', 'rap'],
    productivity: ['tracker', 'review', 'stocks', 'weight', 'remove', 'idea', 'news'],
    creative: ['image', 'redo', 'joker', 'photo']
};

/**
 * Execute Azure CLI command
 */
async function azureCommand(command) {
    try {
        console.log(`Executing: ${command}`);
        const { stdout, stderr } = await execAsync(command);
        if (stderr && !stderr.includes('WARNING')) {
            console.warn('Azure CLI warning:', stderr);
        }
        return JSON.parse(stdout || '[]');
    } catch (error) {
        console.error(`Failed to execute: ${command}`, error.message);
        return [];
    }
}

/**
 * Test HTTP/HTTPS connectivity to a URL
 */
async function testConnectivity(url) {
    return new Promise((resolve) => {
        const startTime = Date.now();
        const urlObj = new URL(url);
        const protocol = urlObj.protocol === 'https:' ? https : http;
        
        const options = {
            method: 'HEAD',
            hostname: urlObj.hostname,
            path: urlObj.pathname,
            timeout: CONFIG.timeout,
            headers: {
                'User-Agent': CONFIG.userAgent
            }
        };

        const req = protocol.request(options, (res) => {
            const responseTime = Date.now() - startTime;
            resolve({
                success: res.statusCode >= 200 && res.statusCode < 400,
                statusCode: res.statusCode,
                responseTime,
                error: null
            });
        });

        req.on('error', (error) => {
            const responseTime = Date.now() - startTime;
            resolve({
                success: false,
                statusCode: null,
                responseTime,
                error: error.message
            });
        });

        req.on('timeout', () => {
            req.destroy();
            resolve({
                success: false,
                statusCode: null,
                responseTime: CONFIG.timeout,
                error: 'Timeout'
            });
        });

        req.end();
    });
}

/**
 * Infer category from app name
 */
function inferCategory(name) {
    const nameLower = name.toLowerCase();
    for (const [category, keywords] of Object.entries(CATEGORY_KEYWORDS)) {
        if (keywords.some(keyword => nameLower.includes(keyword))) {
            return category;
        }
    }
    return 'productivity'; // default
}

/**
 * Infer description from app name and type
 */
function inferDescription(name, resourceType) {
    const baseName = name.replace(/^(app-|ca-)?po/i, '').replace(/-?(web|api|server)$/i, '');
    const category = inferCategory(name);
    
    const templates = {
        games: `Interactive ${baseName} game application`,
        ai: `AI-powered ${baseName} application`,
        productivity: `${baseName} productivity tool`,
        creative: `Creative ${baseName} application`
    };
    
    return templates[category] || `${baseName} web application`;
}

/**
 * Infer technologies from resource type
 */
function inferTechnologies(resourceType, tags = {}) {
    const techs = [];
    
    switch (resourceType) {
        case 'Microsoft.Web/sites':
            techs.push('Azure App Service', 'Blazor');
            break;
        case 'Microsoft.App/containerApps':
            techs.push('Azure Container Apps', 'Docker');
            break;
        case 'Microsoft.Web/staticSites':
            techs.push('Azure Static Web Apps', 'JavaScript');
            break;
    }
    
    // Add from tags if available
    if (tags.technologies) {
        techs.push(...tags.technologies.split(',').map(t => t.trim()));
    }
    
    return [...new Set(techs)]; // Remove duplicates
}

/**
 * Discover App Services
 */
async function discoverAppServices(resourceGroup) {
    const apps = await azureCommand(
        `az webapp list --resource-group "${resourceGroup}" --output json`
    );
    
    return apps.map(app => ({
        id: app.name.toLowerCase().replace(/[^a-z0-9]/g, ''),
        name: app.name,
        resourceGroup: resourceGroup,
        resourceType: 'Microsoft.Web/sites',
        url: `https://${app.defaultHostName}`,
        tags: app.tags || {}
    }));
}

/**
 * Discover Container Apps
 */
async function discoverContainerApps(resourceGroup) {
    const apps = await azureCommand(
        `az containerapp list --resource-group "${resourceGroup}" --output json`
    );
    
    return apps.map(app => ({
        id: app.name.toLowerCase().replace(/[^a-z0-9]/g, ''),
        name: app.name,
        resourceGroup: resourceGroup,
        resourceType: 'Microsoft.App/containerApps',
        url: app.properties?.configuration?.ingress?.fqdn 
            ? `https://${app.properties.configuration.ingress.fqdn}`
            : null,
        tags: app.tags || {}
    }));
}

/**
 * Discover Static Web Apps
 */
async function discoverStaticWebApps(resourceGroup) {
    const apps = await azureCommand(
        `az staticwebapp list --resource-group "${resourceGroup}" --output json`
    );
    
    return apps.map(app => ({
        id: app.name.toLowerCase().replace(/[^a-z0-9]/g, ''),
        name: app.name,
        resourceGroup: resourceGroup,
        resourceType: 'Microsoft.Web/staticSites',
        url: app.defaultHostname ? `https://${app.defaultHostname}` : null,
        tags: app.tags || {}
    }));
}

/**
 * Discover all apps in a resource group
 */
async function discoverResourceGroup(resourceGroup) {
    console.log(`\n🔍 Scanning resource group: ${resourceGroup}`);
    
    const [appServices, containerApps, staticWebApps] = await Promise.all([
        discoverAppServices(resourceGroup),
        discoverContainerApps(resourceGroup),
        discoverStaticWebApps(resourceGroup)
    ]);
    
    const allApps = [...appServices, ...containerApps, ...staticWebApps];
    console.log(`   Found ${allApps.length} apps (${appServices.length} App Services, ${containerApps.length} Container Apps, ${staticWebApps.length} Static Web Apps)`);
    
    return allApps;
}

/**
 * Process discovered app with metadata and connectivity test
 */
async function processApp(app) {
    if (!app.url) {
        console.log(`⚠️  ${app.name}: No URL available`);
        return {
            ...app,
            status: 'disabled',
            connectivity: { success: false, error: 'No URL configured' },
            category: app.tags.category || inferCategory(app.name),
            description: app.tags.description || inferDescription(app.name, app.resourceType),
            technologies: inferTechnologies(app.resourceType, app.tags)
        };
    }
    
    console.log(`🌐 Testing ${app.name} at ${app.url}...`);
    const connectivity = await testConnectivity(app.url);
    
    const status = connectivity.success ? 'active' : 
                   connectivity.statusCode >= 400 ? 'broken' : 'disabled';
    
    const statusIcon = connectivity.success ? '✅' : '❌';
    const statusInfo = connectivity.statusCode 
        ? `${connectivity.statusCode} (${connectivity.responseTime}ms)`
        : connectivity.error;
    
    console.log(`   ${statusIcon} ${statusInfo}`);
    
    return {
        ...app,
        status,
        connectivity,
        category: app.tags.category || inferCategory(app.name),
        description: app.tags.description || inferDescription(app.name, app.resourceType),
        technologies: inferTechnologies(app.resourceType, app.tags)
    };
}

/**
 * Generate comparison with existing apps.json
 */
async function compareWithExisting(discoveredApps) {
    try {
        const { readFile } = await import('fs/promises');
        const appsJsonPath = './PoPunkouterSoftware/wwwroot/data/apps.json';
        const existingData = JSON.parse(await readFile(appsJsonPath, 'utf-8'));
        const existingApps = existingData.apps || [];
        
        const discoveredIds = new Set(discoveredApps.map(a => a.id));
        const existingIds = new Set(existingApps.map(a => a.id));
        
        const newApps = discoveredApps.filter(a => !existingIds.has(a.id));
        const removedApps = existingApps.filter(a => !discoveredIds.has(a.id));
        const updatedApps = discoveredApps.filter(a => existingIds.has(a.id));
        
        return {
            existing: existingApps,
            new: newApps,
            removed: removedApps,
            updated: updatedApps,
            total: {
                existing: existingApps.length,
                discovered: discoveredApps.length
            }
        };
    } catch (error) {
        console.log('ℹ️  No existing apps.json found for comparison');
        return {
            existing: [],
            new: discoveredApps,
            removed: [],
            updated: [],
            total: {
                existing: 0,
                discovered: discoveredApps.length
            }
        };
    }
}

/**
 * Main execution
 */
async function main() {
    console.log('🚀 Starting Azure Apps Discovery\n');
    console.log('=' .repeat(60));
    
    // Get all resource groups
    console.log('\n📋 Fetching resource groups...');
    const resourceGroups = await azureCommand('az group list --output json');
    
    if (resourceGroups.length === 0) {
        console.error('❌ No resource groups found. Please check your Azure CLI authentication.');
        process.exit(1);
    }
    
    console.log(`   Found ${resourceGroups.length} resource groups`);
    
    // Discover all apps
    const allApps = [];
    for (const rg of resourceGroups) {
        const apps = await discoverResourceGroup(rg.name);
        allApps.push(...apps);
    }
    
    console.log('\n' + '='.repeat(60));
    console.log(`\n📊 Total apps discovered: ${allApps.length}`);
    
    if (allApps.length === 0) {
        console.log('ℹ️  No web apps found in any resource group.');
        process.exit(0);
    }
    
    // Test connectivity for all apps
    console.log('\n🔌 Testing connectivity...\n');
    const processedApps = [];
    for (const app of allApps) {
        const processed = await processApp(app);
        processedApps.push(processed);
    }
    
    // Compare with existing
    console.log('\n' + '='.repeat(60));
    console.log('\n📈 Comparison with existing apps.json:\n');
    const comparison = await compareWithExisting(processedApps);
    
    console.log(`   Existing apps: ${comparison.total.existing}`);
    console.log(`   Discovered apps: ${comparison.total.discovered}`);
    console.log(`   New apps: ${comparison.new.length}`);
    console.log(`   Removed apps: ${comparison.removed.length}`);
    console.log(`   Updated apps: ${comparison.updated.length}`);
    
    // Generate report
    const report = {
        generatedAt: new Date().toISOString(),
        summary: {
            totalDiscovered: processedApps.length,
            byStatus: {
                active: processedApps.filter(a => a.status === 'active').length,
                broken: processedApps.filter(a => a.status === 'broken').length,
                disabled: processedApps.filter(a => a.status === 'disabled').length
            },
            byResourceType: {
                appService: processedApps.filter(a => a.resourceType === 'Microsoft.Web/sites').length,
                containerApps: processedApps.filter(a => a.resourceType === 'Microsoft.App/containerApps').length,
                staticWebApps: processedApps.filter(a => a.resourceType === 'Microsoft.Web/staticSites').length
            }
        },
        comparison: {
            totalExisting: comparison.total.existing,
            new: comparison.new.map(a => ({ id: a.id, name: a.name, url: a.url })),
            removed: comparison.removed.map(a => ({ id: a.id, name: a.name })),
            updated: comparison.updated.map(a => ({ id: a.id, name: a.name, url: a.url }))
        },
        apps: processedApps.map(app => ({
            id: app.id,
            name: app.name,
            url: app.url,
            status: app.status,
            category: app.category,
            description: app.description,
            technologies: app.technologies,
            resourceGroup: app.resourceGroup,
            resourceType: app.resourceType,
            connectivity: app.connectivity,
            tags: app.tags
        }))
    };
    
    // Write report
    await writeFile(CONFIG.outputFile, JSON.stringify(report, null, 2));
    
    console.log('\n' + '='.repeat(60));
    console.log(`\n✅ Report generated: ${CONFIG.outputFile}`);
    console.log('\n📋 Summary:');
    console.log(`   Active: ${report.summary.byStatus.active} ✅`);
    console.log(`   Broken: ${report.summary.byStatus.broken} ❌`);
    console.log(`   Disabled: ${report.summary.byStatus.disabled} ⚠️`);
    
    if (comparison.new.length > 0) {
        console.log(`\n🆕 New apps to add:`);
        comparison.new.forEach(app => console.log(`   - ${app.name} (${app.url})`));
    }
    
    if (comparison.removed.length > 0) {
        console.log(`\n🗑️  Apps not found in Azure:`);
        comparison.removed.forEach(app => console.log(`   - ${app.name}`));
    }
    
    console.log('\n💡 Next step: Review the report and run `npm run update-apps` to update apps.json\n');
}

// Run
main().catch(error => {
    console.error('❌ Fatal error:', error);
    process.exit(1);
});
