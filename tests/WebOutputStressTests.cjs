const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const path = require('node:path');

const pixelBudget = 16 * 1024 * 1024;
const encodedBudget = 32 * 1024 * 1024;
const assets = path.join(__dirname, '..', 'Better-SignalRGB-Screen-Capture', 'Services', 'WebOutput');

function outputState(sources, settings = {}) {
    return { version:1, canvasWidth:320, canvasHeight:200, outputWidth:320, outputHeight:200,
        settings:{ webEnabled:true, ambilight:false, frameRate:30, ...settings }, sources };
}

async function createHarness(browser, preview) {
    const html = fs.readFileSync(path.join(assets, 'StreamingCanvasPage.html'), 'utf8')
        .replace('<!--WEB_OUTPUT_SCRIPT-->', () => `<script>${fs.readFileSync(path.join(assets, 'StreamingCanvasPage.js'), 'utf8')}</script>`);
    const clients = new Set(), requests = [], errors = [];
    let stream, version = 0;
    const server = http.createServer((request, response) => {
        requests.push(request.url);
        if (request.url.startsWith('/web-stream')) {
            stream = response; clients.add(response);
            response.writeHead(200, { 'Content-Type':'multipart/x-mixed-replace; boundary=frame', 'Cache-Control':'no-store' });
            response.flushHeaders(); response.on('close', () => clients.delete(response));
        } else { response.writeHead(200, { 'Content-Type':'text/html; charset=utf-8' }); response.end(html); }
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const page = await browser.newPage({ viewport:{ width:640, height:400 } });
    page.on('pageerror', error => errors.push(error.message));
    await page.addInitScript(() => {
        window.messages = [];
        window.chrome = { ...window.chrome, webview:{ postMessage:message => window.messages.push(message) } };
        const decode = window.createImageBitmap.bind(window);
        window.decodeDelay = 0; window.startedDecodes = 0;
        window.createImageBitmap = async (...args) => {
            window.startedDecodes++;
            if (window.decodeDelay) await new Promise(resolve => setTimeout(resolve, window.decodeDelay));
            return decode(...args);
        };
        window.peak = { pixels:0, encoded:0, decoders:0 };
        window.budgetSampler = setInterval(() => {
            const d = window.webOutputDiagnostics; if (!d) return;
            window.peak.pixels = Math.max(window.peak.pixels, d.decodedPixels);
            window.peak.encoded = Math.max(window.peak.encoded, d.bufferedBytes);
            window.peak.decoders = Math.max(window.peak.decoders, d.activeDecoders);
        }, 1);
    });
    await page.goto(`http://127.0.0.1:${server.address().port}/${preview ? '?preview=1' : ''}`, { waitUntil:'domcontentloaded' });
    await page.waitForFunction(() => window.webOutputDiagnostics);
    const deadline = Date.now() + 5000;
    while (!stream && Date.now() < deadline) await new Promise(resolve => setTimeout(resolve, 5));
    assert.ok(stream, 'Renderer opens its multipart connection');
    function part(type, bytes, id) {
        stream.write(`--frame\r\nContent-Type: ${type}\r\nContent-Length: ${bytes.length}\r\nX-State-Version: ${version}\r\n${id ? `X-Source-Id: ${id}\r\n` : ''}\r\n`);
        stream.write(bytes); stream.write('\r\n');
    }
    return {
        page, requests, errors,
        state(value) { version++; part('application/json', Buffer.from(JSON.stringify(value))); return version; },
        image(id, bytes) { part('image/jpeg', bytes, id); },
        async idle() { await page.waitForFunction(() => {
            const d = window.webOutputDiagnostics;
            return d.activeDecoders === 0 && d.pendingCount === 0 && d.bufferedBytes === 0;
        }); },
        async close() {
            await page.close();
            for (const client of clients) client.destroy();
            server.closeAllConnections(); await new Promise(resolve => server.close(resolve));
        }
    };
}

function closeRect(actual, expected, message) {
    for (const key of ['x','y','width','height'])
        assert.ok(Math.abs(actual[key] - expected[key]) < .001, `${message}: ${key} expected ${expected[key]}, got ${actual[key]}`);
}

async function checkPlacement(browser) {
    const h = await createHarness(browser, true), { page } = h;
    const initial = { x:64, y:40, width:160, height:100 };
    const record = rect => ({ screenX:rect.x, screenY:rect.y, screenWidth:rect.width, screenHeight:rect.height, ambilight:false });
    const rect = () => page.evaluate(() => {
        const style = document.getElementById('placementEditor').style;
        return { x:parseFloat(style.left), y:parseFloat(style.top), width:parseFloat(style.width), height:parseFloat(style.height) };
    });
    async function configure(value = initial) { await page.evaluate(value => window.setPreviewSettings(value), record(value)); }
    async function startDrag(handle = null) {
        const bounds = await page.locator(handle ? `[data-handle="${handle}"]` : '#placementEditor').boundingBox();
        const point = { x:bounds.x + bounds.width / 2, y:bounds.y + bounds.height / 2 };
        await page.mouse.move(point.x, point.y); await page.mouse.down(); return point;
    }
    try {
        assert.ok(h.requests.includes('/web-stream?preview=1'), 'Inline preview requests effect frames independently of the public mode');
        assert.equal(await page.locator('#placementEditor>i').count(), 8, 'Preview has eight resize handles before capture starts');
        await configure();
        let point = await startDrag();
        await page.mouse.move(point.x + 20, point.y + 10); // 2x viewport -> 10x5 canonical movement.
        const moved = { ...initial, x:74, y:45 };
        closeRect(await rect(), moved, 'Pointer movement uses canonical coordinates at non-default viewport size');
        // Native settings acknowledgements can lag a pointer event. An old
        // placement must not pull the active drag backwards, while hue may update.
        await page.evaluate(value => window.setPreviewSettings({ ...value, hue:42 }), record(initial));
        closeRect(await rect(), moved, 'Late native settings acknowledgement preserves the active gesture');
        assert.match(await page.locator('#frame').evaluate(node => node.style.filter), /hue-rotate\(42deg\)/,
            'Non-placement settings still update during a drag');
        await page.mouse.up();
        closeRect(await page.evaluate(() => window.messages.at(-1)), moved, 'Placement bridge sends the visible coordinates');

        for (const handle of ['nw','n','ne','e','se','s','sw','w']) {
            await configure(); point = await startDrag(handle);
            await page.mouse.move(point.x + 24, point.y + 16); await page.mouse.up();
            const expected = { ...initial };
            if (handle.includes('w')) { expected.x += 12; expected.width -= 12; }
            if (handle.includes('e')) expected.width += 12;
            if (handle.includes('n')) { expected.y += 8; expected.height -= 8; }
            if (handle.includes('s')) expected.height += 8;
            closeRect(await rect(), expected, `${handle} handle preserves the opposite anchor`);
        }
        await configure(); point = await startDrag('se');
        await page.keyboard.down('Shift'); await page.mouse.move(point.x + 1000, point.y + 1000);
        await page.mouse.up(); await page.keyboard.up('Shift');
        const proportional = await rect();
        assert.ok(Math.abs(proportional.width / proportional.height - 1.6) < .001, 'Shift corner resize keeps its original ratio');
        assert.ok(proportional.x === initial.x && proportional.y === initial.y &&
            proportional.x + proportional.width <= 320 && proportional.y + proportional.height <= 200,
        'Corner overshoot is clamped without moving its opposite anchor');

        for (const cancel of ['Escape','pointercancel']) {
            await configure(); point = await startDrag(); await page.mouse.move(point.x + 30, point.y + 20);
            if (cancel === 'Escape') await page.keyboard.press('Escape');
            else await page.locator('#placementEditor').dispatchEvent('pointercancel', { pointerId:1 });
            await page.mouse.up();
            closeRect(await rect(), initial, `${cancel} restores the initial placement`);
            closeRect(await page.evaluate(() => window.messages.at(-1)), initial, `${cancel} restores native settings too`);
        }
        await page.locator('#placementEditor').focus();
        await page.keyboard.press('ArrowRight'); await page.keyboard.press('Shift+ArrowDown');
        closeRect(await rect(), { ...initial, x:65, y:50 }, 'Keyboard placement uses one pixel or ten with Shift');
        assert.deepEqual(h.errors, []);
        console.log('PASS: preview bridge, all eight anchors, scaled pointer coordinates, stale native echo, cancellation, keyboard and aspect bounds.');
    } finally { await h.close(); }
}

async function checkDecodeBudget(browser, fixture) {
    const h = await createHarness(browser, false), { page } = h;
    try {
        assert.equal(await page.locator('#placementEditor').count(), 0, 'Public output has no editor');
        assert.equal(await page.evaluate(() => typeof window.setPreviewSettings), 'undefined', 'Public output has no settings bridge');
        const original = fixture.sources[0];
        const source = { id:original.id, type:'Monitor', outerStyle:original.outer, cropStyle:original.crop };
        await page.evaluate(() => { window.decodeDelay = 120; });
        h.state(outputState([source])); h.image(source.id, Buffer.from(original.jpeg, 'base64'));
        await page.waitForFunction(() => window.startedDecodes === 1);
        let visibleDuringDrag = false;
        for (let tick = 0; tick < 10; tick++) {
            await page.waitForTimeout(35);
            source.outerStyle = original.outer.replace(/left:[-\d.]+px/, `left:${original.geometry.x + tick % 2}px`);
            h.state(outputState([source]));
            visibleDuringDrag ||= await page.evaluate(() => {
                const pixels = document.getElementById('frame').getContext('2d').getImageData(0, 0, 320, 200).data;
                return pixels.some((value, index) => index % 4 === 3 && value > 0);
            });
        }
        assert.ok(visibleDuringDrag, 'A 120ms decode completes during continuous 35ms layout changes');
        assert.equal(await page.evaluate(() => window.startedDecodes), 1, 'Layout changes do not repeatedly decode a paused JPEG');
        h.state(outputState([])); await h.idle();
        await page.waitForFunction(() => window.webOutputDiagnostics.decodedPixels === 0);

        const colors = await page.evaluate(() => ['red','lime','blue'].map(color => {
            const canvas = document.createElement('canvas'); canvas.width = 1920; canvas.height = 1080;
            const context = canvas.getContext('2d'); context.fillStyle = color; context.fillRect(0, 0, canvas.width, canvas.height);
            return canvas.toDataURL('image/jpeg', .92).split(',')[1];
        }));
        const images = colors.map(color => Buffer.from(color, 'base64'));
        const sources = Array.from({ length:10 }, (_, index) => ({
            id:`00000000-0000-0000-0000-${String(index + 1).padStart(12, '0')}`, type:'Monitor',
            outerStyle:`left:${index * 32}px;top:0px;width:32px;height:200px;opacity:1;z-index:${index};transform:rotate(0deg) scale(1,1)`,
            cropStyle:'clip-path:polygon(0% 0%,100% 0%,100% 100%,0% 100%)'
        }));
        const send = (color, first, count) => sources.slice(first, first + count).forEach(source => h.image(source.id, images[color]));
        const waitForColor = (channel, count = 10) => page.waitForFunction(({ channel, count }) => {
            const context = document.getElementById('frame').getContext('2d');
            return Array.from({ length:count }, (_, index) => context.getImageData(index * 32 + 16, 100, 1, 1).data[channel])
                .every(value => value > 220);
        }, { channel, count });
        await page.evaluate(() => { window.decodeDelay = 40; window.peak = { pixels:0, encoded:0, decoders:0 }; });
        h.state(outputState(sources.slice(0, 7))); send(0, 0, 7); await waitForColor(0, 7);
        // Deliberately do not resend the existing seven JPEGs: they are paused.
        h.state(outputState(sources)); send(0, 7, 3); await waitForColor(0);
        for (const color of [1, 2]) { send(color, 0, 10); await waitForColor(color); }
        await h.idle();
        const counters = await page.evaluate(() => ({ peak:window.peak, current:window.webOutputDiagnostics }));
        assert.ok(counters.peak.decoders <= 2, 'At most two native decoders are active');
        assert.ok(counters.peak.pixels <= pixelBudget, 'Displayed, staging and conservative native scratch pixels share a bounded budget');
        assert.ok(counters.peak.encoded <= encodedBudget, 'Encoded bytes and Blob copies share a bounded budget');
        assert.equal(counters.current.sourceCount, 10);
        assert.equal(counters.current.bufferedBytes, 0, 'Completed JPEGs release all encoded reservations');
        h.state(outputState([])); await h.idle();
        await page.waitForFunction(() => window.webOutputDiagnostics.decodedPixels === 0);
        assert.deepEqual(h.errors, []);
        assert.equal(h.requests.filter(url => url === '/web-stream').length, 1, 'Drag and memory pressure do not reconnect the stream');
        console.log(`PASS: continuous drag during decode; paused 7→10 sources at 1920×1080; all update twice; peak ${counters.peak.pixels} pixels, ${counters.peak.decoders} decoders; cleanup. `);
    } finally { await h.close(); }
}

module.exports = async function checkWebOutputStress(browser, fixture) {
    await checkPlacement(browser);
    await checkDecodeBudget(browser, Array.isArray(fixture) ? fixture[0] : fixture);
};

if (require.main === module) {
    const { chromium } = require('playwright');
    const fixtures = JSON.parse(fs.readFileSync(path.join(__dirname, 'BetterSignalRGB.StreamingTests', 'obj', 'effect-render-fixtures.json'), 'utf8'));
    (async () => {
        const browser = await chromium.launch({ headless:true });
        try { await module.exports(browser, fixtures[0]); }
        finally { await browser.close(); }
    })().catch(error => { console.error(error); process.exitCode = 1; });
}
