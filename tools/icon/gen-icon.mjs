import { Resvg } from '@resvg/resvg-js';
import pngToIco from 'png-to-ico';
import { writeFileSync } from 'node:fs';

const N = 32;
const C = {
  bg:'#214a4d', bgLo:'#1a3e42',
  out:'#221913',
  capR:'#d8452f', capHi:'#f06b49', capLo:'#a8301f',
  spot:'#f4e8ca',
  body:'#ecd7a4', bodyLo:'#cdb074',
  door:'#6e4326', glow:'#ffd76a', glowLo:'#f3a93c', frame:'#5e3a22',
  grass:'#46a05f', grassLo:'#2f7547',
};
const HOUSE = new Set([C.capR,C.capHi,C.capLo,C.spot,C.body,C.bodyLo,C.door,C.glow,C.glowLo,C.frame]);

const g = Array.from({length:N}, () => Array(N).fill(C.bg));
const set=(x,y,c)=>{ x=Math.round(x); y=Math.round(y); if(x>=0&&x<N&&y>=0&&y<N) g[y][x]=c; };
const inEll=(x,y,cx,cy,rx,ry)=> ((x-cx)/rx)**2 + ((y-cy)/ry)**2 <= 1;

// 1. sky split
for(let y=0;y<N;y++) for(let x=0;x<N;x++) g[y][x] = y<17? C.bg : C.bgLo;

// 2. grassy mound
for(let y=0;y<N;y++) for(let x=0;x<N;x++) if(inEll(x,y,16,28.6,14,3.6)) set(x,y, y>=29? C.grassLo : C.grass);

// 3. body — stout rounded barrel
for(let y=15;y<=27;y++) for(let x=10;x<=21;x++){
  if(!inEll(x,y,15.5,21,6.2,7.2)) continue;       // barrel silhouette
  set(x,y, x>=18? C.bodyLo : C.body);              // light from the left
}

// 4. round glowing window (above the door)
for(let y=16;y<=20;y++) for(let x=13;x<=18;x++){
  if(inEll(x,y,15.5,18,2.7,2.7)) set(x,y, inEll(x,y,15.5,18,1.7,1.7)? C.glow : C.glowLo);
}
set(15,18,C.frame); set(16,18,C.frame); set(15.5,16.5,C.frame); set(15.5,19.5,C.frame); // mullion cross-ish

// 5. arched door
for(let y=22;y<=27;y++) for(let x=14;x<=17;x++){
  if(y===22 && (x===14||x===17)) continue;         // round the arch top
  set(x,y,C.door);
}
set(16,24,C.glow);                                  // tiny knob

// 6. mushroom cap — wide overhanging dome
for(let y=4;y<=15;y++) for(let x=3;x<=28;x++){
  const dome = inEll(x,y,15.5,15,12.6,11) && y<=15;
  if(!dome) continue;
  let c = C.capR;
  if(y>=14) c = C.capLo;                            // underside rim
  else if(y<=9 && x<=15) c = C.capHi;               // top-left sheen
  set(x,y,c);
}
// cap spots — clean square pixel dots, only painted over red cap
const spot=(cx,cy,sz)=>{ for(let y=cy;y<cy+sz;y++) for(let x=cx;x<cx+sz;x++){ const cur=g[y]?.[x]; if(cur===C.capR||cur===C.capHi) set(x,y,C.spot); } };
spot(17,6,3); spot(23,10,2); spot(10,10,2); spot(13,12,2); spot(20,13,2); spot(25,13,2); spot(12,7,2);

// 7. 1px dark outline around the whole house silhouette
const snap = g.map(r=>r.slice());
for(let y=0;y<N;y++) for(let x=0;x<N;x++){
  if(HOUSE.has(snap[y][x])) continue;
  const nb=[[1,0],[-1,0],[0,1],[0,-1]].some(([dx,dy])=> HOUSE.has(snap[y+dy]?.[x+dx]));
  if(nb) g[y][x]=C.out;
}

// --- emit pixel-art SVG (one rect per cell, crisp edges) ----------------------
let rects='';
for(let y=0;y<N;y++) for(let x=0;x<N;x++) rects += `<rect x="${x}" y="${y}" width="1" height="1" fill="${g[y][x]}"/>`;
const svg = `<svg width="1024" height="1024" viewBox="0 0 ${N} ${N}" shape-rendering="crispEdges" xmlns="http://www.w3.org/2000/svg">${rects}</svg>`;
writeFileSync('C:/Claude/Cloud/Sporeholm/icon.svg', svg);

// --- render PNG / ICO / ICNS --------------------------------------------------
const buf = Buffer.from(svg);
const render = s => new Resvg(buf, { fitTo:{mode:'width',value:s} }).render().asPng();
for(const s of [16,24,32,48,64,128,256,512,1024]) writeFileSync(`icon_${s}.png`, render(s));
writeFileSync('icon.png', render(1024));
writeFileSync('icon.ico', await pngToIco([16,24,32,48,64,128,256].map(render)));
const types={ic07:128,ic08:256,ic09:512,ic10:1024};
const ent=Object.entries(types).map(([t,s])=>{const p=render(s);const h=Buffer.alloc(8);h.write(t,0,'ascii');h.writeUInt32BE(p.length+8,4);return Buffer.concat([h,p]);});
const bodyB=Buffer.concat(ent);const hd=Buffer.alloc(8);hd.write('icns',0,'ascii');hd.writeUInt32BE(bodyB.length+8,4);
writeFileSync('icon.icns',Buffer.concat([hd,bodyB]));
console.log('pixel icon generated');
