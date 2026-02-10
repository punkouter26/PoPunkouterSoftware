/**
 * Cleanup Script - Creates production-ready apps.json
 * Merges Azure discoveries with existing app descriptions
 * Prefers Static Web Apps over API backends
 * Removes duplicates and non-user-facing apps
 */

import { readFile, writeFile, copyFile } from 'fs/promises';

const CONFIG = {
    reportFile: 'azure-apps-report.json',
    appsJsonPath: './PoPunkouterSoftware/wwwroot/data/apps.json',
    outputFile: './PoPunkouterSoftware/wwwroot/data/apps.json',
    backupSuffix: '.backup'
};

/**
 * Extract canonical app name (remove prefixes)
 */
function getCanonicalName(name) {
    // Remove common prefixes
    return name
        .replace(/^(swa-|app-|api-|ca-)/i, '')
        .replace(/(-api|-web|-server|-app|-prod)$/i, '')
        .toLowerCase();
}

/**
 * Get user-friendly app name
 */
function getUserFriendlyName(name, canonicalName) {
    // Remove 'po' prefix if present, then convert to PascalCase with 'Po' prefix
    const cleanName = canonicalName.replace(/^po/, '');
    
    if (!cleanName) {
        // If the name was just 'po', return the original
        return name;
    }
    
    // Split by hyphens and convert to PascalCase
    const parts = cleanName.split('-').filter(p => p.length > 0);
    const pascalName = parts.map(part => 
        part.charAt(0).toUpperCase() + part.slice(1)
    ).join('');
    
    return 'Po' + pascalName;
}

/**
 * Main cleanup logic
 */
async function main() {
    console.log('🧹 Starting Cleanup Process\n');
    console.log('='.repeat(60));

    // Load Azure report
    console.log('\n📖 Loading Azure discovery report...');
    const report = JSON.parse(await readFile(CONFIG.reportFile, 'utf-8'));
    
    // Load existing apps.json
    console.log('📖 Loading existing apps.json...');
    const existingData = JSON.parse(await readFile(CONFIG.appsJsonPath, 'utf-8'));
    const existingApps = existingData.apps || [];

    // Create lookup maps
    const existingMap = new Map(existingApps.map(app => [app.id, app]));
    const canonicalMap = new Map();

    console.log('\n🔄 Processing and deduplicating apps...\n');

    // Process each discovered app
    for (const azureApp of report.apps) {
        const canonical = getCanonicalName(azureApp.name);
        
        // Skip if this is an API backend and we already have a SWA version
        if (azureApp.resourceType === 'Microsoft.Web/sites' && azureApp.name.includes('-api')) {
            const swaVersion = report.apps.find(a => 
                getCanonicalName(a.name) === canonical && 
                a.resourceType === 'Microsoft.Web/staticSites'
            );
            if (swaVersion && swaVersion.status === 'active') {
                console.log(`   ⏭️  Skipping ${azureApp.name} (prefer SWA version)`);
                continue;
            }
        }

        // Check if we already processed this canonical app
        if (canonicalMap.has(canonical)) {
            const existing = canonicalMap.get(canonical);
            
            // Prefer Static Web Apps over others
            if (azureApp.resourceType === 'Microsoft.Web/staticSites' && 
                existing.resourceType !== 'Microsoft.Web/staticSites') {
                console.log(`   🔄 Replacing ${existing.name} with ${azureApp.name} (SWA preferred)`);
                canonicalMap.set(canonical, azureApp);
            } else if (azureApp.status === 'active' && existing.status !== 'active') {
                console.log(`   🔄 Replacing ${existing.name} with ${azureApp.name} (active vs ${existing.status})`);
                canonicalMap.set(canonical, azureApp);
            } else {
                console.log(`   ⏭️  Skipping ${azureApp.name} (duplicate of ${existing.name})`);
            }
        } else {
            canonicalMap.set(canonical, azureApp);
        }
    }

    console.log(`\n✨ Deduplicated: ${report.apps.length} → ${canonicalMap.size} unique apps\n`);

    // Build final apps array
    const finalApps = [];
    const excludedApps = ['popunkoutersoftware']; // Exclude main site from apps list

    for (const [canonical, azureApp] of canonicalMap.entries()) {
        if (excludedApps.includes(canonical)) {
            console.log(`   ⏭️  Excluding ${azureApp.name} (main site)`);
            continue;
        }

        // Look for existing app data by canonical name matching
        let existingApp = existingMap.get(azureApp.id);
        if (!existingApp) {
            // Try to find by canonical name
            existingApp = existingApps.find(e => 
                getCanonicalName(e.name) === canonical ||
                e.id === canonical ||
                e.name.toLowerCase().includes(canonical)
            );
        }

        const friendlyName = getUserFriendlyName(azureApp.name, canonical);

        // Merge data
        const mergedApp = {
            id: canonical,
            name: existingApp?.name || friendlyName,
            description: existingApp?.description || azureApp.description,
            category: existingApp?.category || azureApp.category,
            status: azureApp.status, // Always use fresh status from Azure
            technologies: existingApp?.technologies || azureApp.technologies,
            url: azureApp.url
        };

        finalApps.push(mergedApp);
    }

    // Add apps from existing that weren't found in Azure (if they have good data)
    console.log('\n📋 Checking for preserved apps from existing apps.json...\n');
    const azureCanonicals = new Set(canonicalMap.keys());
    
    for (const existingApp of existingApps) {
        const canonical = getCanonicalName(existingApp.name);
        const existingId = existingApp.id;
        
        // Check if this app is already in our final list (by canonical name or ID)
        const alreadyIncluded = azureCanonicals.has(canonical) || 
                              azureCanonicals.has(existingId) ||
                              finalApps.some(a => 
                                  a.id === existingId || 
                                  getCanonicalName(a.name) === canonical
                              );
        
        if (!alreadyIncluded && !excludedApps.includes(canonical)) {
            // Keep apps that have good descriptions
            if (existingApp.description && existingApp.description.length > 20) {
                console.log(`   ✅ Preserving ${existingApp.name} (not found in Azure but has good data)`);
                finalApps.push({
                    ...existingApp,
                    status: 'disabled' // Mark as disabled since not found in Azure
                });
            } else {
                console.log(`   ❌ Skipping ${existingApp.name} (not in Azure, no good description)`);
            }
        }
    }

    // Sort by name
    finalApps.sort((a, b) => a.name.localeCompare(b.name));

    // Stats
    const stats = {
        total: finalApps.length,
        active: finalApps.filter(a => a.status === 'active').length,
        disabled: finalApps.filter(a => a.status === 'disabled').length,
        broken: finalApps.filter(a => a.status === 'broken').length
    };

    console.log('\n' + '='.repeat(60));
    console.log('\n📊 Final Statistics:\n');
    console.log(`   Total apps: ${stats.total}`);
    console.log(`   Active: ${stats.active} ✅`);
    console.log(`   Disabled: ${stats.disabled} ⚠️`);
    console.log(`   Broken: ${stats.broken} ❌`);

    console.log('\n📝 App List:\n');
    finalApps.forEach(app => {
        const statusIcon = app.status === 'active' ? '✅' : app.status === 'broken' ? '❌' : '⚠️';
        console.log(`   ${statusIcon} ${app.name} - ${app.category}`);
    });

    // Create backup
    console.log('\n' + '='.repeat(60));
    const backupPath = CONFIG.appsJsonPath + CONFIG.backupSuffix;
    console.log(`\n💾 Creating backup: ${backupPath}`);
    await copyFile(CONFIG.appsJsonPath, backupPath);

    // Write cleaned apps.json
    const cleanedData = { apps: finalApps };
    console.log(`\n✍️  Writing cleaned file: ${CONFIG.outputFile}`);
    await writeFile(CONFIG.outputFile, JSON.stringify(cleanedData, null, 2) + '\n');

    console.log('\n' + '='.repeat(60));
    console.log('\n✅ Cleanup Complete!\n');
    console.log('💡 Next steps:');
    console.log('   1. Review the updated apps.json');
    console.log('   2. Update e2e test expected count (currently 16)');
    console.log('   3. Test OurWebApps.html locally');
    console.log('   4. Commit and deploy\n');
}

main().catch(error => {
    console.error('❌ Fatal error:', error);
    process.exit(1);
});
