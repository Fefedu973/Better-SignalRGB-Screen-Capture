'use strict';

// Generate a short, silent synthetic clip for native WebView file:/// capture tests.
const { chromium } = require('playwright');
const fs = require('node:fs/promises');
const path = require('node:path');

(async () => {
    const output = path.resolve(__dirname, 'obj', 'local-media-fixture.webm');
    const browser = await chromium.launch({ headless: true });
    try {
        const page = await browser.newPage();
        const bytes = await page.evaluate(async () => {
            const canvas = document.createElement('canvas');
            canvas.width = 320; canvas.height = 240;
            const context = canvas.getContext('2d');
            const stream = canvas.captureStream(10);
            const recorder = new MediaRecorder(stream, { mimeType: 'video/webm;codecs=vp8' });
            const chunks = [];
            recorder.ondataavailable = event => chunks.push(event.data);
            const stopped = new Promise(resolve => { recorder.onstop = resolve; });
            let frame = 0;
            const draw = () => {
                context.fillStyle = `hsl(${(frame++ * 35) % 360},100%,50%)`;
                context.fillRect(0, 0, 320, 240);
            };
            draw();
            recorder.start();
            const timer = setInterval(draw, 100);
            await new Promise(resolve => setTimeout(resolve, 2200));
            clearInterval(timer);
            recorder.stop();
            await stopped;
            stream.getTracks().forEach(track => track.stop());
            return Array.from(new Uint8Array(await new Blob(chunks).arrayBuffer()));
        });
        await fs.mkdir(path.dirname(output), { recursive: true });
        await fs.writeFile(output, Buffer.from(bytes));
        console.log(`Generated silent synthetic WebM: ${output} (${bytes.length} bytes)`);
    } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
