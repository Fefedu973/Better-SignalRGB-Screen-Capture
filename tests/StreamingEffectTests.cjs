const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');

const effect = fs.readFileSync(path.join(__dirname, '..', 'Better-SignalRGB-Screen-Capture-Effect.html'), 'utf8');
const script = effect.match(/<script>([\s\S]*?)<\/script>/)[1];
let now = 1000, paths = 0;
const ticks = [], images = [], draws = [], glowDraws = [];
function style() {
    return { setProperty(name, value) { this[name] = value; },
        set cssText(value) {
            for (const name of ['left','top','width','height','opacity','zIndex','transform','clipPath']) this[name] = '';
            this.text = value;
            for (const declaration of value.split(';')) {
                const separator = declaration.indexOf(':'); if (separator < 0) continue;
                const name = declaration.slice(0, separator).trim().replace(/-([a-z])/g, (_, letter) => letter.toUpperCase());
                this[name] = declaration.slice(separator + 1).trim();
            }
        }, get cssText() { return this.text || ''; } };
}
class Element {
    constructor(id) { this.id = id; this.style = style(); this.children = new Map(); }
    querySelector(selector) { if (!this.children.has(selector)) this.children.set(selector, new Element(selector)); return this.children.get(selector); }
    setAttribute(name, value) { this[name] = value; }
    getContext() { return { clearRect() {}, save() {}, restore() {}, clip() {}, translate() {}, rotate() {}, scale() {},
        drawImage: image => {
            this.content = image.src || image.content || image.id;
            if (this.id === 'canvas') draws.push(this.content);
            else if (this.id === 'ambiCanvas') glowDraws.push(image.id || this.content);
        } }; }
}
class FakeImage {
    constructor() { images.push(this); this.naturalWidth = 0; this.naturalHeight = 0; }
    set src(value) { this.url = value; }
    get src() { return this.url; }
    finish(success = true) { this.naturalWidth = success ? 2 : 0; this.naturalHeight = success ? 2 : 0; (success ? this.onload : this.onerror)?.(); }
}
const elements = Object.fromEntries(['canvas','screenContainer','ambiCanvas','ambilight','fullscreenAmbilight'].map(id => [id, new Element(id)]));
const sandbox = { document:{ getElementById:id => elements[id], createElement:tag => new Element(`offscreen-${tag}`) },
    requestAnimationFrame:callback => ticks.push(callback), Image:FakeImage,
    Path2D:class { constructor() { paths++; } rect() {} moveTo() {} lineTo() {} closePath() {} }, Date:{ now:() => now }, console };
sandbox.window = sandbox; vm.createContext(sandbox); vm.runInContext(script, sandbox);
const sender = 'BetterSignalRGBScreenCapture', id = '11111111-2222-3333-4444-555555555555';
const outer = 'position:absolute;left:10px;top:20px;width:120px;height:80px;opacity:0.5;z-index:1;transform:rotate(45deg) scale(-1,1)';
const crop = 'clip-path:polygon(10% 20%,90% 20%,90% 80%,10% 80%)';
function send(event, encoded = false) { sandbox.onCanvasApiEvent({ sender, event:encoded ? encodeURIComponent(event) : event }); }
function header(sourceId = id, count = 2, style = outer) { send(`header:${sourceId}.jpg:image/jpeg:${count}:outer:${style}|crop:${crop}`); }
function frame(encoded = 'AAAA', sourceId = id) { header(sourceId, 1); send(`data:${sourceId}:0:${encoded}`); send(`end:${sourceId}`); }
function step(elapsed = 40) { now += elapsed; assert.equal(ticks.length, 1, 'There is one animation loop'); ticks.shift()(); }
function config(value, encoded = false) { send(`config:${JSON.stringify({ version:1, enabled:true, ...value })}`, encoded); }

assert.equal(elements.canvas.width, 320); assert.equal(elements.ambiCanvas.height, 200);
send('%invalid'); header(id, 2049); send(`data:${id}:0:AAAA`); send(`end:${id}`);
assert.equal(images.length, 0, 'Excessive chunk counts cannot start decoding');
header(); send(`data:${id}:0:AA`); send(`data:${id}:999:AA`); send(`end:${id}`);
assert.equal(images.length, 0, 'Incomplete/out-of-range chunks cannot start decoding');
send(`data:${id}:1:AA`); send(`end:${id}`);
assert.equal(images.length, 1, 'Raw percentage CSS header is accepted');
step(); assert.equal(draws.length, 0, 'Images are only rendered after successful decode');
images[0].finish(); step(); assert.equal(draws.at(-1), 'data:image/jpeg;base64,AAAA');
const parsedPaths = paths, initialDraws = draws.length;
step(); assert.equal(draws.length, initialDraws, 'An unchanged scene does not redraw');
frame(); step(); assert.equal(images.length, 1, 'Identical frames reuse the decoded image');
assert.equal(paths, parsedPaths, 'Identical geometry reuses its parsed clip paths');

frame('BBBB'); frame('CCCC'); frame('DDDD');
assert.equal(images.length, 2, 'Only one decoder per source runs while newer frames arrive');
images[1].finish();
assert.equal(images.length, 3); assert.equal(images[2].src, 'data:image/jpeg;base64,DDDD', 'The pending slot keeps only the latest completed frame');
images[2].finish(); step(); assert.equal(draws.at(-1), 'data:image/jpeg;base64,DDDD');
assert.equal(images[0].src, '', 'Replacing an image releases its previous encoded data');
frame('EEEE'); images.at(-1).finish(false); step();
assert.equal(draws.at(-1), 'data:image/jpeg;base64,DDDD', 'A failed decode preserves the last valid frame');

config({ pictureMode:'Mono', blur:true, ambilight:true, ambilightFullscreen:true, hideSources:true }); step();
assert.match(elements.canvas.style.filter, /grayscale\(1\).*blur\(1px\)/, 'Blur preserves picture mode');
assert.match(elements.ambiCanvas.style.filter, /grayscale\(1\).*blur\(1px\)/, 'Fullscreen glow receives the same picture controls');
assert.equal(elements.screenContainer.style.visibility, 'hidden'); assert.equal(glowDraws.at(-1), 'canvas', 'Glow copies the composed scene, never transforms sources independently');
const configuredDraws = draws.length;
config({ pictureMode:'Mono', blur:true, ambilight:true, ambilightFullscreen:true, hideSources:true }); step(100);
assert.equal(draws.length, configuredDraws, 'Repeated configuration heartbeats do not invalidate the scene');
config({ version:99, pictureMode:'Vivid' }); assert.match(elements.canvas.style.filter, /grayscale/);
config({ hue:999, brightness:-999, saturation:999, ambilightIntensity:999, frameRate:999 }, true);
assert.match(elements.canvas.style.filter, /hue-rotate\(180deg\) brightness\(0%\) saturate\(200%\)/);
assert.equal(elements.fullscreenAmbilight.querySelector('feFuncR').slope, 2);
assert.equal(elements.fullscreenAmbilight.querySelector('feMorphology').radius, 10, 'Fullscreen glow also honors spread');
sandbox.picture_mode = 'Cinema'; sandbox.onpicture_modeChanged(); assert.doesNotMatch(elements.canvas.style.filter, /sepia/, 'App control leaves host globals separate');
config({ enabled:false }); assert.match(elements.canvas.style.filter, /sepia/, 'Releasing app control restores host values');

config({ frameRate:1 }); step(1100); const capped = draws.length;
frame('FFFF'); images.at(-1).finish(); step(100); assert.equal(draws.length, capped, 'Raster updates honor the configured frame-rate cap');
step(950); assert.equal(draws.length, capped + 1);
frame('GGGG'); const removedImage = images.at(-1), staleCallback = removedImage.onload;
send(`remove:${id.toUpperCase()}`); removedImage.naturalWidth = 2; staleCallback(); step(1100);
assert.equal(removedImage.src, '', 'Removal releases in-flight decoding data');
assert.equal(draws.length, capped + 1, 'A late image callback cannot resurrect a removed source');

const beforeExpired = images.length;
header(); now += 6000; step(); send(`data:${id}:0:AA`); send(`data:${id}:1:AA`); send(`end:${id}`);
assert.equal(images.length, beforeExpired, 'Expired partial assemblies are discarded');
send('reset');
for (let i = 0; i < 129; i++) header(`00000000-0000-0000-0000-${i.toString().padStart(12,'0')}`, 1);
send('data:00000000-0000-0000-0000-000000000128:0:AAAA'); send('end:00000000-0000-0000-0000-000000000128');
assert.equal(images.length, beforeExpired, 'Unknown sources cannot exceed the configured source bound');
send('reset'); frame('HHHH'); images.at(-1).finish(); step(1100); const paused = draws.length;
now += 6000; step(); assert.equal(draws.length, paused, 'A completed paused source remains visible without redundant work');
send('reset'); step(1100); assert.equal(images.at(-1).src, '', 'Reset releases displayed frames as well as assemblies');
const sourceId = index => `00000000-0000-0000-0000-${index.toString().padStart(12,'0')}`;
const beforeQueue = images.length;
frame('AAAA', sourceId(1)); frame('BBBB', sourceId(2)); frame('CCCC', sourceId(3)); frame('DDDD', sourceId(3));
assert.equal(images.length, beforeQueue + 2, 'Global decoding concurrency is limited to two images');
images[beforeQueue].finish();
assert.equal(images.at(-1).src, 'data:image/jpeg;base64,DDDD', 'A queued source decodes its latest frame when a global slot becomes available');
send('reset');
frame('AAAA'); const stalled = images.at(-1), stalledCallback = stalled.onload;
frame('BBBB'); now += 6000; step();
assert.equal(stalled.src, ''); assert.equal(images.at(-1).src, 'data:image/jpeg;base64,BBBB', 'A timed-out decoder releases its slot for the latest frame');
stalledCallback(); images.at(-1).finish(); step(1100);
assert.equal(draws.at(-1), 'data:image/jpeg;base64,BBBB', 'Late timeout callbacks do not overwrite newer frames');
send('reset');
const beforeOversize = images.length;
frame('A'.repeat(12 * 1024 * 1024 + 4));
assert.equal(images.length, beforeOversize, 'The per-frame encoded size bound is enforced');
const large = 'A'.repeat(8 * 1024 * 1024);
const globalBudgetStart = images.length;
for (let index = 0; index < 4; index++) frame(large, sourceId(index));
frame('BBBB', sourceId(4));
for (let index = globalBudgetStart; index < images.length; index++) images[index].finish();
assert.equal(images.length, globalBudgetStart + 4, 'The global encoded budget rejects additional frames while earlier images retain their data');
frame('ZZZZ', sourceId(0));
assert.equal(images.at(-1).src, 'data:image/jpeg;base64,ZZZZ', 'A full encoded budget still allows existing sources to receive replacements');
assert.equal(images[globalBudgetStart].src, '', 'Pressure detaches old pixels from their retained data URL');
images.at(-1).finish(false); step(1100);
assert.ok(draws.slice(-4).every(value => value === `data:image/jpeg;base64,${large}`), 'An invalid replacement retains the previous visible pixels after encoded data is released');
frame('YYYY', sourceId(0)); images.at(-1).finish(); step(1100);
assert.ok(draws.slice(-4).includes('data:image/jpeg;base64,YYYY'), 'A subsequent valid frame recovers without remove/reset');
const beforeClear = images.length;
send('reset'); assert.equal(images.length, beforeClear, 'Reset does not begin decoding discarded queued sources');
frame('CCCC', sourceId(4)); assert.equal(images.at(-1).src, 'data:image/jpeg;base64,CCCC', 'Reset releases the global encoded budget for new frames');
send('reset');
const largeDecoded = [];
for (let index = 0; index < 9; index++) {
    frame('AAAA', sourceId(index)); const img = images.at(-1); largeDecoded.push(img);
    img.naturalWidth = 1920; img.naturalHeight = 1080; img.onload();
}
assert.ok(largeDecoded.slice(0, 8).every(image => image.src), 'Decoded sources fit within the global pixel budget');
assert.equal(largeDecoded[8].src, '', 'Decoded pixel storage is bounded independently of JPEG compression');
send(`remove:${sourceId(0)}`); frame('BBBB', sourceId(8));
const recovered = images.at(-1); recovered.naturalWidth = 1920; recovered.naturalHeight = 1080; recovered.onload();
assert.ok(recovered.src, 'Removing a source releases its decoded pixel budget');
send('reset');
header(id, 1); send(`header:${id}.jpg:image/jpeg:1:outer:${outer}|crop:clip-path:polygon(1e-12% -3.061617E-15%,100% 0%,100% 100%,0% 100%)`);
const beforeExponent = images.length;
send(`data:${id}:0:AAAA`); send(`end:${id}`);
assert.equal(images.length, beforeExponent + 1, 'Scientific notation and very small signed CSS percentages are accepted');
images.at(-1).finish(); send('reset');
const pageScript = fs.readFileSync(path.join(__dirname, '..', 'Better-SignalRGB-Screen-Capture', 'Services', 'WebOutput', 'StreamingCanvasPage.js'), 'utf8');
new vm.Script(pageScript);
console.log('PASS: effect protocol, decode backpressure, source bounds, cached paths, shared scene, filter composition, app overrides, frame-rate cap, failure/removal and HTTP script syntax.');
