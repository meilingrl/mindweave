// Brain sketches (prototype): one stroke when Mindweave is closed, nodes when it is open.
//   node prototypes/app-icon/brain.mjs [outDir]   writes brain.html; render it with headless Chrome
// The nodes are the places where the single line crosses itself, and the links are the pieces of line between them,
// so the two icons overlap by construction.
import { writeFileSync, mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const out = process.argv[2] ?? join(dirname(fileURLToPath(import.meta.url)), 'screenshots');
mkdirSync(out, { recursive: true });
const TAU = Math.PI * 2;

// Catmull-Rom through points.
function smooth(p, closed = false, per = 24) {
  const n = p.length, at = i => closed ? p[(i + n) % n] : p[Math.max(0, Math.min(n - 1, i))], o = [];
  for (let i = 0; i < (closed ? n : n - 1); i++) {
    const [a, b, c, d] = [at(i - 1), at(i), at(i + 1), at(i + 2)];
    for (let k = 0; k < per; k++) {
      const t = k / per, t2 = t * t, t3 = t2 * t;
      o.push([0, 1].map(j => .5 * (2 * b[j] + (c[j] - a[j]) * t + (2 * a[j] - 5 * b[j] + 4 * c[j] - d[j]) * t2 + (3 * b[j] - a[j] - 3 * c[j] + d[j]) * t3)));
    }
  }
  o.push(closed ? o[0] : p[n - 1]);
  return o;
}

// Distance from the centre to the outline in a direction.
function radius(outline, c) {
  return th => {
    const dx = Math.cos(th), dy = Math.sin(th); let best = 0;
    for (let i = 1; i < outline.length; i++) {
      const [x1, y1] = outline[i - 1], [x2, y2] = outline[i];
      const ex = x2 - x1, ey = y2 - y1, den = dx * ey - dy * ex;
      if (Math.abs(den) < 1e-9) continue;
      const t = ((x1 - c[0]) * ey - (y1 - c[1]) * ex) / den, u = ((x1 - c[0]) * dy - (y1 - c[1]) * dx) / den;
      if (t > 0 && u >= 0 && u <= 1) best = Math.max(best, t);
    }
    return best;
  };
}

// Crossings of a polyline with itself, merged when close, and the pieces between them.
function graph(line, merge, ends = true, step = 120) {
  const hits = [];
  for (let i = 1; i < line.length; i++)
    for (let j = i + 4; j < line.length; j++) {
      const [x1, y1] = line[i - 1], [x2, y2] = line[i], [x3, y3] = line[j - 1], [x4, y4] = line[j];
      const d = (x2 - x1) * (y4 - y3) - (y2 - y1) * (x4 - x3);
      if (Math.abs(d) < 1e-9) continue;
      const t = ((x3 - x1) * (y4 - y3) - (y3 - y1) * (x4 - x3)) / d, u = ((x3 - x1) * (y2 - y1) - (y3 - y1) * (x2 - x1)) / d;
      if (t >= 0 && t < 1 && u >= 0 && u < 1) hits.push({ p: [x1 + t * (x2 - x1), y1 + t * (y2 - y1)], at: [i + t, j + u] });
    }
  const nodes = [], order = [];
  const node = p => {
    let k = nodes.findIndex(n => Math.hypot(n.x - p[0], n.y - p[1]) < merge);
    if (k < 0) { nodes.push({ x: p[0], y: p[1], n: 1 }); return nodes.length - 1; }
    const n = nodes[k]; n.x = (n.x * n.n + p[0]) / (n.n + 1); n.y = (n.y * n.n + p[1]) / (n.n + 1); n.n++; return k;
  };
  for (const h of hits) { const k = node(h.p); order.push([h.at[0], k], [h.at[1], k]); }
  if (ends) { order.push([0, node(line[0])], [line.length, node(line.at(-1))]); }
  order.sort((a, b) => a[0] - b[0]);
  // A long piece without a crossing gets nodes of its own, on the line, so the shape stays.
  const len = [0]; for (let i = 1; i < line.length; i++) len.push(len[i - 1] + Math.hypot(line[i][0] - line[i - 1][0], line[i][1] - line[i - 1][1]));
  const L = t => len[Math.min(line.length - 1, Math.floor(t))], more = [];
  for (let i = 1; i < order.length; i++) {
    const a = L(order[i - 1][0]), b = L(order[i][0]), parts = Math.round((b - a) / step);
    for (let k = 1; k < parts; k++) { const want = a + (b - a) * k / parts, at = len.findIndex(v => v >= want); more.push([at, node(line[at])]); }
  }
  order.push(...more); order.sort((a, b) => a[0] - b[0]);
  const edges = new Set();
  for (let i = 1; i < order.length; i++) if (order[i][1] !== order[i - 1][1]) edges.add([order[i][1], order[i - 1][1]].sort((a, b) => a - b).join());
  const list = [...edges].map(e => e.split(',').map(Number));
  nodes.forEach((n, i) => n.deg = list.filter(e => e.includes(i)).length);
  return { nodes, edges: list };
}

const side = smooth([[150, 520], [160, 405], [232, 300], [345, 226], [480, 192], [620, 202], [745, 258], [836, 352], [872, 470], [842, 575], [778, 640], [790, 712], [728, 778], [632, 775], [566, 716], [470, 742], [350, 738], [262, 690], [242, 632], [180, 600]], true);

// A: the outline once round, then a rosette stretched into the same outline, out through the stem.
function A() {
  const c = [505, 470], rho = radius(side, c), pts = [];
  const bump = th => 1 - .035 * (1 - Math.abs(Math.sin(th * 6.5)));
  const start = 1.28;
  pts.push([560, 905], [548, 830]);
  for (let i = 0; i <= 70; i++) { const th = start + TAU * i / 70; const r = rho(th) * bump(th); pts.push([c[0] + r * Math.cos(th), c[1] + r * Math.sin(th)]); }
  const turns = 4, petals = 9, N = 150;
  for (let i = 1; i <= N; i++) {
    const t = i / N, th = start + TAU * turns * t;
    const f = .55 + .41 * Math.cos(petals * (th - start) / turns) + .07 * Math.sin(th * 1.7 + 1);
    const r = rho(th) * Math.min(.97, f);
    pts.push([c[0] + r * Math.cos(th), c[1] + r * Math.sin(th)]);
  }
  pts.push([510, 760], [496, 840], [500, 905]);
  return smooth(pts, false, 8);
}

// A ring of curls that turn inwards from the outline, like the folds of a brain.
function curls(c, rho, reach, size, n, from, N, dir = 1) {
  const pts = [];
  for (let i = 0; i <= N; i++) {
    const th = from + dir * TAU * i / N, ph = dir * n * TAU * i / N, R = rho(th) * reach;
    pts.push([c[0] + R * ((1 - size) * Math.cos(th) + size * Math.cos(th + ph)), c[1] + R * ((1 - size) * Math.sin(th) + size * Math.sin(th + ph))]);
  }
  return pts;
}

// B: messier: two loose turns round the outline, then wide uneven loops, like yarn wound by hand.
function B() {
  const c = [505, 470], rho = radius(side, c), pts = [[572, 905], [556, 825]], start = 1.28;
  for (let i = 0; i <= 120; i++) {
    const th = start + 2 * TAU * i / 120, r = rho(th) * (.955 + .045 * Math.sin(th * 3.5 + .6));
    pts.push([c[0] + r * Math.cos(th), c[1] + r * Math.sin(th)]);
  }
  const turns = 4, petals = 7, N = 130;
  for (let i = 1; i <= N; i++) {
    const t = i / N, th = start + TAU * turns * t, wob = th + .28 * Math.sin(th * 1.3 + 2);
    const r = rho(wob) * Math.min(.94, .50 + .40 * Math.cos(petals * (th - start) / turns) + .10 * Math.sin(th * 1.9 + 4));
    pts.push([c[0] + r * Math.cos(wob), c[1] + r * Math.sin(wob)]);
  }
  pts.push([508, 760], [490, 835], [506, 905]);
  return smooth(pts, false, 8);
}

// C: one ring of curls along the outline, a smaller one inside, one line with two loose ends.
function C() {
  const c = [505, 470], rho = radius(side, c);
  return smooth([[562, 905], [550, 830], ...curls(c, rho, 1, .25, 10, 1.3, 140), ...curls(c, rho, .70, .47, 4, 1.3, 70).slice(1), [505, 740], [494, 830], [500, 905]], false, 8);
}

// D: the drawing the owner chose on 2026-11-24, traced by hand from the picture they sent (1180 x 1180 units).
// One line: up the stem, the inner folds, once round the outline with a small loop at each turn.
const chosen = [[478,1075], [495,985], [550,930], [630,885], [668,830], [672,790], [652,732], [610,697], [560,688], [510,705], [485,742], [495,782], [535,800], [572,783], [585,742], [562,702], [520,675], [450,632], [370,597], [312,552], [285,482], [300,402], [350,342], [430,315], [492,335], [545,385], [600,452], [660,532], [730,588], [800,600], [868,572], [908,512], [905,440], [870,385], [815,360], [770,375], [745,420], [765,465], [820,490], [890,497], [960,485], [1008,442], [1020,382], [992,322], [932,287], [860,270], [770,270], [680,287], [600,322], [545,377], [500,442], [475,512], [486,570], [530,610], [600,632], [670,662], [720,712], [770,780], [830,820], [900,815], [960,788], [998,732], [1005,662], [985,607], [940,586], [900,606], [890,645], [915,680], [960,690], [1010,680], [1070,652], [1122,600], [1158,520], [1165,422], [1130,322], [1062,232], [962,152], [842,106], [722,95], [642,110], [588,145], [565,190], [585,235], [625,246], [655,220], [645,180], [600,155], [540,132], [470,123], [400,130], [312,160], [232,215], [187,280], [175,335], [182,382], [215,410], [254,400], [266,371], [241,346], [192,338], [132,350], [70,387], [26,452], [15,522], [36,592], [88,648], [150,680], [200,685], [234,660], [230,622], [196,608], [152,625], [112,665], [98,720], [120,770], [170,800], [230,805], [300,798], [380,808], [450,830], [495,852]];
function D() { return smooth(chosen.map(([x, y]) => [508 + (x - 590) * .72, 530 + (y - 585) * .72]), false, 8); }

const path = l => 'M' + l.map(p => p.map(v => v.toFixed(1)).join(' ')).join('L');
const tile = (x, inner) => `<g transform="translate(${x} 0)"><rect x="40" y="40" width="944" height="944" rx="220" fill="url(#bg)"/><g transform="translate(512 512) scale(.94) translate(-508 -530)">${inner}</g></g>`;
const closed = (l, w) => `<path d="${path(l)}" fill="none" stroke="#fff" stroke-width="${w}" stroke-linejoin="round" stroke-linecap="round"/>`;
const open = (g, w, r) => g.edges.map(([a, b]) => `<path d="M${g.nodes[a].x} ${g.nodes[a].y}L${g.nodes[b].x} ${g.nodes[b].y}" stroke="#BFCBFF" stroke-width="${w}" stroke-linecap="round"/>`).join('') +
  g.nodes.map(n => `<circle cx="${n.x}" cy="${n.y}" r="${r * (n.deg >= 5 ? 1.35 : 1)}" fill="#fff"/>`).join('');

let html = '<body style="margin:0;background:#fff;font:500 22px Helvetica Neue">';
for (const [name, f, w, merge, r] of [['A', A, 18, 38, 18], ['B', B, 17, 36, 17], ['C', C, 20, 36, 19], ['D', D, 20, 30, 18]]) {
  const l = f(), g = graph(l, merge);
  console.log(name, l.length, 'points', g.nodes.length, 'nodes', g.edges.length, 'links');
  const both = `<g opacity=".45">${closed(l, w)}</g>${open(g, w * .6, r)}`;
  html += `<div style="display:flex;align-items:center"><span style="width:40px;padding-left:12px">${name}</span><svg width="1110" height="370" viewBox="0 0 3072 1024"><defs><linearGradient id="bg" x1="0" y1="0" x2="0" y2="1"><stop stop-color="#6F86FF"/><stop offset="1" stop-color="#3A35C4"/></linearGradient></defs>${tile(0, closed(l, w))}${tile(1024, open(g, w * .6, r))}${tile(2048, both)}</svg></div>`;
}
writeFileSync(join(out, 'brain.html'), html + '</body>');

// The chosen drawing as files: the mark and the mark on a tile next to this script (committed),
// and one tile per icon size in screenshots/ for chosen-icon.sh. Small sizes get a bolder line.
const here = dirname(fileURLToPath(import.meta.url));
const curve = p => { // Catmull-Rom as cubic curves
  const at = i => p[Math.max(0, Math.min(p.length - 1, i))]; let d = `M${p[0].join(' ')}`;
  for (let i = 0; i < p.length - 1; i++) {
    const [a, b, c, e] = [at(i - 1), at(i), at(i + 1), at(i + 2)];
    d += ` C${[b[0] + (c[0] - a[0]) / 6, b[1] + (c[1] - a[1]) / 6, c[0] - (e[0] - b[0]) / 6, c[1] - (e[1] - b[1]) / 6].map(v => Math.round(v)).join(' ')} ${c.join(' ')}`;
  }
  return d;
};
const mark = (stroke, w, more = '', lines = [chosen]) => `<path${more} fill="none" stroke="${stroke}" stroke-width="${w}" stroke-linecap="round" stroke-linejoin="round" d="${lines.map(curve).join(' ')}"/>`;
// Below 100 px the full drawing turns to mud, so small sizes use fewer pieces of the same line:
// the outline with its three dents, the stem, and at 32 px one inner fold. These are two or three strokes, not one.
const outline = [[600, 640], [670, 662], [720, 712], [770, 780], [830, 820], [900, 815], [960, 788], [1010, 720], [1070, 652], [1122, 600], [1158, 520], [1165, 422], [1130, 322], [1062, 232], [962, 152], [842, 106], [722, 95], [640, 115], [600, 150], [540, 132], [470, 123], [400, 130], [312, 160], [232, 215], [187, 280], [178, 340], [130, 350], [70, 387], [26, 452], [15, 522], [36, 592], [88, 648], [125, 670], [105, 720], [120, 770], [170, 800], [230, 805], [300, 798], [380, 808], [450, 830], [495, 852]];
const stem = [[478, 1075], [495, 985], [550, 930], [630, 885], [668, 830], [672, 790], [652, 732], [610, 697]];
const fold = [[520, 675], [450, 632], [370, 597], [312, 552], [285, 482], [300, 402], [350, 342], [430, 315], [492, 335], [545, 385], [600, 322], [680, 287], [770, 270], [860, 270], [932, 287], [992, 322], [1020, 382], [1008, 442], [960, 485]];
const svg = (size, inner) => `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${size} ${size}">\n<title>Mindweave</title>\n${inner}\n</svg>\n`;
const INK = '#0b0b0b', YARN = '#f4f1ea';
writeFileSync(join(here, 'mindweave-logo.svg'), svg(1180, mark(INK, 27)));
writeFileSync(join(here, 'mindweave-logo-tile.svg'), svg(1180, `<rect width="1180" height="1180" rx="205" fill="${INK}"/>\n${mark(YARN, 30, ' transform="translate(118 112) scale(.8)"')}`));
for (const [name, w, lines] of [['full', 30], ['mid', 58, [outline, [...stem, ...fold]]], ['small', 92, [outline, stem]]])
  writeFileSync(join(out, `chosen-${name}.svg`), svg(1024, `<rect x="100" y="100" width="824" height="824" rx="185" fill="${INK}"/>\n${mark(YARN, w, ' transform="translate(512 512) scale(.56) translate(-590 -585)"', lines)}`));
