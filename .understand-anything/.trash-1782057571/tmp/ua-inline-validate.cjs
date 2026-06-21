const fs=require('fs');
const graphPath=process.argv[2], outputPath=process.argv[3];
const graph=JSON.parse(fs.readFileSync(graphPath,'utf8'));
const issues=[],warnings=[];
const nodeIds=new Set(),seen=new Map();
graph.nodes.forEach((n,i)=>{
  if(!n.id){issues.push(`Node[${i}] missing id`);return;}
  if(!n.type)issues.push(`Node[${i}] '${n.id}' missing type`);
  if(!n.name)issues.push(`Node[${i}] '${n.id}' missing name`);
  if(!n.summary)issues.push(`Node[${i}] '${n.id}' missing summary`);
  if(!n.tags||!n.tags.length)issues.push(`Node[${i}] '${n.id}' missing tags`);
  if(seen.has(n.id))issues.push(`Duplicate node ID '${n.id}'`);else seen.set(n.id,i);
  nodeIds.add(n.id);
});
graph.edges.forEach((e,i)=>{
  if(!nodeIds.has(e.source))issues.push(`Edge[${i}] source '${e.source}' not found`);
  if(!nodeIds.has(e.target))issues.push(`Edge[${i}] target '${e.target}' not found`);
});
const fileLevel=new Set(['file','config','document','service','pipeline','table','schema','resource','endpoint']);
const fileNodes=graph.nodes.filter(n=>fileLevel.has(n.type)).map(n=>n.id);
const assigned=new Map();
(graph.layers||[]).forEach(l=>(l.nodeIds||[]).forEach(id=>{
  if(!nodeIds.has(id))issues.push(`Layer '${l.id}' refs missing node '${id}'`);
  if(assigned.has(id))issues.push(`Node '${id}' in multiple layers`);
  assigned.set(id,l.id);
}));
fileNodes.forEach(id=>{if(!assigned.has(id))issues.push(`File node '${id}' not in any layer`);});
(graph.tour||[]).forEach((s,i)=>(s.nodeIds||[]).forEach(id=>{if(!nodeIds.has(id))issues.push(`Tour step[${i}] refs missing node '${id}'`);}));
const withEdges=new Set([...graph.edges.map(e=>e.source),...graph.edges.map(e=>e.target)]);
graph.nodes.forEach(n=>{if(!withEdges.has(n.id))warnings.push(`orphan: ${n.id}`);});
const stats={totalNodes:graph.nodes.length,totalEdges:graph.edges.length,totalLayers:(graph.layers||[]).length,tourSteps:(graph.tour||[]).length,
  nodeTypes:graph.nodes.reduce((a,n)=>{a[n.type]=(a[n.type]||0)+1;return a;},{}),
  edgeTypes:graph.edges.reduce((a,e)=>{a[e.type]=(a[e.type]||0)+1;return a;},{})};
fs.writeFileSync(outputPath,JSON.stringify({issues,warnings,stats},null,2));
console.log("ISSUES:",issues.length,"WARNINGS:",warnings.length);
console.log(JSON.stringify(stats,null,2));
if(issues.length)console.log("FIRST ISSUES:\n"+issues.slice(0,15).join("\n"));
