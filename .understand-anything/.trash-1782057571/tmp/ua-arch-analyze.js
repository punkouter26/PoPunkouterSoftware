#!/usr/bin/env node
'use strict';
const fs = require('fs');

function main() {
  const inPath = process.argv[2];
  const outPath = process.argv[3];
  if (!inPath || !outPath) { console.error('usage: analyze.js <in> <out>'); process.exit(1); }
  const data = JSON.parse(fs.readFileSync(inPath, 'utf8'));
  const fileNodes = data.fileNodes || [];
  const importEdges = data.importEdges || [];
  const allEdges = data.allEdges || [];

  const idToNode = {};
  fileNodes.forEach(n => { idToNode[n.id] = n; });

  // --- common prefix of file paths ---
  const paths = fileNodes.map(n => n.filePath || '');
  function commonPrefixDir(paths) {
    if (paths.length === 0) return '';
    const split = paths.map(p => p.split('/'));
    let prefix = [];
    for (let i = 0; ; i++) {
      const seg = split[0][i];
      if (seg === undefined) break;
      if (split.every(s => s[i] === seg) && split.every(s => s.length > i + 1)) {
        prefix.push(seg);
      } else break;
    }
    return prefix.length ? prefix.join('/') + '/' : '';
  }
  const prefix = commonPrefixDir(paths);

  function groupOf(fp) {
    let rest = fp;
    if (prefix && rest.startsWith(prefix)) rest = rest.slice(prefix.length);
    const parts = rest.split('/');
    if (parts.length <= 1) return '(root)';
    return parts[0];
  }

  // A. Directory grouping
  const directoryGroups = {};
  fileNodes.forEach(n => {
    const g = groupOf(n.filePath || '');
    (directoryGroups[g] = directoryGroups[g] || []).push(n.id);
  });

  // B. Node type grouping
  const nodeTypeGroups = {};
  fileNodes.forEach(n => {
    (nodeTypeGroups[n.type] = nodeTypeGroups[n.type] || []).push(n.id);
  });

  // map id -> group
  const idToGroup = {};
  Object.keys(directoryGroups).forEach(g => directoryGroups[g].forEach(id => idToGroup[id] = g));

  // C. fan-in/out (import-like edges)
  const importLike = new Set(['imports', 'depends_on', 'calls']);
  const fileFanIn = {}, fileFanOut = {};
  fileNodes.forEach(n => { fileFanIn[n.id] = 0; fileFanOut[n.id] = 0; });
  importEdges.forEach(e => {
    if (fileFanOut[e.source] !== undefined) fileFanOut[e.source]++;
    if (fileFanIn[e.target] !== undefined) fileFanIn[e.target]++;
  });

  // D. cross-category edges (by node type) using allEdges
  const ccMap = {};
  allEdges.forEach(e => {
    const s = idToNode[e.source], t = idToNode[e.target];
    if (!s || !t) return;
    if (s.type === t.type) return;
    const key = s.type + '|' + t.type + '|' + e.type;
    ccMap[key] = (ccMap[key] || 0) + 1;
  });
  const crossCategoryEdges = Object.keys(ccMap).map(k => {
    const [fromType, toType, edgeType] = k.split('|');
    return { fromType, toType, edgeType, count: ccMap[k] };
  }).sort((a, b) => b.count - a.count);

  // E. inter-group import frequency (import-like)
  const igMap = {};
  importEdges.forEach(e => {
    const fg = idToGroup[e.source], tg = idToGroup[e.target];
    if (!fg || !tg || fg === tg) return;
    const key = fg + '|' + tg;
    igMap[key] = (igMap[key] || 0) + 1;
  });
  const interGroupImports = Object.keys(igMap).map(k => {
    const [from, to] = k.split('|');
    return { from, to, count: igMap[k] };
  }).sort((a, b) => b.count - a.count);

  // F. intra-group density (import-like)
  const intraGroupDensity = {};
  Object.keys(directoryGroups).forEach(g => { intraGroupDensity[g] = { internalEdges: 0, totalEdges: 0, density: 0 }; });
  importEdges.forEach(e => {
    const fg = idToGroup[e.source], tg = idToGroup[e.target];
    if (fg) intraGroupDensity[fg].totalEdges++;
    if (tg && tg !== fg) intraGroupDensity[tg].totalEdges++;
    if (fg && fg === tg) { intraGroupDensity[fg].internalEdges++; }
  });
  Object.keys(intraGroupDensity).forEach(g => {
    const d = intraGroupDensity[g];
    d.density = d.totalEdges ? +(d.internalEdges / d.totalEdges).toFixed(3) : 0;
  });

  // G. pattern matching
  const dirPatterns = [
    [/^(routes|api|controllers|endpoints|handlers|controller|routers|blueprints|serializers)$/i, 'api'],
    [/^(services|core|lib|domain|logic|composables|signals|mailers|jobs|channels|internal)$/i, 'service'],
    [/^(models|db|data|persistence|repository|entities|migrations|entity|sql|database)$/i, 'data'],
    [/^(components|views|pages|ui|layouts|screens)$/i, 'ui'],
    [/^(middleware|plugins|interceptors|guards)$/i, 'middleware'],
    [/^(utils|helpers|common|shared|tools|templatetags|pkg)$/i, 'utility'],
    [/^(config|constants|env|settings|management|commands)$/i, 'config'],
    [/^(__tests__|test|tests|spec|specs)$/i, 'test'],
    [/^(types|interfaces|schemas|contracts|dtos|dto|request|response)$/i, 'types'],
    [/^hooks$/i, 'hooks'],
    [/^(store|state|reducers|actions|slices)$/i, 'state'],
    [/^(assets|static|public)$/i, 'assets'],
    [/^(cmd|bin)$/i, 'entry'],
    [/^(docs|documentation|wiki)$/i, 'documentation'],
    [/^(deploy|deployment|infra|infrastructure|docker|k8s|kubernetes|helm|charts|terraform|tf)$/i, 'infrastructure'],
    [/^(\.github|\.gitlab|\.circleci)$/i, 'ci-cd'],
  ];
  function dirPattern(name) {
    for (const [re, label] of dirPatterns) if (re.test(name)) return label;
    return null;
  }
  function filePattern(fp, name) {
    const b = name.toLowerCase();
    if (/(\.test\.|\.spec\.)/i.test(b) || /^test_.*\.py$/i.test(b) || /_test\.go$/i.test(b) || /test\.java$/i.test(b) || /_spec\.rb$/i.test(b) || /test\.php$/i.test(b) || /tests\.cs$/i.test(b)) return 'test';
    if (/\.d\.ts$/i.test(b)) return 'types';
    if (/\.(graphql|gql|proto)$/i.test(b)) return 'types';
    if (/\.sql$/i.test(b)) return 'data';
    if (/\.(md|rst)$/i.test(b)) return 'documentation';
    if (/^dockerfile/i.test(b) || /^docker-compose/i.test(b)) return 'infrastructure';
    if (/\.(tf|tfvars)$/i.test(b)) return 'infrastructure';
    if (/^makefile$/i.test(b)) return 'infrastructure';
    if (/\.(yml|yaml)$/i.test(b) && /\.github\/workflows\//i.test(fp)) return 'ci-cd';
    if (/^(jenkinsfile|\.gitlab-ci\.yml)$/i.test(b)) return 'ci-cd';
    if (/(cargo\.toml|go\.mod|gemfile|pom\.xml|build\.gradle|composer\.json|\.csproj|\.sln|\.props|\.targets)$/i.test(b)) return 'config';
    return null;
  }
  const patternMatches = {};
  Object.keys(directoryGroups).forEach(g => {
    const p = dirPattern(g);
    if (p) patternMatches[g] = p;
  });

  // H. deployment topology
  const infraFiles = [];
  let hasDockerfile = false, hasCompose = false, hasK8s = false, hasTerraform = false, hasCI = false;
  fileNodes.forEach(n => {
    const fp = n.filePath || '', nm = (n.name || '').toLowerCase();
    if (/^dockerfile/i.test(nm)) { hasDockerfile = true; infraFiles.push(fp); }
    else if (/^docker-compose/i.test(nm)) { hasCompose = true; infraFiles.push(fp); }
    else if (/\.(tf|tfvars)$/i.test(nm)) { hasTerraform = true; infraFiles.push(fp); }
    else if (/(deployment|service|ingress)\.ya?ml$/i.test(nm) && /(k8s|kubernetes|helm|charts)/i.test(fp)) { hasK8s = true; infraFiles.push(fp); }
    else if (/\.github\/workflows\//i.test(fp) || /(jenkinsfile|gitlab-ci)/i.test(nm)) { hasCI = true; infraFiles.push(fp); }
    else if (/(^|\/)(infra|bicep|main\.bicep)/i.test(fp) || /\.bicep$/i.test(nm)) { infraFiles.push(fp); }
  });
  const deploymentTopology = { hasDockerfile, hasCompose, hasK8s, hasTerraform, hasCI, infraFiles: Array.from(new Set(infraFiles)) };

  // I. data pipeline
  const dataPipeline = { schemaFiles: [], migrationFiles: [], dataModelFiles: [], apiHandlerFiles: [] };
  fileNodes.forEach(n => {
    const fp = n.filePath || '', nm = (n.name || '').toLowerCase(), tags = (n.tags || []).join(' ').toLowerCase();
    if (/\.(sql|graphql|gql|proto|prisma)$/i.test(nm)) dataPipeline.schemaFiles.push(fp);
    if (/migration/i.test(fp)) dataPipeline.migrationFiles.push(fp);
    if (/(model|entity|dto|contract)/i.test(fp) || /model|entity/i.test(tags)) dataPipeline.dataModelFiles.push(fp);
    if (/(endpoint|controller|route|handler|api)/i.test(fp) || /api-handler|endpoint/i.test(tags)) dataPipeline.apiHandlerFiles.push(fp);
  });

  // J. doc coverage
  const docFilesByGroup = {};
  fileNodes.forEach(n => {
    if (n.type === 'document' || /\.(md|rst)$/i.test(n.name || '')) {
      const g = idToGroup[n.id];
      docFilesByGroup[g] = true;
    }
  });
  const allGroups = Object.keys(directoryGroups);
  const groupsWithDocs = Object.keys(docFilesByGroup).length;
  const undocumentedGroups = allGroups.filter(g => !docFilesByGroup[g]);
  const docCoverage = {
    groupsWithDocs,
    totalGroups: allGroups.length,
    coverageRatio: allGroups.length ? +(groupsWithDocs / allGroups.length).toFixed(2) : 0,
    undocumentedGroups
  };

  // K. dependency direction
  const pairNet = {};
  interGroupImports.forEach(({ from, to, count }) => {
    const key = [from, to].sort().join('||');
    pairNet[key] = pairNet[key] || {};
    pairNet[key][from + '>' + to] = count;
  });
  const dependencyDirection = [];
  Object.keys(pairNet).forEach(key => {
    const [a, b] = key.split('||');
    const ab = pairNet[key][a + '>' + b] || 0;
    const ba = pairNet[key][b + '>' + a] || 0;
    if (ab >= ba && ab > 0) dependencyDirection.push({ dependent: a, dependsOn: b });
    else if (ba > ab) dependencyDirection.push({ dependent: b, dependsOn: a });
  });

  // file stats
  const filesPerGroup = {};
  Object.keys(directoryGroups).forEach(g => filesPerGroup[g] = directoryGroups[g].length);
  const nodeTypeCounts = {};
  Object.keys(nodeTypeGroups).forEach(t => nodeTypeCounts[t] = nodeTypeGroups[t].length);

  const result = {
    scriptCompleted: true,
    commonPrefix: prefix,
    directoryGroups,
    nodeTypeGroups,
    crossCategoryEdges,
    interGroupImports,
    intraGroupDensity,
    patternMatches,
    deploymentTopology,
    dataPipeline,
    docCoverage,
    dependencyDirection,
    fileStats: { totalFileNodes: fileNodes.length, filesPerGroup, nodeTypeCounts },
    fileFanIn,
    fileFanOut
  };
  fs.writeFileSync(outPath, JSON.stringify(result, null, 2));
  console.log('done. groups:', allGroups.length, 'totalFileNodes:', fileNodes.length);
}
try { main(); } catch (e) { console.error(e.stack || e.message); process.exit(1); }
