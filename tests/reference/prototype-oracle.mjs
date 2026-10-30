// Oracle: reprend VERBATIM compute() et les fonctions de période de
// docs/reference/budget-tenable-reference.html et affiche des valeurs de référence (JSON).
// Usage : node tests/reference/prototype-oracle.mjs
var FIXED=[{k:'immo',v:959},{k:'arkea',v:70},{k:'tel',v:92},{k:'eau',v:62},{k:'assur',v:75},{k:'abo',v:23},{k:'frais',v:22},{k:'tf',v:1432,annual:true},{k:'auto',v:396,annual:true},{k:'anth',v:23},{k:'ring',v:10},{k:'pp',v:60}];
var ALLOC=[{id:'courses',g:'var',pct:47,today:810},{id:'resto',g:'var',pct:7,today:160},{id:'carbu',g:'var',pct:5,today:70},{id:'shop',g:'var',pct:10,today:272},{id:'jeux',g:'var',pct:4,today:102},{id:'divers',g:'var',pct:3,today:58},{id:'voiture',g:'prov',pct:6,today:125},{id:'brico',g:'prov',pct:3,today:116},{id:'vac',g:'prov',pct:3,today:40}];
function sum(a){return a.reduce(function(s,x){return s+x},0)}
function mkS(over){var S={inc:{g:2050,p:750,caf:198},primes:3000,arkea:false,incPrimes:true,fixed:FIXED.map(function(x){return x.v}),pct:{},start:'2026-11',day:28,open:{},ldds:{}};ALLOC.forEach(function(a){S.pct[a.id]=a.pct});return Object.assign(S,over||{})}
function compute(S){
  var inc=S.inc.g+S.inc.p+S.inc.caf;
  var fx=FIXED.map(function(x,i){return (x.k==='arkea'&&S.arkea)?0:(x.annual?S.fixed[i]/12:S.fixed[i])});
  var fixedTotal=sum(fx);
  var fixedMonthly=sum(fx.filter(function(v,i){return !FIXED[i].annual}));
  var mandProv=fixedTotal-fixedMonthly;
  var env=inc-fixedTotal;
  var allocPct=sum(ALLOC.map(function(a){return S.pct[a.id]}));
  var savePct=100-allocPct;
  var amt={};ALLOC.forEach(function(a){amt[a.id]=env*S.pct[a.id]/100});
  var saveAmt=env*savePct/100;
  var varTotal=sum(ALLOC.filter(function(a){return a.g==='var'}).map(function(a){return amt[a.id]}));
  var provTotal=sum(ALLOC.filter(function(a){return a.g==='prov'}).map(function(a){return amt[a.id]}));
  var todayVar=sum(ALLOC.map(function(a){return a.today}));
  var todayBal=inc-fixedTotal-todayVar;
  return {inc:inc,fixedTotal:fixedTotal,fixedMonthly:fixedMonthly,mandProv:mandProv,env:env,allocPct:allocPct,savePct:savePct,amt:amt,saveAmt:saveAmt,varTotal:varTotal,provTotal:provTotal,todayVar:todayVar,todayBal:todayBal,primesM:S.primes/12};
}
function pad(n){return (n<10?'0':'')+n}
function ym(d){return d.getFullYear()+'-'+pad(d.getMonth()+1)}
function mshift(m,k){var p=m.split('-');return ym(new Date(+p[0],+p[1]-1+k,1))}
function mdiff(a,b){var p=a.split('-'),q=b.split('-');return (+q[0]-+p[0])*12+(+q[1]-+p[1])}
var MSH=['janv.','févr.','mars','avr.','mai','juin','juil.','août','sept.','oct.','nov.','déc.'];
function mk(day){
  function sstr(m){var p=m.split('-');return p[0]+'-'+p[1]+'-'+pad(day)}
  function pkey(d){var k=d.slice(0,7);return +d.slice(8,10)>=day?k:mshift(k,-1)}
  function pdate(m,i){var a=sstr(m).split('-');return new Date(+a[0],+a[1]-1,+a[2]+i)}
  function plen(m){var b=sstr(mshift(m,1)).split('-'),a=pdate(m,0);return Math.round((new Date(+b[0],+b[1]-1,+b[2])-a)/864e5)}
  function plabel(m){var a=pdate(m,0),e=pdate(m,plen(m)-1);return a.getDate()+' '+MSH[a.getMonth()]+' → '+e.getDate()+' '+MSH[e.getMonth()]+' '+e.getFullYear()}
  return {pkey:pkey,plen:plen,plabel:plabel};
}
var out={seed:compute(mkS()),arkeaSettled:compute(mkS({arkea:true}))};
var p=mk(28);
out.periods={};
['2026-10-27','2026-10-28','2026-11-27','2026-12-31','2027-01-01','2027-01-27','2027-01-28'].forEach(function(d){out.periods[d]=p.pkey(d)});
out.lengths={};['2026-02','2026-03','2026-10','2027-02','2028-02','2028-03'].forEach(function(m){out.lengths[m]=p.plen(m)});
out.labels={};['2026-10','2026-12','2028-02'].forEach(function(m){out.labels[m]=p.plabel(m)});
var p1=mk(1);out.day1={key:p1.pkey('2026-11-01'),len:p1.plen('2026-11'),label:p1.plabel('2026-11')};
console.log(JSON.stringify(out,null,1));
