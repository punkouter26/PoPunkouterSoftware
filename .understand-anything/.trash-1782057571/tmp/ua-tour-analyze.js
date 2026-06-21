#!/usr/bin/env node
"use strict";
const fs = require("fs");

function main() {
  const inPath = process.argv[2];
  const outPath = process.argv[3];
  if (!inPath || !outPath) {
    console.error("usage: node ua-tour-analyze.js <input.json> <output.json>");
    process.exit(1);
  }
  const data = JSON.parse(fs.readFileSync(inPath, "utf8"));
  const nodes = data.nodes || [];
  const edges = data.edges || [];
  const layers = data.layers || [];

  const nodeById = new Map();
  nodes.forEach(n => nodeById.set(n.id, n));

  // Fan-in / fan-out
  const fanIn = new Map();
  const fanOut = new Map();
  nodes.forEach(n => { fanIn.set(n.id, 0); fanOut.set(n.id, 0); });
  edges.forEach(e => {
    if (fanOut.has(e.source)) fanOut.set(e.source, fanOut.get(e.source) + 1);
    if (fanIn.has(e.target)) fanIn.set(e.target, fanIn.get(e.target) + 1);
  });

  const fanInRanking = [...nodeById.keys()]
    .map(id => ({ id, fanIn: fanIn.get(id), name: nodeById.get(id).name }))
    .sort((a, b) => b.fanIn - a.fanIn).slice(0, 20);
  const fanOutRanking = [...nodeById.keys()]
    .map(id => ({ id, fanOut: fanOut.get(id), name: nodeById.get(id).name }))
    .sort((a, b) => b.fanOut - a.fanOut).slice(0, 20);

  // percentile helpers
  const fanOutVals = [...fanOut.values()].sort((a, b) => a - b);
  const fanInVals = [...fanIn.values()].sort((a, b) => a - b);
  const pct = (arr, p) => arr.length ? arr[Math.min(arr.length - 1, Math.floor(arr.length * p))] : 0;
  const fanOutTop10 = pct(fanOutVals, 0.9);
  const fanInBottom25 = pct(fanInVals, 0.25);

  const codeNames = new Set(["index.ts","index.js","main.ts","main.js","app.ts","app.js",
    "server.ts","server.js","mod.rs","main.go","main.py","main.rs","manage.py","app.py",
    "wsgi.py","asgi.py","run.py","__main__.py","Application.java","Main.java","Program.cs",
    "config.ru","index.php","App.swift","Application.kt","main.cpp","main.c"]);

  function depth(fp) {
    if (!fp) return 99;
    return fp.split("/").length - 1;
  }

  const epCandidates = nodes.map(n => {
    let score = 0;
    const name = n.name || "";
    if (n.type === "document") {
      if (name === "README.md" && depth(n.filePath) === 0) score += 5;
      else if (name.endsWith(".md") && depth(n.filePath) === 0) score += 2;
    } else if (n.type === "file") {
      if (codeNames.has(name)) score += 3;
      if (depth(n.filePath) <= 1) score += 1;
      if (fanOut.get(n.id) >= fanOutTop10 && fanOutTop10 > 0) score += 1;
      if (fanIn.get(n.id) <= fanInBottom25) score += 1;
    }
    return { id: n.id, score, name, type: n.type, summary: n.summary || "", filePath: n.filePath };
  }).filter(c => c.score > 0).sort((a, b) => b.score - a.score);

  const topCandidates = epCandidates.slice(0, 5).map(c => ({ id: c.id, score: c.score, name: c.name, summary: c.summary }));

  // BFS from top CODE entry point
  const codeEntry = epCandidates.find(c => c.type === "file" && codeNames.has(c.name))
    || epCandidates.find(c => c.type === "file")
    || (nodes.find(n => n.type === "file") ? { id: nodes.find(n => n.type === "file").id } : null);
  const adj = new Map();
  nodes.forEach(n => adj.set(n.id, []));
  edges.forEach(e => {
    if ((e.type === "imports" || e.type === "calls") && adj.has(e.source)) {
      adj.get(e.source).push(e.target);
    }
  });
  const startNode = codeEntry ? codeEntry.id : null;
  const order = [];
  const depthMap = {};
  if (startNode) {
    const q = [startNode];
    depthMap[startNode] = 0;
    while (q.length) {
      const cur = q.shift();
      order.push(cur);
      (adj.get(cur) || []).forEach(t => {
        if (!(t in depthMap)) { depthMap[t] = depthMap[cur] + 1; q.push(t); }
      });
    }
  }
  const byDepth = {};
  Object.keys(depthMap).forEach(id => {
    const d = depthMap[id];
    (byDepth[d] = byDepth[d] || []).push(id);
  });

  // Non-code inventory
  function inv(pred) {
    return nodes.filter(pred).map(n => ({ id: n.id, name: n.name, type: n.type, summary: n.summary || "" }));
  }
  const nonCodeFiles = {
    documentation: inv(n => n.type === "document"),
    infrastructure: inv(n => ["service", "pipeline", "resource"].includes(n.type)),
    data: inv(n => ["table", "schema", "endpoint"].includes(n.type)),
    config: inv(n => n.type === "config")
  };

  // Clusters: bidirectional imports/calls, then expand
  const pairKey = (a, b) => [a, b].sort().join("||");
  const directed = new Set();
  edges.forEach(e => {
    if (e.type === "imports" || e.type === "calls" || e.type === "depends_on") directed.add(e.source + ">>" + e.target);
  });
  const seeds = [];
  const seenPair = new Set();
  edges.forEach(e => {
    if (!(e.type === "imports" || e.type === "calls" || e.type === "depends_on")) return;
    if (directed.has(e.target + ">>" + e.source)) {
      const k = pairKey(e.source, e.target);
      if (!seenPair.has(k)) { seenPair.add(k); seeds.push([e.source, e.target]); }
    }
  });
  // adjacency for expansion (undirected over coupling edges)
  const undAdj = new Map();
  nodes.forEach(n => undAdj.set(n.id, new Set()));
  edges.forEach(e => {
    if (e.type === "imports" || e.type === "calls" || e.type === "depends_on" || e.type === "related") {
      if (undAdj.has(e.source)) undAdj.get(e.source).add(e.target);
      if (undAdj.has(e.target)) undAdj.get(e.target).add(e.source);
    }
  });
  const clusters = [];
  seeds.forEach(pair => {
    const set = new Set(pair);
    let changed = true;
    while (changed && set.size < 5) {
      changed = false;
      for (const cand of nodeById.keys()) {
        if (set.has(cand)) continue;
        let conn = 0;
        set.forEach(m => { if (undAdj.get(cand) && undAdj.get(cand).has(m)) conn++; });
        if (conn >= 2) { set.add(cand); changed = true; break; }
      }
    }
    const arr = [...set];
    let ec = 0;
    edges.forEach(e => { if (set.has(e.source) && set.has(e.target)) ec++; });
    clusters.push({ nodes: arr, edgeCount: ec });
  });
  clusters.sort((a, b) => b.edgeCount - a.edgeCount);
  const topClusters = clusters.slice(0, 10);

  const nodeSummaryIndex = {};
  nodes.forEach(n => { nodeSummaryIndex[n.id] = { name: n.name, type: n.type, summary: n.summary || "" }; });

  const out = {
    scriptCompleted: true,
    entryPointCandidates: topCandidates,
    fanInRanking,
    fanOutRanking,
    bfsTraversal: { startNode, order, depthMap, byDepth },
    nonCodeFiles,
    clusters: topClusters,
    layers: { count: layers.length, list: layers.map(l => ({ id: l.id, name: l.name, description: l.description })) },
    nodeSummaryIndex,
    totalNodes: nodes.length,
    totalEdges: edges.length
  };
  fs.writeFileSync(outPath, JSON.stringify(out, null, 2));
  console.log("done");
}

try { main(); } catch (e) { console.error(e && e.stack ? e.stack : String(e)); process.exit(1); }
