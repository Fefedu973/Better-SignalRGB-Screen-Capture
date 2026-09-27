const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');

module.exports = async function checkRawOutputPage(browser, fixtures) {
    const source = fs.readFileSync(path.join(__dirname, '..', 'Better-SignalRGB-Screen-Capture', 'Services', 'StreamingCanvasPage.cs'), 'utf8');
    const html = source.match(/public const string Html = """([\s\S]*?)""";/)[1];
    const frames = fixtures.slice(0, 2).map(fixture => Buffer.from(fixture.composite, 'base64'));
    let connections = 0;
    const streams = new Set();
    const server = http.createServer((request, response) => {
        if (!request.url.startsWith('/stream')) {
            response.writeHead(200, { 'Content-Type': 'text/html' }); response.end(html); return;
        }
        connections++;
        streams.add(response);
        response.writeHead(200, { 'Content-Type': 'multipart/x-mixed-replace; boundary=frame', 'Cache-Control': 'no-store' });
        let index = 0;
        const send = () => {
            const bytes = frames[index++ % frames.length];
            response.write(`--frame\r\nContent-Type: image/jpeg\r\nContent-Length: ${bytes.length}\r\n\r\n`);
            response.write(bytes); response.write('\r\n');
        };
        send();
        const timer = setInterval(send, 120);
        response.on('close', () => { clearInterval(timer); streams.delete(response); });
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const page = await browser.newPage({ viewport: { width: 800, height: 600 } });
    try {
        await page.goto(`http://127.0.0.1:${server.address().port}/`, { waitUntil: 'domcontentloaded' });
        const hasPixels = () => document.getElementById('frame').getContext('2d').getImageData(0, 0, 320, 200)
            .data.some((value, index) => index % 4 !== 3 && value > 30);
        await page.waitForFunction(hasPixels, null, { timeout: 5000 });
        for (const [width, height] of [[800, 600], [319, 199], [1920, 1080]]) {
            await page.setViewportSize({ width, height });
            const state = await page.evaluate(() => {
                const rect = document.getElementById('frame').getBoundingClientRect();
                return { text: document.body.innerText, x: rect.x, y: rect.y, width: rect.width, height: rect.height,
                    scrollWidth: document.documentElement.scrollWidth, scrollHeight: document.documentElement.scrollHeight };
            });
            assert.deepEqual(state, { text: '', x: 0, y: 0, width, height, scrollWidth: width, scrollHeight: height });
        }
        const hashes = new Set();
        for (let index = 0; index < 6; index++) {
            hashes.add(await page.evaluate(() => {
                const bytes = document.getElementById('frame').getContext('2d').getImageData(0, 0, 320, 200).data;
                return bytes.reduce((hash, value) => Math.imul(hash ^ value, 16777619), 2166136261);
            }));
            await new Promise(resolve => setTimeout(resolve, 120));
        }
        assert.ok(hashes.size >= 2, 'Multipart JPEGs produce actual changing output pixels');
        const standardFrames = frames.slice();
        const highQuality = await page.evaluate(() => {
            const image = document.createElement('canvas'); image.width = 800; image.height = 600;
            const ctx = image.getContext('2d'); ctx.fillStyle = '#20e040'; ctx.fillRect(0, 0, 800, 600);
            return image.toDataURL('image/jpeg', .92).split(',')[1];
        });
        frames.splice(0, frames.length, Buffer.from(highQuality, 'base64'));
        await page.waitForFunction(() => {
            const image = document.getElementById('frame');
            return image.width === 800 && image.height === 600 && image.getContext('2d').getImageData(700, 500, 1, 1).data[1] > 180;
        });
        frames.splice(0, frames.length, ...standardFrames);
        await page.waitForFunction(() => {
            const image = document.getElementById('frame'); return image.width === 320 && image.height === 200;
        });
        assert.equal(connections, 1, 'Resizing the output surface does not duplicate the stream');
        for (const response of streams) response.destroy();
        const deadline = Date.now() + 5000;
        while (connections < 2 && Date.now() < deadline) await new Promise(resolve => setTimeout(resolve, 50));
        assert.equal(connections, 2, 'A disconnected composite stream reconnects automatically');
        await page.waitForFunction(hasPixels, null, { timeout: 5000 });
        console.log('PASS: clean raw output page: real multipart pixels, live HQ 800x600 switch, reconnect, no text, full viewport at 800x600, 319x199 and 1920x1080.');
    } finally {
        await page.close();
        for (const response of streams) response.destroy();
        server.closeAllConnections();
        await new Promise(resolve => server.close(resolve));
    }
};
