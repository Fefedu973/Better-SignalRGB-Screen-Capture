const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { execFileSync } = require('node:child_process');
const http = require('node:http');
const root = path.resolve(__dirname, '..');
// Resolve Playwright from ordinary NODE_PATH/project dependencies. No browser or
// package is installed by this test; use an existing Chromium installation.
const { chromium } = require('playwright');
const fixturesPath = path.join(__dirname, 'BetterSignalRGB.StreamingTests', 'obj', 'effect-render-fixtures.json');
execFileSync('dotnet', ['run', '--project', path.join(__dirname, 'BetterSignalRGB.StreamingTests', 'BetterSignalRGB.StreamingTests.csproj'),
    '-c', 'Release', '--no-restore', '--', '--export-effect-fixtures', fixturesPath], { cwd:root, stdio:'inherit' });
const fixtures = JSON.parse(fs.readFileSync(fixturesPath, 'utf8'));
assert.ok(fixtures.some(fixture => fixture.name.startsWith('cardinal-') && fixture.sources.some(source => /e[+-]?\d+%/i.test(source.crop))),
    'Real invariant snapshot serialization exercises very small percentages in scientific notation');
const html = fs.readFileSync(path.join(root, 'Better-SignalRGB-Screen-Capture-Effect.html'), 'utf8');
(async () => {
    const browser = await chromium.launch({ headless:true });
    try {
        await require('./RawOutputPageTests.cjs')(browser, fixtures);
        await require('./WebOutputStressTests.cjs')(browser, fixtures);
        const page = await browser.newPage({ viewport:{ width:320, height:200 } });
        const errors = []; page.on('pageerror', error => errors.push(error.message));
        await page.evaluate(() => {
            window.sceneDraws = 0;
            const draw = CanvasRenderingContext2D.prototype.drawImage;
            CanvasRenderingContext2D.prototype.drawImage = function (...args) {
                if (this.canvas.id === 'canvas') window.sceneDraws++;
                return draw.apply(this, args);
            };
        });
        await page.setContent(html);
        await page.evaluate(() => {
            window.screenX = window.screenY = 0; window.screenW = 320; window.screenH = 200;
            onCanvasApiEvent({ sender:'BetterSignalRGBScreenCapture', event:'config:' + JSON.stringify({ version:1, enabled:true,
                pictureMode:'Standard', ambilight:true, ambilightFullscreen:true, ambilightBlur:0, ambilightSaturation:1,
                ambilightIntensity:100, frameRate:30 }) });
        });
        for (const fixture of fixtures) {
            const before = await page.evaluate(value => {
                const send = event => onCanvasApiEvent({ sender:'BetterSignalRGBScreenCapture', event });
                send('reset'); const before = window.sceneDraws;
                for (const source of value.sources) {
                    send(`header:${source.id}.jpg:image/jpeg:1:outer:${source.outer}|crop:${source.crop}`);
                    send(`data:${source.id}:0:${source.jpeg}`); send(`end:${source.id}`);
                }
                return before;
            }, fixture);
            await page.waitForFunction(({ before, count }) => window.sceneDraws >= before + count, { before, count:fixture.sources.length });
            const result = await page.evaluate(async fixture => {
                const scene = document.getElementById('canvas'), glow = document.getElementById('ambiCanvas');
                function pixels(image) {
                    const output = document.createElement('canvas'); output.width = 320; output.height = 200;
                    const c = output.getContext('2d'); c.fillStyle = '#000'; c.fillRect(0, 0, 320, 200); c.drawImage(image, 0, 0);
                    return c.getImageData(0, 0, 320, 200).data;
                }
                async function image(encoded) { const img = new Image(); img.src = 'data:image/jpeg;base64,' + encoded; await img.decode(); return img; }
                const actual = pixels(scene), glowPixels = pixels(glow), expected = pixels(await image(fixture.composite));
                let sum = 0, outliers = 0, glowDiff = 0;
                for (let index = 0; index < actual.length; index += 4) {
                    let peak = 0;
                    for (let channel = 0; channel < 3; channel++) {
                        const diff = Math.abs(actual[index + channel] - expected[index + channel]); sum += diff; peak = Math.max(peak, diff);
                        glowDiff += Math.abs(actual[index + channel] - glowPixels[index + channel]);
                    }
                    if (peak > 60) outliers++;
                }
                // Independent geometric oracle: invert only the source rotation,
                // test the rotated crop rectangle, then mirror the sampled pixels.
                // This does not use transmitted CSS, Path2D or the compositor's code.
                const samples = [];
                for (const source of fixture.sources) {
                    const img = await image(source.jpeg), c = document.createElement('canvas'); c.width = img.width; c.height = img.height;
                    const ctx = c.getContext('2d'); ctx.drawImage(img, 0, 0); samples.push({ g:source.geometry, width:img.width, height:img.height, pixels:ctx.getImageData(0, 0, img.width, img.height).data });
                }
                let oracleSamples = 0, oracleErrors = 0;
                for (let y = 4; y < 196; y += 4) for (let x = 4; x < 316; x += 4) {
                    let expected = [0, 0, 0], edge = false;
                    for (const { g, width, height, pixels:raw } of samples) {
                        const angle = -g.rotation * Math.PI / 180, dx = x + .5 - g.x - g.width / 2, dy = y + .5 - g.y - g.height / 2;
                        const localX = dx * Math.cos(angle) - dy * Math.sin(angle) + g.width / 2;
                        const localY = dx * Math.sin(angle) + dy * Math.cos(angle) + g.height / 2;
                        const cw = g.width * (1 - g.left - g.right), ch = g.height * (1 - g.top - g.bottom);
                        const cx = localX - g.width * g.left - cw / 2, cy = localY - g.height * g.top - ch / 2;
                        const cropAngle = -g.cropRotation * Math.PI / 180;
                        const cropX = cx * Math.cos(cropAngle) - cy * Math.sin(cropAngle), cropY = cx * Math.sin(cropAngle) + cy * Math.cos(cropAngle);
                        const distances = [localX, localY, g.width - localX, g.height - localY, cw / 2 - Math.abs(cropX), ch / 2 - Math.abs(cropY)];
                        if (distances.some(distance => Math.abs(distance) < 3)) { edge = true; break; }
                        if (distances.some(distance => distance < 0)) continue;
                        const sx = (g.mirrorX ? g.width - localX : localX) / g.width * width;
                        const sy = (g.mirrorY ? g.height - localY : localY) / g.height * height;
                        if (Math.abs(sx - width / 2) < 3 || Math.abs(sy - height / 2) < 3) { edge = true; break; }
                        const ix = Math.min(width - 1, Math.max(0, Math.floor(sx))), iy = Math.min(height - 1, Math.max(0, Math.floor(sy)));
                        const i = (iy * width + ix) * 4;
                        // Exclude sharp image-content edges too (e.g. the white
                        // asymmetric marker), where bilinear/nearest sampling differ.
                        for (const [dx, dy] of [[-2,0],[2,0],[0,-2],[0,2]]) {
                            const neighbor = (Math.min(height - 1, Math.max(0, iy + dy)) * width + Math.min(width - 1, Math.max(0, ix + dx))) * 4;
                            if ([0,1,2].some(channel => Math.abs(raw[neighbor + channel] - raw[i + channel]) > 35)) edge = true;
                        }
                        if (edge) break;
                        expected = expected.map((value, channel) => value * (1 - g.opacity) + raw[i + channel] * g.opacity);
                    }
                    if (edge) continue;
                    oracleSamples++;
                    const offset = (y * 320 + x) * 4;
                    if (expected.some((value, channel) => Math.abs(value - actual[offset + channel]) > 35)) oracleErrors++;
                }
                return { meanError:sum / (320 * 200 * 3), outliers:outliers / (320 * 200), glowDiff, oracleSamples, oracleErrors };
            }, fixture);
            assert.ok(result.meanError < 7, `${fixture.name}: composite mean RGB error ${result.meanError}`);
            assert.ok(result.outliers < .035, `${fixture.name}: ${result.outliers * 100}% large pixel differences`);
            assert.equal(result.glowDiff, 0, `${fixture.name}: glow and visible scene must have identical transforms`);
            assert.ok(result.oracleSamples > 2000 && result.oracleErrors / result.oracleSamples < .006,
                `${fixture.name}: independent transform oracle ${result.oracleErrors}/${result.oracleSamples} mismatches`);
            console.log(`PASS: ${fixture.name}; mean RGB error ${result.meanError.toFixed(3)}, >60 difference ${(100 * result.outliers).toFixed(2)}%, oracle ${result.oracleErrors}/${result.oracleSamples}`);
        }
        const filters = await page.evaluate(() => {
            onCanvasApiEvent({ sender:'BetterSignalRGBScreenCapture', event:'config:' + JSON.stringify({ version:1, enabled:true,
                pictureMode:'Mono', blur:true, hue:40, brightness:12, saturation:30, ambilight:true, ambilightFullscreen:true, hideSources:true }) });
            const scene = document.getElementById('canvas'), glow = document.getElementById('ambiCanvas');
            return { scene:getComputedStyle(scene).filter, glow:getComputedStyle(glow).filter,
                hidden:getComputedStyle(document.getElementById('screenContainer')).visibility };
        });
        assert.match(filters.scene, /grayscale\(1\).*blur\(1px\)/); assert.match(filters.glow, /grayscale\(1\).*blur\(1px\)/);
        assert.equal(filters.hidden, 'hidden');
        async function screenshotStats() {
            const screenshot = await page.screenshot();
            return page.evaluate(async encoded => {
                const image = new Image(); image.src = `data:image/png;base64,${encoded}`; await image.decode();
                const output = document.createElement('canvas'); output.width = image.width; output.height = image.height;
                const context = output.getContext('2d'); context.drawImage(image, 0, 0);
                const rgba = context.getImageData(0, 0, output.width, output.height).data;
                let lit = 0, colored = 0;
                for (let index = 0; index < rgba.length; index += 4) {
                    const r = rgba[index], g = rgba[index + 1], b = rgba[index + 2];
                    if (Math.max(r, g, b) > 5) lit++;
                    if (Math.max(r, g, b) - Math.min(r, g, b) > 3) colored++;
                }
                return { lit, colored };
            }, screenshot.toString('base64'));
        }
        const monochrome = await screenshotStats();
        assert.ok(monochrome.lit > 500, 'The full-area SVG glow renders while the source picture is hidden');
        assert.equal(monochrome.colored, 0, 'The final browser output honors Mono even with image blur and fullscreen glow');
        await page.evaluate(() => onCanvasApiEvent({ sender:'BetterSignalRGBScreenCapture', event:'config:' + JSON.stringify({
            version:1, enabled:true, ambilight:true, ambilightFullscreen:true, hideSources:true, ambilightIntensity:0 }) }));
        assert.equal((await screenshotStats()).lit, 0, 'Zero ambilight intensity actually removes the visible glow');
        const pressure = await page.evaluate(async jpeg => {
            const send = event => onCanvasApiEvent({ sender:'BetterSignalRGBScreenCapture', event });
            send('reset');
            send('config:' + JSON.stringify({ version:1, enabled:true, pictureMode:'Standard', ambilight:false, frameRate:30 }));
            const decoded = atob(jpeg), padded = btoa(decoded + '\0'.repeat(6 * 1024 * 1024 - decoded.length));
            const ids = [0, 1, 2, 3].map(index => `00000000-0000-0000-0000-${index.toString().padStart(12, '0')}`);
            function frame(index, encoded) {
                const outer = `left:${10 + index * 70}px;top:20px;width:50px;height:50px;opacity:1;z-index:${index};transform:rotate(0deg) scale(1,1)`;
                send(`header:${ids[index]}.jpg:image/jpeg:1:outer:${outer}|crop:clip-path:polygon(0% 0%,100% 0%,100% 100%,0% 100%)`);
                send(`data:${ids[index]}:0:${encoded}`); send(`end:${ids[index]}`);
            }
            const canvas = document.getElementById('canvas'), ctx = canvas.getContext('2d');
            async function until(condition) {
                const deadline = Date.now() + 5000;
                while (!condition()) { if (Date.now() > deadline) throw new Error('Budget stress renderer timed out'); await new Promise(requestAnimationFrame); }
            }
            for (let index = 0; index < 4; index++) frame(index, padded);
            await until(() => ids.every((_, index) => ctx.getImageData(20 + index * 70, 30, 1, 1).data[0] > 100));
            const before = ctx.getImageData(0, 0, 320, 200).data;
            // The JPEG is invalid, but admitting it under pressure must not clear
            // the old image. Force redraw after the decoder's error has settled.
            frame(0, 'AAAA');
            await new Promise(resolve => setTimeout(resolve, 100));
            const drawsBefore = window.sceneDraws; window.invalidateCaptureLayout();
            await until(() => window.sceneDraws > drawsBefore);
            const after = ctx.getImageData(0, 0, 320, 200).data;
            let changed = 0;
            for (let index = 0; index < before.length; index++) if (before[index] !== after[index]) changed++;
            const replacement = document.createElement('canvas'); replacement.width = replacement.height = 50;
            const paint = replacement.getContext('2d'); paint.fillStyle = '#0000ff'; paint.fillRect(0, 0, 50, 50);
            frame(0, replacement.toDataURL('image/jpeg').split(',')[1]);
            await until(() => ctx.getImageData(20, 30, 1, 1).data[2] > 200 && ctx.getImageData(20, 30, 1, 1).data[0] < 20);
            return { changed, recovered:true, otherSourcesIntact:ids.slice(1).every((_, index) => ctx.getImageData(90 + index * 70, 30, 1, 1).data[0] > 100) };
        }, fixtures[0].sources[0].jpeg);
        assert.equal(pressure.changed, 0, 'Encoded-budget recovery preserves actual displayed pixels after a corrupt replacement');
        assert.ok(pressure.recovered && pressure.otherSourcesIntact, 'A valid replacement recovers at a full 32 MiB budget without evicting other sources');
        const feedback = [];
        const server = http.createServer((request, response) => {
            response.setHeader('Access-Control-Allow-Origin', '*');
            response.setHeader('Access-Control-Allow-Private-Network', 'true');
            response.setHeader('Access-Control-Allow-Methods', 'GET, OPTIONS');
            if (request.method === 'GET') feedback.push(new URL(request.url, 'http://localhost').searchParams);
            response.writeHead(204); response.end();
        });
        await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
        try {
            const session = '1234567890abcdef1234567890abcdef';
            const callback = `http://localhost:${server.address().port}/api/effect-status`;
            await page.evaluate(({ session, callback }) => {
                onCanvasApiEvent({ sender:'BetterSignalRGBScreenCapture', event:'health:' + JSON.stringify({ version:1, session, callback }) });
            }, { session, callback });
            const deadline = Date.now() + 6000;
            while (!feedback.some(item => Number(item.get('frames')) > 0)) {
                if (Date.now() > deadline) throw new Error('The actual browser did not acknowledge a rendered image');
                await new Promise(resolve => setTimeout(resolve, 50));
            }
            assert.ok(feedback.every(item => item.get('session') === session && item.get('version') === '1'));
            assert.ok(feedback.length <= 5, 'Render feedback is throttled independently of animation callbacks');
            console.log('PASS: actual browser loopback feedback confirms source rendering with the active session token.');
        } finally {
            await page.evaluate(() => onCanvasApiEvent({ sender:'BetterSignalRGBScreenCapture', event:'reset' }));
            server.closeAllConnections();
            await new Promise(resolve => server.close(resolve));
        }
        assert.deepEqual(errors, []);
        console.log('PASS: actual Chromium Canvas2D pixels match the production compositor and an independent crop/rotation/mirror oracle; combined filters remain valid.');
    } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
