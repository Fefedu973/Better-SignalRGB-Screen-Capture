(() => {
    'use strict';
    const MAX_SOURCES = 128, MAX_HEADER = 4096, MAX_STATE = 256 * 1024, MAX_FRAME = 8 * 1024 * 1024;
    const MAX_BUFFERED = 32 * 1024 * 1024, MAX_PIXELS = 16 * 1024 * 1024, MAX_FRAME_PIXELS = 2101000, MAX_DECODERS = 2;
    const frame = document.getElementById('frame'), context = frame.getContext('2d');
    const glow = document.getElementById('glow'), glowContext = glow.getContext('2d');
    const contourCanvas = document.getElementById('contourHalo');
    const screen = document.getElementById('screenContainer'), stage = document.getElementById('stage');
    const filters = [document.getElementById('ambilight'), document.getElementById('fullscreenAmbilight')];
    const styleParser = document.createElement('div').style, textDecoder = new TextDecoder('utf-8', { fatal:true });
    const sources = new Map(), decodeQueue = new Set();
    const raw = { id:null, pending:null, loading:null, displayed:null };
    const pictureFilters = { Standard:'', Cinema:'sepia(0.2) contrast(1.1) brightness(0.9)',
        Mono:'grayscale(1)', Vivid:'contrast(1.3) saturate(1.4) brightness(1.1)',
        Dominant:'contrast(1.1) saturate(1.2)', HD:'contrast(1.05) saturate(1.1) brightness(1.02)' };
    const finite = (value, fallback, min, max) => typeof value === 'number' && Number.isFinite(value)
        ? Math.max(min, Math.min(max, value)) : fallback;
    const bool = (value, fallback) => typeof value === 'boolean' ? value : fallback;
    const preview = window.__outputPreview === true || new URLSearchParams(location.search).get('preview') === '1';
    let settings = normalize({}), ordered = [], stateVersion = 0, generation = 0, active = true;
    let bufferedBytes = 0, decodedPixels = 0, activeDecoders = 0, renderCount = 0;
    let request, retry, renderTimer, animation, dirty = false, lastDraw = -Infinity;
    let previewSettings = null, placementEditor = null, placementGesture = null;
    let contourRenderer = null, layoutSequence = 0;

    // Diagnostics expose counters only, never JPEGs, URLs, source names or mutation hooks.
    Object.defineProperty(window, 'webOutputDiagnostics', { get:() => Object.freeze({
        stateVersion, generation, activeDecoders, bufferedBytes, decodedPixels,
        sourceCount:sources.size, pendingCount:decodeQueue.size, renderCount, webEnabled:settings.webEnabled,
        halo:contourRenderer?.diagnostics || null }) });

    function normalize(value) {
        const width = finite(value.screenWidth, 320, 1, 320), height = finite(value.screenHeight, 200, 1, 200);
        return { webEnabled:bool(value.webEnabled, false),
            screenX:finite(value.screenX, 0, 0, 320 - width), screenY:finite(value.screenY, 0, 0, 200 - height),
            screenWidth:width, screenHeight:height,
            pictureMode:Object.hasOwn(pictureFilters, value.pictureMode) ? value.pictureMode : 'Standard',
            hue:finite(value.hue, 0, -180, 180), brightness:finite(value.brightness, 0, -100, 100),
            saturation:finite(value.saturation, 0, -100, 100), blur:bool(value.blur, false),
            ambilight:bool(value.ambilight, true), ambilightFullscreen:bool(value.ambilightFullscreen, false),
            hideSources:bool(value.hideSources, false), ambilightBlur:finite(value.ambilightBlur, 30, 0, 100),
            ambilightSpread:finite(value.ambilightSpread, 10, 0, 100),
            ambilightStyle:['Soft','Contours'].includes(value.ambilightStyle) ? value.ambilightStyle : 'Classic', ambilightCutoff:finite(value.ambilightCutoff, 0, 0, 100),
            ambilightEdgeDepth:finite(value.ambilightEdgeDepth, 3, 1, 20),
            ambilightEdgeMix:finite(value.ambilightEdgeMix, 2, 0, 30),
            ambilightEdgeReach:finite(value.ambilightEdgeReach, 60, 1, 200),
            ambilightEdgeFade:finite(value.ambilightEdgeFade, 50, 0, 100),
            ambilightSaturation:finite(value.ambilightSaturation, 3, 0, 10),
            ambilightIntensity:finite(value.ambilightIntensity, 100, 0, 200),
            interpolation:value.interpolation === 'pixelated' ? 'pixelated' : 'smooth',
            frameRate:finite(value.frameRate, 15, 1, 30) };
    }

    function applyFilters() {
        const s = settings, effects = s.webEnabled;
        const contours = effects && s.ambilight && s.ambilightStyle === 'Contours';
        const x = effects ? s.screenX : 0, y = effects ? s.screenY : 0;
        const width = effects ? s.screenWidth : 320, height = effects ? s.screenHeight : 200;
        screen.style.left = `${x}px`; screen.style.top = `${y}px`;
        screen.style.width = `${width}px`; screen.style.height = `${height}px`;
        frame.style.transform = `scale(${width / 320},${height / 200})`;
        const tone = `hue-rotate(${s.hue}deg) brightness(${100 + s.brightness}%) saturate(${100 + s.saturation}%) ${pictureFilters[s.pictureMode]}`;
        const color = tone + (s.blur ? ' blur(1px)' : '');
        frame.style.filter = effects ? color : 'none';
        glow.style.filter = effects ? `${color} url(#fullscreenAmbilight)` : 'none';
        // Picture softness must not bleed an exterior-only halo back into its mask.
        contourCanvas.style.filter = effects ? tone : 'none';
        contourCanvas.style.display = contours ? 'block' : 'none';
        if (!contours) releaseContourRenderer();
        screen.style.filter = effects && s.ambilight && !s.ambilightFullscreen && !contours ? 'url(#ambilight)' : 'none';
        const fullscreen = effects && s.ambilight && s.ambilightFullscreen;
        glow.style.display = fullscreen && !contours ? 'block' : 'none';
        screen.style.visibility = fullscreen && s.hideSources ? 'hidden' : 'visible';
        frame.style.imageRendering = glow.style.imageRendering = effects && s.interpolation === 'pixelated' ? 'pixelated' : 'auto';
        const near = Math.max(.01, s.ambilightBlur * .35 + s.ambilightSpread * .15);
        const far = Math.max(.01, s.ambilightBlur + s.ambilightSpread * .5);
        const margin = Math.ceil(s.ambilightStyle === 'Soft' ? 3 * Math.max(near, far) : s.ambilightSpread + 3 * s.ambilightBlur);
        filters.forEach((filter, index) => {
            const input = s.ambilightCutoff > 0 ? 'glowSource' : 'SourceGraphic';
            filter.querySelector('[data-role="cutoff"]').setAttribute('values',
                `1 0 0 0 0 0 1 0 0 0 0 0 1 0 0 1.063 3.576 .361 0 ${-5 * s.ambilightCutoff / 100}`);
            const spread = filter.querySelector('[data-role="spread"]');
            spread.setAttribute('in', input); spread.setAttribute('radius', s.ambilightSpread);
            filter.querySelector('[data-role="classic-saturation"]').setAttribute('values', s.ambilightSaturation);
            filter.querySelector('[data-role="classic-blur"]').setAttribute('stdDeviation', s.ambilightBlur);
            const soft = filter.querySelector('[data-role="soft-saturation"]');
            soft.setAttribute('in', input); soft.setAttribute('values', s.ambilightSaturation);
            filter.querySelector('[data-role="near"]').setAttribute('stdDeviation', near);
            filter.querySelector('[data-role="far"]').setAttribute('stdDeviation', far);
            filter.querySelector('[data-role="intensity"]').setAttribute('in', s.ambilightStyle === 'Soft' ? 'softLight' : 'classicLight');
            for (const channel of ['R','G','B']) filter.querySelector(`feFunc${channel}`).setAttribute('slope', s.ambilightIntensity / 100);
            for (const [name, value] of Object.entries({ x:-margin, y:-margin, width:(index ? 320 : width) + 2 * margin, height:(index ? 200 : height) + 2 * margin }))
                filter.setAttribute(name, value);
        });
        updatePlacementEditor();
    }

    // Geometry is shared with the SignalRGB renderer: mirror-compensated snapshot
    // polygons keep the crop fixed while the media itself is reflected.
    function parseLayout(outer, crop) {
        if (typeof outer !== 'string' || typeof crop !== 'string' || outer.length > 4096 || crop.length > 4096) return null;
        styleParser.cssText = outer;
        const rotation = /rotate\(([-\d.]+)deg\)/.exec(styleParser.transform);
        const scale = /scale\(([-\d.]+),\s*([-\d.]+)\)/.exec(styleParser.transform);
        const layout = { x:parseFloat(styleParser.left), y:parseFloat(styleParser.top),
            w:parseFloat(styleParser.width), h:parseFloat(styleParser.height),
            rotation:rotation ? Number(rotation[1]) * Math.PI / 180 : 0,
            sx:scale ? Number(scale[1]) : 1, sy:scale ? Number(scale[2]) : 1,
            opacity:styleParser.opacity === '' ? 1 : Number(styleParser.opacity), z:Number(styleParser.zIndex) || 0 };
        if (Object.values(layout).some(value => !Number.isFinite(value)) || layout.w <= 0 || layout.h <= 0 ||
            layout.w > 7680 || layout.h > 4320 || Math.abs(layout.x) > 1e7 || Math.abs(layout.y) > 1e7 ||
            Math.abs(layout.sx) !== 1 || Math.abs(layout.sy) !== 1) return null;
        styleParser.cssText = crop;
        const polygon = /^polygon\((.*)\)$/.exec(styleParser.clipPath);
        if (!polygon) return null;
        const points = polygon[1].split(',').map(point => point.trim().split(/\s+/));
        const percentage = /^[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:e[+-]?\d+)?%$/i;
        if (points.length < 3 || points.length > 16 || points.some(point => point.length !== 2 ||
            point.some(value => !percentage.test(value) || !Number.isFinite(parseFloat(value)) || Math.abs(parseFloat(value)) > 1000))) return null;
        layout.clip = new Path2D(); layout.clip.rect(0, 0, layout.w, layout.h);
        layout.crop = new Path2D();
        points.forEach((point, index) => {
            const x = parseFloat(point[0]) * layout.w / 100, y = parseFloat(point[1]) * layout.h / 100;
            if (index === 0) layout.crop.moveTo(x, y); else layout.crop.lineTo(x, y);
        });
        layout.crop.closePath();
        layout.haloId = ++layoutSequence;
        return layout;
    }

    function discardPending(source) {
        if (source.pending) { bufferedBytes -= source.pending.bytes.length; source.pending = null; }
        decodeQueue.delete(source);
    }
    function releaseDisplayed(source) {
        if (!source.displayed) return;
        decodedPixels -= source.displayed.pixels;
        releaseImage(source.displayed.bitmap); source.displayed = null;
    }
    function releaseImage(image) {
        if (typeof image.close === 'function') image.close();
        else image.width = image.height = 0;
    }
    function fitSize(width, height, limit) {
        const factor = Math.min(1, Math.sqrt(limit / (width * height)));
        width = Math.max(1, Math.floor(width * factor)); height = Math.max(1, Math.floor(height * factor));
        // One-pixel-thin inputs can survive floor() on only one axis.
        if (width * height > limit) width = Math.max(1, Math.floor(limit / height));
        return { width, height, pixels:width * height };
    }
    function sourcePixelLimit() {
        // Keep staging slots and native decoder scratch for full JPEG dimensions
        // in addition to every displayed source. Even a decoder that allocates
        // the original before resizing stays inside this conservative budget.
        return Math.min(MAX_FRAME_PIXELS, Math.floor((MAX_PIXELS - MAX_DECODERS * MAX_FRAME_PIXELS) /
            ((settings.webEnabled ? Math.max(1, sources.size) : 1) + MAX_DECODERS)));
    }
    function rebalanceDisplayed() {
        const limit = sourcePixelLimit();
        for (const source of settings.webEnabled ? sources.values() : [raw]) {
            const previous = source.displayed;
            if (!previous || previous.pixels <= limit) continue;
            const size = fitSize(previous.bitmap.width, previous.bitmap.height, limit);
            if (decodedPixels + size.pixels > MAX_PIXELS) continue;
            // Newly added sources lower the per-source allowance. Shrink even
            // paused images so their former large allocations cannot starve new
            // sources. The old image survives until the bounded copy succeeds.
            const copy = document.createElement('canvas');
            decodedPixels += size.pixels;
            try {
                copy.width = size.width; copy.height = size.height;
                const paint = copy.getContext('2d'); paint.imageSmoothingQuality = 'high';
                paint.drawImage(previous.bitmap, 0, 0, size.width, size.height);
                releaseDisplayed(source);
                source.displayed = { bitmap:copy, pixels:size.pixels };
            } catch { releaseImage(copy); decodedPixels -= size.pixels; }
        }
    }
    function releaseSource(source) {
        discardPending(source); releaseDisplayed(source);
        // In-flight createImageBitmap cannot be aborted. Keep its reservation and
        // decoder slot until it settles; the removed flag prevents resurrection.
        source.removed = true;
    }
    function clearImages() {
        for (const source of sources.values()) releaseSource(source);
        sources.clear(); ordered = [];
        discardPending(raw); releaseDisplayed(raw);
        if (raw.loading) raw.loading.version = -1;
    }
    function reserveBytes(length) {
        if (bufferedBytes + length > MAX_BUFFERED) {
            for (const source of decodeQueue) {
                discardPending(source);
                if (bufferedBytes + length <= MAX_BUFFERED) break;
            }
        }
        if (bufferedBytes + length > MAX_BUFFERED) return false;
        bufferedBytes += length; return true;
    }
    function validSource(source) { return source === raw ? !settings.webEnabled : settings.webEnabled && sources.get(source.id) === source; }

    function receiveState(bytes, version) {
        const value = JSON.parse(textDecoder.decode(bytes));
        if (!value || value.version !== 1 || value.canvasWidth !== 320 || value.canvasHeight !== 200 ||
            !((value.outputWidth === 320 && value.outputHeight === 200) || (value.outputWidth === 800 && value.outputHeight === 600)) ||
            !value.settings || typeof value.settings !== 'object' || !Array.isArray(value.sources) || value.sources.length > MAX_SOURCES)
            throw new Error('Invalid output state');
        if (version <= stateVersion) return;
        const next = new Map();
        for (const item of value.sources) {
            if (!item || typeof item.id !== 'string' || !/^[a-f0-9]{8}-(?:[a-f0-9]{4}-){3}[a-f0-9]{12}$/i.test(item.id)) throw new Error('Invalid source');
            const id = item.id.toLowerCase(), existing = sources.get(id);
            if (next.has(id)) throw new Error('Duplicate source');
            const key = `${item.outerStyle}\n${item.cropStyle}`;
            const layout = existing?.layoutKey === key ? existing.layout : parseLayout(item.outerStyle, item.cropStyle);
            if (!layout) throw new Error('Invalid layout');
            next.set(id, { id, key, layout, identity:`${item.type}|${item.websiteUrl || ''}` });
        }
        const nextSettings = previewSettings || normalize(preview ? { ...value.settings, webEnabled:true } : value.settings);
        const modeChanged = nextSettings.webEnabled !== settings.webEnabled;
        const cadenceChanged = nextSettings.frameRate !== settings.frameRate;
        const sizeChanged = frame.width !== value.outputWidth || frame.height !== value.outputHeight;
        if (modeChanged) clearImages();
        stateVersion = version; settings = nextSettings;
        for (const [id, source] of sources) if (!next.has(id) || next.get(id).identity !== source.identity) {
            releaseSource(source); sources.delete(id);
        }
        if (settings.webEnabled) for (const item of next.values()) {
            let source = sources.get(item.id);
            if (!source) { source = { id:item.id, pending:null, loading:null, displayed:null }; sources.set(item.id, source); }
            source.layout = item.layout; source.layoutKey = item.key; source.identity = item.identity;
            // A JPEG contains source pixels, not a layout. Reuse an in-flight
            // decode during continuous dragging and draw with the current mask;
            // restarting it on each state would starve slower decoders forever.
            if (source.pending) source.pending.version = version;
            if (source.loading) source.loading.version = version;
        }
        if (!settings.webEnabled) {
            if (raw.pending) raw.pending.version = version;
            if (raw.loading?.version > 0 && raw.loading.generation === generation && !modeChanged) raw.loading.version = version;
        }
        ordered = Array.from(sources.values()).sort((a, b) => a.layout.z - b.layout.z);
        if (sizeChanged) { frame.width = glow.width = contourCanvas.width = value.outputWidth; frame.height = glow.height = contourCanvas.height = value.outputHeight; }
        if (modeChanged || sizeChanged) clearCanvases();
        if (modeChanged || cadenceChanged) cancelRender();
        rebalanceDisplayed(); applyFilters(); scheduleRender(); pumpDecoders();
    }

    // Reject implausible dimensions before native decoding, not after allocating
    // an attacker-controlled bitmap. JPEG SOF supplies the complete pixel budget.
    function jpegSize(bytes) {
        if (bytes.length < 4 || bytes[0] !== 255 || bytes[1] !== 216) return null;
        let offset = 2;
        while (offset + 3 < bytes.length) {
            if (bytes[offset++] !== 255) return null;
            while (bytes[offset] === 255) offset++;
            const marker = bytes[offset++];
            if (marker === 217 || marker === 218) return null;
            if (marker === 1 || marker >= 208 && marker <= 215) continue;
            const length = bytes[offset] * 256 + bytes[offset + 1];
            if (length < 2 || offset + length > bytes.length) return null;
            if (marker >= 192 && marker <= 207 && ![196, 200, 204].includes(marker)) {
                if (length < 8) return null;
                const height = bytes[offset + 3] * 256 + bytes[offset + 4], width = bytes[offset + 5] * 256 + bytes[offset + 6];
                return width > 0 && height > 0 && width <= 1920 && height <= 1920 && width * height <= MAX_FRAME_PIXELS
                    ? { width, height, pixels:width * height } : null;
            }
            offset += length;
        }
        return null;
    }

    // Takes ownership of the parser's encoded-byte reservation.
    function receiveFrame(bytes, version, id) {
        const source = id ? sources.get(id) : raw;
        if (!active || version !== stateVersion || !source || !validSource(source)) { bufferedBytes -= bytes.length; return; }
        discardPending(source);
        source.pending = { bytes, version, generation };
        decodeQueue.add(source); pumpDecoders();
    }
    function pumpDecoders() {
        if (!active) return;
        for (const source of decodeQueue) {
            if (activeDecoders >= MAX_DECODERS) break;
            if (source.loading) continue;
            const item = source.pending;
            if (!item) { decodeQueue.delete(source); continue; }
            const original = jpegSize(item.bytes);
            if (!validSource(source) || item.version !== stateVersion || !original) { discardPending(source); continue; }
            const size = fitSize(original.width, original.height, sourcePixelLimit());
            // Keep the latest compressed frame while old-generation decoders
            // drain. It must not be lost when a paused source cannot resend it.
            if (decodedPixels + original.pixels + size.pixels > MAX_PIXELS) continue;
            decodeQueue.delete(source); source.pending = null;
            if (!reserveBytes(item.bytes.length)) { bufferedBytes -= item.bytes.length; continue; }
            // Charge both the retained ArrayBuffer and Blob copy. Keep encoded
            // accounting until the native decoder releases its input.
            source.loading = item; activeDecoders++; decodedPixels += original.pixels + size.pixels;
            decode(source, item, size, original.pixels);
        }
    }
    async function decode(source, item, size, scratchPixels) {
        let bitmap, retained = false;
        try {
            bitmap = await createImageBitmap(new Blob([item.bytes], { type:'image/jpeg' }),
                { resizeWidth:size.width, resizeHeight:size.height, resizeQuality:'high' });
            if (active && item.generation === generation && item.version === stateVersion && validSource(source) &&
                bitmap.width === size.width && bitmap.height === size.height) {
                releaseDisplayed(source);
                source.displayed = { bitmap, pixels:size.pixels }; retained = true;
                scheduleRender();
            }
        } catch { /* Keep the previous valid frame after a malformed JPEG. */ }
        finally {
            if (!retained) { bitmap?.close(); decodedPixels -= size.pixels; }
            decodedPixels -= scratchPixels;
            bufferedBytes -= item.bytes.length * 2;
            if (source.loading === item) source.loading = null;
            activeDecoders--; rebalanceDisplayed(); pumpDecoders();
        }
    }

    function clearCanvases() {
        context.resetTransform(); context.clearRect(0, 0, frame.width, frame.height);
        glowContext.resetTransform(); glowContext.clearRect(0, 0, glow.width, glow.height);
    }
    function releaseContourRenderer() {
        contourRenderer?.dispose(); contourRenderer = null;
    }
    function drawSource(target, source, mask = false) {
        if (!source.displayed || source.layout.opacity <= 0) return;
        const item = source.layout;
        target.save();
        try {
            target.globalAlpha = mask ? 1 : Math.max(0, Math.min(1, item.opacity));
            target.translate(item.x + item.w / 2, item.y + item.h / 2);
            target.rotate(item.rotation); target.scale(item.sx, item.sy); target.translate(-item.w / 2, -item.h / 2);
            target.clip(item.clip); target.clip(item.crop);
            if (mask) { target.fillStyle = '#fff'; target.fillRect(0, 0, item.w, item.h); }
            else target.drawImage(source.displayed.bitmap, 0, 0, item.w, item.h);
        } finally { target.restore(); }
    }
    function drawContourMask(target) {
        target.save();
        try {
            // The picture is clipped to the capture canvas before global placement.
            target.beginPath(); target.rect(0, 0, 320, 200); target.clip();
            for (const source of ordered) drawSource(target, source, true);
        } finally { target.restore(); }
    }
    function cancelRender() {
        clearTimeout(renderTimer); renderTimer = null;
        if (animation != null) cancelAnimationFrame(animation);
        animation = null; dirty = false;
    }
    function scheduleRender() {
        dirty = true;
        if (!active || renderTimer != null || animation != null) return;
        const interval = settings.webEnabled ? 1000 / settings.frameRate : 0;
        renderTimer = setTimeout(() => {
            renderTimer = null;
            animation = requestAnimationFrame(() => { animation = null; draw(); });
        }, Math.max(0, interval - (performance.now() - lastDraw)));
    }
    function draw() {
        if (!active || !dirty) return;
        clearCanvases();
        if (settings.webEnabled) {
            context.imageSmoothingEnabled = settings.interpolation === 'smooth';
            context.setTransform(frame.width / 320, 0, 0, frame.height / 200, 0, 0);
            for (const source of ordered) drawSource(context, source);
            if (settings.ambilight && settings.ambilightStyle === 'Contours') {
                contourRenderer ||= window.ContourHalo.create(contourCanvas);
                const geometryKey = ordered.filter(source => source.displayed && source.layout.opacity > 0)
                    .map(source => source.layout.haloId).join(',');
                contourRenderer.render(frame, drawContourMask, geometryKey, settings);
            } else if (settings.ambilight && settings.ambilightFullscreen) {
                glowContext.imageSmoothingEnabled = settings.interpolation === 'smooth';
                glowContext.drawImage(frame, 0, 0);
            }
        } else if (raw.displayed) {
            context.imageSmoothingEnabled = true;
            context.drawImage(raw.displayed.bitmap, 0, 0, frame.width, frame.height);
        }
        dirty = false; lastDraw = performance.now(); renderCount++;
    }

    // A fixed-size header buffer and one pre-sized body avoid repeated concatenation
    // and quadratic copies when a proxy fragments a multi-megabyte JPEG.
    class MultipartReader {
        constructor(boundary) {
            this.boundary = `--${boundary}`; this.header = new Uint8Array(MAX_HEADER);
            this.headerLength = 0; this.part = null; this.body = null; this.offset = 0;
        }
        dispose() {
            if (this.body) bufferedBytes -= this.body.length;
            this.body = this.part = null;
        }
        parseHeader() {
            const lines = textDecoder.decode(this.header.subarray(0, this.headerLength - 4)).trimStart().split('\r\n');
            if (lines.shift() !== this.boundary) throw new Error('Invalid boundary');
            const values = new Map();
            for (const line of lines) {
                const index = line.indexOf(':'); if (index <= 0) throw new Error('Invalid header');
                const key = line.slice(0, index).trim().toLowerCase();
                if (values.has(key)) throw new Error('Duplicate header');
                values.set(key, line.slice(index + 1).trim());
            }
            const size = values.get('content-length'), state = values.get('x-state-version');
            const type = values.get('content-type')?.toLowerCase().split(';')[0];
            if (!/^\d+$/.test(size || '') || !/^\d+$/.test(state || '')) throw new Error('Missing length/version');
            const length = Number(size), version = Number(state), id = values.get('x-source-id')?.toLowerCase() || null;
            if (!Number.isSafeInteger(version) || version < 1 || !Number.isSafeInteger(length) || length < 1 ||
                length > (type === 'application/json' ? MAX_STATE : MAX_FRAME) || !['application/json', 'image/jpeg'].includes(type) ||
                id && !/^[a-f0-9]{8}-(?:[a-f0-9]{4}-){3}[a-f0-9]{12}$/.test(id)) throw new Error('Invalid part');
            this.part = { length, version, id, type }; this.offset = 0; this.headerLength = 0;
            if (reserveBytes(length)) this.body = new Uint8Array(length);
            else if (type === 'application/json') throw new Error('State memory limit');
        }
        push(chunk) {
            let offset = 0;
            while (offset < chunk.length) {
                if (!this.part) {
                    if (this.headerLength === MAX_HEADER) throw new Error('Oversized header');
                    this.header[this.headerLength++] = chunk[offset++];
                    const n = this.headerLength;
                    if (n >= 4 && this.header[n - 4] === 13 && this.header[n - 3] === 10 && this.header[n - 2] === 13 && this.header[n - 1] === 10)
                        this.parseHeader();
                    continue;
                }
                const count = Math.min(chunk.length - offset, this.part.length - this.offset);
                if (this.body) this.body.set(chunk.subarray(offset, offset + count), this.offset);
                offset += count; this.offset += count;
                if (this.offset !== this.part.length) continue;
                const { type, version, id } = this.part, body = this.body;
                this.part = this.body = null;
                if (body) {
                    if (type === 'application/json') {
                        try { receiveState(body, version); } finally { bufferedBytes -= body.length; }
                    } else receiveFrame(body, version, id);
                }
            }
        }
    }

    function reset() {
        cancelRender(); clearImages(); stateVersion = 0; lastDraw = -Infinity;
        clearCanvases(); releaseContourRenderer();
    }
    async function connect() {
        clearTimeout(retry); if (!active) return;
        const connection = ++generation, abort = request = new AbortController();
        let reader, parser;
        try {
            const response = await fetch(preview ? '/web-stream?preview=1' : '/web-stream', { cache:'no-store', credentials:'same-origin', signal:abort.signal });
            const mime = response.headers.get('content-type') || '';
            const boundary = /boundary="?([a-zA-Z0-9_-]{1,70})"?(?:;|\s|$)/i.exec(mime);
            if (!response.ok || !response.body || !mime.toLowerCase().startsWith('multipart/x-mixed-replace') || !boundary) throw new Error('Unavailable stream');
            reader = response.body.getReader(); parser = new MultipartReader(boundary[1]);
            while (active && connection === generation) {
                const chunk = await reader.read(); if (chunk.done) break;
                if (!active || connection !== generation) break;
                parser.push(chunk.value);
            }
        } catch { /* Reconnect silently: this page is a clean capture surface. */ }
        finally {
            parser?.dispose(); abort.abort();
            if (reader) try { await reader.cancel(); } catch { }
            if (connection === generation) {
                generation++; reset();
                if (active) retry = setTimeout(connect, 1000);
            }
        }
    }
    function placement() { return { x:settings.screenX, y:settings.screenY, width:settings.screenWidth, height:settings.screenHeight }; }
    function updatePlacementEditor() {
        if (!placementEditor) return;
        const rect = placement();
        Object.assign(placementEditor.style, { left:`${rect.x}px`, top:`${rect.y}px`, width:`${rect.width}px`, height:`${rect.height}px` });
    }
    function setPlacement(rect) {
        previewSettings = settings = normalize({ ...settings, webEnabled:true,
            screenX:rect.x, screenY:rect.y, screenWidth:rect.width, screenHeight:rect.height });
        applyFilters();
        if (settings.ambilight && settings.ambilightStyle === 'Contours') scheduleRender();
        // Only the explicitly requested preview has a native bridge. The public
        // output never posts messages or contains editor controls.
        window.chrome?.webview?.postMessage({ type:'placement', ...placement() });
    }
    function pointerPosition(event) {
        const bounds = stage.getBoundingClientRect();
        return { x:(event.clientX - bounds.left) * 320 / bounds.width, y:(event.clientY - bounds.top) * 200 / bounds.height };
    }
    function resizePlacement(start, handle, dx, dy, keepAspect) {
        const clamp = (value, low, high) => Math.max(low, Math.min(high, value));
        let left = start.x, top = start.y, right = left + start.width, bottom = top + start.height;
        if (handle === 'move') return { ...start, x:clamp(left + dx, 0, 320 - start.width), y:clamp(top + dy, 0, 200 - start.height) };
        if (handle.includes('w')) left = clamp(left + dx, 0, right - 1);
        if (handle.includes('e')) right = clamp(right + dx, left + 1, 320);
        if (handle.includes('n')) top = clamp(top + dy, 0, bottom - 1);
        if (handle.includes('s')) bottom = clamp(bottom + dy, top + 1, 200);
        if (keepAspect && handle.length === 2) {
            const ratio = start.width / start.height;
            let width = right - left, height = bottom - top;
            if (Math.abs(width / start.width - 1) >= Math.abs(height / start.height - 1)) height = width / ratio;
            else width = height * ratio;
            const maximumWidth = handle.includes('w') ? start.x + start.width : 320 - start.x;
            const maximumHeight = handle.includes('n') ? start.y + start.height : 200 - start.y;
            const factor = Math.min(1, maximumWidth / width, maximumHeight / height);
            width = Math.max(Math.max(1, ratio), width * factor); height = width / ratio;
            left = handle.includes('w') ? start.x + start.width - width : start.x;
            top = handle.includes('n') ? start.y + start.height - height : start.y;
            right = left + width; bottom = top + height;
        }
        return { x:left, y:top, width:right - left, height:bottom - top };
    }
    function movePlacement(event) {
        if (!placementGesture || placementGesture.pointerId !== event.pointerId) return;
        const point = pointerPosition(event), gesture = placementGesture;
        setPlacement(resizePlacement(gesture.rect, gesture.handle, point.x - gesture.point.x, point.y - gesture.point.y, event.shiftKey));
        event.preventDefault(); event.stopPropagation();
    }
    function finishPlacement(cancel) {
        const gesture = placementGesture; if (!gesture) return;
        placementGesture = null;
        if (cancel) setPlacement(gesture.rect);
        if (placementEditor.hasPointerCapture(gesture.pointerId)) placementEditor.releasePointerCapture(gesture.pointerId);
    }
    function setupPreview() {
        placementEditor = document.createElement('div'); placementEditor.id = 'placementEditor'; placementEditor.tabIndex = 0;
        placementEditor.setAttribute('role', 'region'); placementEditor.setAttribute('aria-label', 'Output placement. Drag to move, handles to resize, arrow keys for precision, Escape to cancel.');
        const handles = { nw:[0,0], n:[50,0], ne:[100,0], e:[100,50], se:[100,100], s:[50,100], sw:[0,100], w:[0,50] };
        for (const [handle, [x,y]] of Object.entries(handles)) {
            const element = document.createElement('i'); element.dataset.handle = handle;
            element.style.left = `${x}%`; element.style.top = `${y}%`;
            element.style.cursor = `${handle === 'n' || handle === 's' ? 'ns' : handle === 'e' || handle === 'w' ? 'ew' : handle === 'nw' || handle === 'se' ? 'nwse' : 'nesw'}-resize`;
            placementEditor.appendChild(element);
        }
        stage.appendChild(placementEditor);
        placementEditor.addEventListener('pointerdown', event => {
            if (event.button !== 0 || placementGesture) return;
            placementEditor.focus({ preventScroll:true });
            placementGesture = { pointerId:event.pointerId, point:pointerPosition(event), rect:placement(), handle:event.target.dataset.handle || 'move' };
            placementEditor.setPointerCapture(event.pointerId); event.preventDefault(); event.stopPropagation();
        });
        placementEditor.addEventListener('pointermove', movePlacement);
        placementEditor.addEventListener('pointerup', event => {
            if (placementGesture?.pointerId !== event.pointerId) return;
            movePlacement(event); finishPlacement(false);
        });
        placementEditor.addEventListener('pointercancel', () => finishPlacement(true));
        placementEditor.addEventListener('lostpointercapture', () => finishPlacement(true));
        placementEditor.addEventListener('keydown', event => {
            if (event.key === 'Escape' && placementGesture) { finishPlacement(true); event.preventDefault(); event.stopPropagation(); return; }
            if (event.ctrlKey || event.metaKey || !['ArrowLeft','ArrowRight','ArrowUp','ArrowDown'].includes(event.key)) return;
            const step = event.shiftKey ? 10 : 1;
            const dx = event.key === 'ArrowLeft' ? -step : event.key === 'ArrowRight' ? step : 0;
            const dy = event.key === 'ArrowUp' ? -step : event.key === 'ArrowDown' ? step : 0;
            setPlacement(resizePlacement(placement(), 'move', dx, dy, false)); event.preventDefault(); event.stopPropagation();
        });
        window.setPreviewSettings = value => {
            if (!value || typeof value !== 'object') return;
            // The native bridge acknowledges pointer messages asynchronously.
            // Ignore an older rectangle while this gesture owns placement, but
            // still apply unrelated color/FPS changes immediately.
            const currentPlacement = placementGesture ? { screenX:settings.screenX, screenY:settings.screenY,
                screenWidth:settings.screenWidth, screenHeight:settings.screenHeight } : {};
            previewSettings = settings = normalize({ ...value, ...currentPlacement, webEnabled:true });
            cancelRender(); applyFilters(); scheduleRender();
        };
        // Placement is editable even before the first server state/image arrives.
        settings = normalize({ webEnabled:true }); updatePlacementEditor();
    }
    function resizeViewport() {
        finishPlacement(true);
        stage.style.transform = `scale(${Math.max(1, innerWidth) / 320},${Math.max(1, innerHeight) / 200})`;
    }
    addEventListener('resize', resizeViewport);
    addEventListener('pagehide', () => {
        finishPlacement(true);
        active = false; generation++; clearTimeout(retry); request?.abort(); reset();
    });
    addEventListener('pageshow', event => { if (event.persisted && !active) { active = true; resizeViewport(); connect(); } });
    if (preview) setupPreview();
    resizeViewport(); applyFilters(); connect();
})();
