// Synthetic native-renderer handoff assets, rendered by the actual web output implementation.
// --update deliberately refreshes checked-in reference PNGs; a normal run only compares them.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const crypto = require('node:crypto');

const root = path.join(__dirname, '..');
const directory = path.join(__dirname, 'fixtures', 'native-rendering-v1');
const assets = path.join(root, 'Better-SignalRGB-Screen-Capture', 'Services', 'WebOutput');
const hash = bytes => crypto.createHash('sha256').update(bytes).digest('hex');

module.exports = async function run(browser, { update = false } = {}) {
    const fixtures = JSON.parse(fs.readFileSync(path.join(directory, 'cases.json'), 'utf8'));
    const page = await browser.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    // Expose only the existing mask painter inside this test's in-memory script. The
    // production page has no additional bridge or diagnostics carrying image pixels.
    const script = fs.readFileSync(path.join(assets, 'StreamingCanvasPage.js'), 'utf8')
        .replace(/\}\)\(\);\s*$/, 'window.__nativeReferenceDrawMask = drawContourMask;})();');
    const html = fs.readFileSync(path.join(assets, 'StreamingCanvasPage.html'), 'utf8')
        .replace('<!--CONTOUR_HALO_SCRIPT-->', () => `<script>${fs.readFileSync(path.join(assets, 'ContourHalo.js'), 'utf8')}</script>`)
        .replace('<!--WEB_OUTPUT_SCRIPT-->', () => `<script>${script}</script>`);
    const clients = new Set();
    let current, images;
    const part = (type, bytes, id) => Buffer.concat([Buffer.from(`--frame\r\nContent-Type: ${type}\r\nContent-Length: ${bytes.length}\r\nX-State-Version: 1\r\n${id ? `X-Source-Id: ${id}\r\n` : ''}\r\n`), bytes, Buffer.from('\r\n')]);
    const server = http.createServer((request, response) => {
        if (request.url.startsWith('/web-stream')) {
            const metadata = current.metadata;
            const active = current.sources.filter(source => metadata.sources.find(item => item.id === source.id)?.hasFrame);
            const state = { version:1, canvasWidth:320, canvasHeight:200, outputWidth:metadata.outputWidth,
                outputHeight:metadata.outputHeight, settings:metadata.effectiveSettings, sources:active };
            response.writeHead(200, { 'Content-Type':'multipart/x-mixed-replace; boundary=frame', 'Cache-Control':'no-store' });
            clients.add(response); response.on('close', () => clients.delete(response));
            response.write(part('application/json', Buffer.from(JSON.stringify(state))));
            for (const source of active) response.write(part('image/jpeg', images[current.pattern], source.id));
        } else { response.writeHead(200, { 'Content-Type':'text/html' }); response.end(html); }
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    try {
        const generated = await page.evaluate(() => {
            const canvas = document.createElement('canvas'); canvas.width = 96; canvas.height = 64;
            const ctx = canvas.getContext('2d');
            ctx.fillStyle = '#000'; ctx.fillRect(0, 0, 96, 64);
            const black = canvas.toDataURL('image/jpeg', 1).split(',')[1];
            for (const [color, x, y] of [['#ff0000',0,0],['#00ff00',48,0],['#0000ff',0,32],['#ffffff',48,32]])
            { ctx.fillStyle = color; ctx.fillRect(x, y, 48, 32); }
            // Midtones ensure contrast/saturation/brightness modes cannot all pass by
            // clipping saturated primaries to the same output values.
            ctx.fillStyle = '#597a9b'; ctx.fillRect(12, 8, 70, 10);
            for (const [index, color] of ['#101010','#404040','#808080','#c0c0c0'].entries())
            { ctx.fillStyle = color; ctx.fillRect(8 + index * 20, 48, 18, 10); }
            ctx.fillStyle = '#000'; ctx.fillRect(35, 21, 26, 22);
            return { black, quadrants:canvas.toDataURL('image/jpeg', 1).split(',')[1] };
        });
        images = {};
        for (const pattern of ['black', 'quadrants']) {
            const imagePath = path.join(directory, `source-${pattern}.jpg`);
            if (update) fs.writeFileSync(imagePath, Buffer.from(generated[pattern], 'base64'));
            images[pattern] = fs.readFileSync(imagePath);
        }
        const references = [];
        for (const fixture of fixtures.cases) {
            current = fixture;
            const { outputWidth:width, outputHeight:height } = fixture.metadata;
            await page.setViewportSize({ width, height });
            await page.goto(`http://127.0.0.1:${server.address().port}/${fixture.name}`);
            await page.waitForFunction(() => window.webOutputDiagnostics?.renderCount > 0 &&
                window.webOutputDiagnostics.activeDecoders === 0 && window.webOutputDiagnostics.pendingCount === 0);
            await page.waitForTimeout(120);
            const data = await page.evaluate(metadata => {
                const frame = document.getElementById('frame'), w = frame.width, h = frame.height;
                const pixels = frame.getContext('2d').getImageData(0, 0, w, h).data;
                const coverage = document.createElement('canvas'); coverage.width = w; coverage.height = h;
                const target = coverage.getContext('2d'), image = target.createImageData(w, h);
                for (let i = 0; i < pixels.length; i += 4) { image.data[i] = image.data[i + 1] = image.data[i + 2] = pixels[i + 3]; image.data[i + 3] = 255; }
                target.putImageData(image, 0, 0);
                const silhouette = document.createElement('canvas'); silhouette.width = w; silhouette.height = h;
                const mask = silhouette.getContext('2d'); mask.setTransform(w / 320, 0, 0, h / 200, 0, 0);
                window.__nativeReferenceDrawMask(mask);
                const expectedMask = mask.getImageData(0, 0, w, h).data;
                const fromMetadata = document.createElement('canvas'); fromMetadata.width = w; fromMetadata.height = h;
                const geometry = fromMetadata.getContext('2d'); geometry.scale(w / 320, h / 200); geometry.fillStyle = '#fff';
                for (const source of metadata.sources.filter(source => source.contributes)) {
                    geometry.beginPath(); source.coveragePolygon.forEach((point, index) => index ? geometry.lineTo(point.x, point.y) : geometry.moveTo(point.x, point.y));
                    geometry.closePath(); geometry.fill();
                }
                const actualMask = geometry.getImageData(0, 0, w, h).data;
                let interiorMismatches = 0, maskAbsoluteError = 0, opacityMismatches = 0, opacitySamples = 0;
                for (let i = 3; i < expectedMask.length; i += 4) {
                    const delta = Math.abs(expectedMask[i] - actualMask[i]); maskAbsoluteError += delta;
                    if (delta > 200) interiorMismatches++;
                }
                function membership(polygon, x, y) {
                    let sign = 0, nearEdge = false;
                    for (let i = 0; i < polygon.length; i++) {
                        const a = polygon[i], b = polygon[(i + 1) % polygon.length];
                        const cross = (b.x - a.x) * (y - a.y) - (b.y - a.y) * (x - a.x);
                        if (Math.abs(cross) / Math.max(.001, Math.hypot(b.x - a.x, b.y - a.y)) < 2) nearEdge = true;
                        if (Math.abs(cross) > 1e-7) { if (sign && Math.sign(cross) !== sign) return { inside:false, nearEdge }; sign = Math.sign(cross); }
                    }
                    return { inside:polygon.length >= 3, nearEdge };
                }
                for (let y = 5; y < h; y += 11) for (let x = 5; x < w; x += 11) {
                    let uncovered = 1, nearEdge = false;
                    for (const source of metadata.sources.filter(source => source.contributes)) {
                        const result = membership(source.coveragePolygon, (x + .5) * 320 / w, (y + .5) * 200 / h);
                        nearEdge ||= result.nearEdge;
                        if (result.inside) uncovered *= 1 - source.opacity;
                    }
                    if (nearEdge) continue;
                    opacitySamples++;
                    if (Math.abs(pixels[(y * w + x) * 4 + 3] - (1 - uncovered) * 255) > 2) opacityMismatches++;
                }
                return { scene:frame.toDataURL('image/png').split(',')[1], coverage:coverage.toDataURL('image/png').split(',')[1],
                    silhouette:silhouette.toDataURL('image/png').split(',')[1], interiorMismatches, opacityMismatches, opacitySamples, maskMeanError:maskAbsoluteError / (w * h) };
            }, fixture.metadata);
            assert.equal(data.interiorMismatches, 0, `${fixture.name}: native polygons match actual web silhouette interiors`);
            // Intersecting separately antialiased clips differs slightly from rasterizing
            // their analytical intersection once. Full interior disagreements remain forbidden.
            assert.ok(data.maskMeanError < 1, `${fixture.name}: antialias-only polygon differences (${data.maskMeanError})`);
            assert.ok(data.opacitySamples > 50 && data.opacityMismatches === 0, `${fixture.name}: independent opacity coverage agrees with the browser (${data.opacityMismatches}/${data.opacitySamples})`);
            const outputs = { scene:Buffer.from(data.scene, 'base64'), coverage:Buffer.from(data.coverage, 'base64'),
                silhouette:Buffer.from(data.silhouette, 'base64'), appearance:await page.screenshot({ animations:'disabled' }) };
            const files = {};
            for (const [kind, bytes] of Object.entries(outputs)) {
                const name = `${fixture.name}-${kind}.png`, destination = path.join(directory, name);
                if (update) fs.writeFileSync(destination, bytes);
                else {
                    const expected = fs.readFileSync(destination).toString('base64');
                    const comparison = await page.evaluate(async ({ expected, actual }) => {
                        async function decode(base64) {
                            const image = new Image(); image.src = `data:image/png;base64,${base64}`; await image.decode();
                            const canvas = document.createElement('canvas'); canvas.width = image.width; canvas.height = image.height;
                            const context = canvas.getContext('2d'); context.drawImage(image, 0, 0); return { w:image.width, h:image.height, bytes:context.getImageData(0, 0, image.width, image.height).data };
                        }
                        const a = await decode(expected), b = await decode(actual); let total = 0, large = 0;
                        if (a.w !== b.w || a.h !== b.h) return { valid:false };
                        for (let i = 0; i < a.bytes.length; i++) { const d = Math.abs(a.bytes[i] - b.bytes[i]); total += d; if (d > 30) large++; }
                        return { valid:true, mean:total / a.bytes.length, large:large / a.bytes.length };
                    }, { expected, actual:bytes.toString('base64') });
                    assert.ok(comparison.valid && comparison.mean < 1.5 && comparison.large < .01, `${name}: reference image changed: ${JSON.stringify(comparison)}`);
                }
                files[kind] = { path:name, sha256:hash(bytes), width, height };
            }
            references.push({ name:fixture.name, files, polygonMaskMeanError:data.maskMeanError });
            console.log(`PASS: native ${fixture.name}, polygon mask error ${data.maskMeanError.toFixed(4)}, 4 reference images.`);
        }
        if (update) fs.writeFileSync(path.join(directory, 'reference-manifest.json'), JSON.stringify({ version:1,
            generator:'NativeRenderingReferenceTests.cjs', browser:await browser.version(),
            sourceImages:Object.fromEntries(Object.entries(images).map(([name, bytes]) => [name, { path:`source-${name}.jpg`, sha256:hash(bytes) }])),
            references }, null, 2) + '\n');
        assert.deepEqual(errors, [], 'Production renderer generated no browser exceptions');
    } finally {
        await page.close(); for (const client of clients) client.destroy();
        await new Promise(resolve => server.close(resolve));
    }
};

if (require.main === module) {
    const { chromium } = require('playwright');
    (async () => { const browser = await chromium.launch({headless:true});
        try { await module.exports(browser, { update:process.argv.includes('--update') }); } finally { await browser.close(); }
    })().catch(error => { console.error(error); process.exitCode = 1; });
}
