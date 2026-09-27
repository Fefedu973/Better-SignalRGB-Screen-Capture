/* Shared by the web output and the standalone SignalRGB effect. No frame loop or transport. */
(() => {
    'use strict';
    const WIDTH = 320, HEIGHT = 200, MAX_MASK_WIDTH = 320, MAX_MASK_HEIGHT = 320;
    const number = (value, fallback, min, max) => Number.isFinite(value) ? Math.max(min, Math.min(max, value)) : fallback;

    function parameters(value) {
        value = value || {};
        const width = number(value.screenWidth, WIDTH, 1, WIDTH), height = number(value.screenHeight, HEIGHT, 1, HEIGHT);
        return {
            // SignalRGB also permits a picture partially beyond the right/bottom edge.
            // The app constrains its own editor; this renderer must retain the host placement.
            x:number(value.screenX, 0, 0, WIDTH - 1), y:number(value.screenY, 0, 0, HEIGHT - 1), width, height,
            depth:number(value.ambilightEdgeDepth, 3, 1, 20) / 100,
            mix:number(value.ambilightEdgeMix, 2, 0, 30) / 100,
            reach:number(value.ambilightEdgeReach, 60, 1, 200), fade:number(value.ambilightEdgeFade, 50, 0, 100) / 100,
            saturation:number(value.ambilightSaturation, 3, 0, 10), intensity:number(value.ambilightIntensity, 100, 0, 200) / 100,
            cutoff:number(value.ambilightCutoff, 0, 0, 100) / 100, fullscreen:value.ambilightFullscreen === true
        };
    }

    // Exact squared Euclidean distance transform, with deterministic ties. Its seeds are
    // opaque boundary pixels of the composed silhouette, never luminance-dependent pixels.
    // First solve each row, then the lower envelope of parabolas in each column: O(w*h).
    function nearestBoundary(alpha, width, height) {
        const length = width * height, rowDistance = new Float32Array(length), rowSeed = new Int16Array(length);
        const nearest = new Int32Array(length); nearest.fill(-1);
        const seeds = new Uint8Array(length);
        let seedCount = 0;
        for (let y = 0; y < height; y++) for (let x = 0; x < width; x++) {
            const i = y * width + x;
            if (alpha[i] && (!x || !y || x === width - 1 || y === height - 1 ||
                !alpha[i - 1] || !alpha[i + 1] || !alpha[i - width] || !alpha[i + width])) { seeds[i] = 1; seedCount++; }
        }
        if (!seedCount) return nearest;
        for (let y = 0; y < height; y++) {
            let previous = -100000;
            for (let x = 0; x < width; x++) {
                const i = y * width + x;
                if (seeds[i]) previous = x;
                rowDistance[i] = (x - previous) ** 2; rowSeed[i] = previous >= 0 ? previous : -1;
            }
            previous = 100000;
            for (let x = width - 1; x >= 0; x--) {
                const i = y * width + x;
                if (seeds[i]) previous = x;
                const distance = (x - previous) ** 2;
                if (distance < rowDistance[i]) { rowDistance[i] = distance; rowSeed[i] = previous < width ? previous : -1; }
            }
        }
        const sites = new Int16Array(height), intersections = new Float64Array(height + 1);
        for (let x = 0; x < width; x++) {
            let end = -1;
            for (let y = 0; y < height; y++) {
                if (rowSeed[y * width + x] < 0) continue;
                let intersection = -Infinity;
                while (end >= 0) {
                    const previous = sites[end];
                    intersection = ((rowDistance[y * width + x] + y * y) -
                        (rowDistance[previous * width + x] + previous * previous)) / (2 * (y - previous));
                    if (intersection > intersections[end]) break;
                    end--;
                }
                sites[++end] = y; intersections[end] = end ? intersection : -Infinity; intersections[end + 1] = Infinity;
            }
            let segment = 0;
            for (let y = 0; y < height; y++) {
                while (segment < end && intersections[segment + 1] < y) segment++;
                const row = sites[segment]; nearest[y * width + x] = row * width + rowSeed[row * width + x];
            }
        }
        return nearest;
    }

    const vertexShader = `attribute vec2 position; varying vec2 uv;
        void main(){uv=vec2((position.x+1.0)*0.5,(1.0-position.y)*0.5);gl_Position=vec4(position,0.0,1.0);}`;
    const fragmentShader = `precision highp float;
        varying vec2 uv;
        uniform sampler2D sourceImage, boundary, silhouette, samplingLimits;
        uniform vec2 fieldSize, metricSize;
        uniform vec4 placement;
        uniform float depth, lateral, reach, fade, saturation, intensity, cutoff;
        vec2 seed(vec2 point){vec4 b=texture2D(boundary,point);return vec2(b.r*65280.0+b.g*255.0,b.b*65280.0+b.a*255.0)/65535.0;}
        void main(){
            float outside=1.0-texture2D(silhouette,uv).a;
            if(outside<0.004){gl_FragColor=vec4(0.0);return;}
            vec2 q=seed(uv), delta=(uv-q)*metricSize;
            float distance=length(delta);
            if(distance>=reach){gl_FragColor=vec4(0.0);return;}
            vec2 cellDelta=((floor(uv*fieldSize)+0.5)/fieldSize-q)*metricSize;
            float cellDistance=length(cellDelta);
            vec2 inward=cellDistance>0.01 ? -cellDelta/cellDistance : vec2(0.0,1.0);
            vec2 tangent=vec2(-inward.y,inward.x);
            vec3 limits=texture2D(samplingLimits,uv).rgb*255.0;
            vec3 sum=vec3(0.0);float alpha=0.0;float samples=0.0;
            for(int side=-1;side<=1;side++){
                if(lateral<0.001 && side!=0)continue;
                float encodedLimit=side<0?limits.r:(side==0?limits.g:limits.b);
                if(encodedLimit<0.5)continue;
                float bandDepth=max(0.0,(encodedLimit-1.0)/254.0)*depth;
                for(int band=0;band<4;band++){
                    vec2 point=q+(inward*(float(band)*bandDepth/3.0)+tangent*float(side)*lateral)/metricSize;
                    if(point.x<0.0||point.y<0.0||point.x>1.0||point.y>1.0)break;
                    if(texture2D(silhouette,point).a<0.05)break;
                    vec2 sourcePoint=(point-placement.xy)/placement.zw;
                    vec4 color=texture2D(sourceImage,clamp(sourcePoint,0.0,1.0));
                    float luminance=dot(color.rgb,vec3(0.2126,0.7152,0.0722));
                    float contribution=color.a*(cutoff>0.0?clamp(5.0*(luminance-cutoff),0.0,1.0):1.0);
                    sum+=color.rgb*contribution;alpha+=contribution;samples+=1.0;
                }
            }
            if(alpha<0.0001||samples<0.5){gl_FragColor=vec4(0.0);return;}
            vec3 color=sum/alpha;float luminance=dot(color,vec3(0.2126,0.7152,0.0722));
            color=clamp(mix(vec3(luminance),color,saturation)*intensity,0.0,1.0);
            float t=clamp(distance/reach,0.0,1.0);
            float falloff=fade<0.0001?1.0:pow(1.0-t,fade*4.0);
            gl_FragColor=vec4(color,alpha/samples*outside*falloff);
        }`;

    function create(outputCanvas, options = {}) {
        if (!outputCanvas || typeof outputCanvas.getContext !== 'function') throw new TypeError('A canvas is required.');
        const output = outputCanvas.getContext('2d');
        if (!output) throw new Error('The halo output requires a 2D canvas.');
        const maskCanvas = document.createElement('canvas'), mask = maskCanvas.getContext('2d', {willReadFrequently:true});
        const cpuCanvas = document.createElement('canvas'), cpu = cpuCanvas.getContext('2d', {willReadFrequently:true});
        const gpuCanvas = document.createElement('canvas');
        let geometry = null, gpu = null, ownedGl = null, gpuUnavailable = options.forceCanvas2D === true, lost = false, disposed = false;
        let geometryBuilds = 0, renderCount = 0, fallbackCount = 0, backend = 'none', fallbackReason = options.forceCanvas2D ? 'forced' : null;
        const onLost = event => { event.preventDefault(); lost = true; gpu = null; };
        const onRestored = () => { lost = false; gpuUnavailable = options.forceCanvas2D === true; gpu = null; };
        gpuCanvas.addEventListener('webglcontextlost', onLost);
        gpuCanvas.addEventListener('webglcontextrestored', onRestored);

        function buildGeometry(drawMask, key, p, width, height) {
            const signature = `${width},${height},${p.x},${p.y},${p.width},${p.height}`;
            if (geometry && geometry.key === key && geometry.signature === signature) return;
            // Preserve the output aspect ratio so nearest-edge projection remains orthogonal
            // even when the 320x200 scene is stretched to the 800x600 HQ output.
            const w = Math.min(MAX_MASK_WIDTH, width), h = Math.min(MAX_MASK_HEIGHT, Math.max(1, Math.round(w * height / width)));
            maskCanvas.width = w; maskCanvas.height = h;
            mask.save();
            mask.setTransform(w / WIDTH, 0, 0, h / HEIGHT, 0, 0);
            mask.translate(p.x, p.y); mask.scale(p.width / WIDTH, p.height / HEIGHT);
            mask.fillStyle = '#fff';
            try { drawMask(mask); } finally { mask.restore(); }
            const image = mask.getImageData(0, 0, w, h), alpha = new Uint8Array(w * h);
            let count = 0, allOpaque = true;
            for (let i = 0; i < alpha.length; i++) {
                const coverage = image.data[i * 4 + 3];
                // The 50% coverage contour avoids treating a faint resampling fringe as
                // an interior color-sampling seed. Keep original AA for the output alpha.
                alpha[i] = coverage >= 128 ? 1 : 0; count += alpha[i];
                if (coverage !== 255) allOpaque = false;
            }
            const nearest = allOpaque ? new Int32Array(w * h).fill(-1) : nearestBoundary(alpha, w, h);
            const packed = new Uint8Array(w * h * 4);
            const distance = new Float32Array(w * h), normalX = new Float32Array(w * h), normalY = new Float32Array(w * h);
            const metricWidth = WIDTH, metricHeight = WIDTH * height / width;
            for (let i = 0; i < nearest.length; i++) {
                const seed = nearest[i];
                if (seed < 0) continue;
                const qx = seed % w, qy = Math.floor(seed / w);
                const encodedX = Math.round((qx + .5) / w * 65535), encodedY = Math.round((qy + .5) / h * 65535);
                packed[i * 4] = encodedX >> 8; packed[i * 4 + 1] = encodedX & 255;
                packed[i * 4 + 2] = encodedY >> 8; packed[i * 4 + 3] = encodedY & 255;
                const dx = (qx - i % w) * metricWidth / w, dy = (qy - Math.floor(i / w)) * metricHeight / h;
                const d = Math.hypot(dx, dy); distance[i] = d;
                normalX[i] = d ? dx / d : 0; normalY[i] = d ? dy / d : 1;
            }
            geometry = { key, signature, w, h, image, alpha, nearest, packed, distance, normalX, normalY, count, allOpaque, metricWidth, metricHeight };
            geometryBuilds++;
            if (gpu) gpu.geometry = null;
        }

        function buildSamplingLimits(depth, lateral) {
            const g = geometry, signature = `${depth},${lateral}`;
            if (g.samplingSignature === signature) return;
            const limits = new Uint8Array(g.w * g.h * 4), stepX = g.w / g.metricWidth, stepY = g.h / g.metricHeight;
            // Traverse EVERY crossed mask cell, rather than checking only the four color
            // taps. A narrow gap must terminate the band before an unrelated source beyond
            // it, even when the requested band depth is much larger than that first source.
            function continuousDistance(x, y, dx, dy, maximum) {
                let cellX = Math.floor(x), cellY = Math.floor(y);
                if (cellX < 0 || cellY < 0 || cellX >= g.w || cellY >= g.h || !g.alpha[cellY * g.w + cellX]) return -1;
                const incrementX = dx > 0 ? 1 : -1, incrementY = dy > 0 ? 1 : -1;
                const deltaX = Math.abs(dx) < 1e-9 ? Infinity : Math.abs(1 / dx);
                const deltaY = Math.abs(dy) < 1e-9 ? Infinity : Math.abs(1 / dy);
                let nextX = Number.isFinite(deltaX) ? (dx > 0 ? cellX + 1 - x : x - cellX) * deltaX : Infinity;
                let nextY = Number.isFinite(deltaY) ? (dy > 0 ? cellY + 1 - y : y - cellY) * deltaY : Infinity;
                for (let steps = 0; steps <= g.w + g.h; steps++) {
                    const next = Math.min(nextX, nextY);
                    if (next >= maximum) return maximum;
                    if (nextX <= next) { cellX += incrementX; nextX += deltaX; }
                    if (nextY <= next) { cellY += incrementY; nextY += deltaY; }
                    if (cellX < 0 || cellY < 0 || cellX >= g.w || cellY >= g.h || !g.alpha[cellY * g.w + cellX])
                        return Math.max(0, next - .03); // Conservative against packed-coordinate rounding.
                }
                return 0;
            }
            for (let i = 0; i < g.nearest.length; i++) {
                const seed = g.nearest[i];
                if (seed < 0 || g.image.data[i * 4 + 3] === 255) continue;
                const qx = seed % g.w + .5, qy = Math.floor(seed / g.w) + .5, nx = g.normalX[i], ny = g.normalY[i];
                for (let side = lateral < .001 ? 0 : -1; side <= (lateral < .001 ? 0 : 1); side++) {
                    const tx = -ny * side * stepX, ty = nx * side * stepY;
                    if (side && continuousDistance(qx, qy, tx, ty, lateral) < lateral) continue;
                    const distance = continuousDistance(qx + tx * lateral, qy + ty * lateral, nx * stepX, ny * stepY, depth);
                    if (distance >= 0) limits[i * 4 + side + 1] = 1 + Math.floor(Math.min(1, distance / depth) * 254);
                }
            }
            g.samplingSignature = signature; g.limits = limits;
        }

        function shader(gl, type, source) {
            const result = gl.createShader(type); gl.shaderSource(result, source); gl.compileShader(result);
            if (!gl.getShaderParameter(result, gl.COMPILE_STATUS)) { const message = gl.getShaderInfoLog(result); gl.deleteShader(result); throw new Error(message || 'Halo shader compilation failed.'); }
            return result;
        }
        function initializeGpu() {
            if (gpu || gpuUnavailable || lost) return;
            const gl = gpuCanvas.getContext('webgl', {alpha:true, premultipliedAlpha:false, antialias:false, depth:false, stencil:false, preserveDrawingBuffer:false});
            if (!gl) throw new Error('WebGL is unavailable.');
            ownedGl = gl;
            // Software WebGL adds a full-frame transfer to software rasterization. The
            // bounded CPU renderer avoids that cost; never benchmark or finish() on a live
            // frame to make this choice. The override exists for deterministic GPU tests.
            const info = gl.getExtension('WEBGL_debug_renderer_info');
            const renderer = info ? String(gl.getParameter(info.UNMASKED_RENDERER_WEBGL)) : '';
            if (!options.forceWebGL && /swiftshader|llvmpipe|softpipe|software rasterizer|microsoft basic render/i.test(renderer)) {
                gpuUnavailable = true; fallbackReason = 'software-renderer';
                gl.getExtension('WEBGL_lose_context')?.loseContext();
                return;
            }
            const program = gl.createProgram();
            let vertex = null, fragment = null;
            try {
                vertex = shader(gl, gl.VERTEX_SHADER, vertexShader); fragment = shader(gl, gl.FRAGMENT_SHADER, fragmentShader);
                gl.attachShader(program, vertex); gl.attachShader(program, fragment); gl.linkProgram(program);
                if (!gl.getProgramParameter(program, gl.LINK_STATUS)) throw new Error('Halo shader linking failed.');
            } catch (error) { gl.deleteProgram(program); throw error; }
            finally { if (vertex) gl.deleteShader(vertex); if (fragment) gl.deleteShader(fragment); }
            const buffer = gl.createBuffer(); gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
            gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1,-1,1,-1,-1,1,1,1]), gl.STATIC_DRAW);
            const textures = Array.from({length:4}, () => {
                const texture = gl.createTexture(); gl.bindTexture(gl.TEXTURE_2D, texture);
                gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE); gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
                gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR); gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
                return texture;
            });
            const uniforms = {};
            for (const name of ['sourceImage','boundary','silhouette','samplingLimits','fieldSize','metricSize','placement','depth','lateral','reach','fade','saturation','intensity','cutoff']) uniforms[name] = gl.getUniformLocation(program, name);
            gpu = {gl, program, buffer, textures, uniforms, position:gl.getAttribLocation(program, 'position'), geometry:null, limits:null, frameWidth:0, frameHeight:0, validated:false};
        }
        function releaseGpu() {
            if (!gpu) return;
            const {gl, program, buffer, textures} = gpu;
            if (!gl.isContextLost()) { textures.forEach(texture => gl.deleteTexture(texture)); gl.deleteBuffer(buffer); gl.deleteProgram(program); }
            gpu = null;
        }
        function renderGpu(frame, p, reach, depth, lateral) {
            initializeGpu();
            if (!gpu || lost) return false;
            const {gl, program, buffer, textures, uniforms:u} = gpu, g = geometry;
            if (gpuCanvas.width !== outputCanvas.width || gpuCanvas.height !== outputCanvas.height) { gpuCanvas.width = outputCanvas.width; gpuCanvas.height = outputCanvas.height; }
            gl.viewport(0, 0, gpuCanvas.width, gpuCanvas.height); gl.useProgram(program);
            gl.bindBuffer(gl.ARRAY_BUFFER, buffer); gl.enableVertexAttribArray(gpu.position); gl.vertexAttribPointer(gpu.position, 2, gl.FLOAT, false, 0, 0);
            gl.activeTexture(gl.TEXTURE0); gl.bindTexture(gl.TEXTURE_2D, textures[0]);
            gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, false); gl.pixelStorei(gl.UNPACK_PREMULTIPLY_ALPHA_WEBGL, false);
            if (gpu.frameWidth === frame.width && gpu.frameHeight === frame.height)
                gl.texSubImage2D(gl.TEXTURE_2D, 0, 0, 0, gl.RGBA, gl.UNSIGNED_BYTE, frame);
            else {
                gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, frame);
                gpu.frameWidth = frame.width; gpu.frameHeight = frame.height;
            }
            gl.uniform1i(u.sourceImage, 0);
            gl.activeTexture(gl.TEXTURE1); gl.bindTexture(gl.TEXTURE_2D, textures[1]);
            if (gpu.geometry !== g) {
                gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST); gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
                gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, g.w, g.h, 0, gl.RGBA, gl.UNSIGNED_BYTE, g.packed);
            }
            gl.uniform1i(u.boundary, 1);
            gl.activeTexture(gl.TEXTURE2); gl.bindTexture(gl.TEXTURE_2D, textures[2]);
            if (gpu.geometry !== g) gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, maskCanvas);
            gl.uniform1i(u.silhouette, 2); gpu.geometry = g;
            gl.activeTexture(gl.TEXTURE3); gl.bindTexture(gl.TEXTURE_2D, textures[3]);
            if (gpu.limits !== g.limits) {
                gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST); gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
                gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, g.w, g.h, 0, gl.RGBA, gl.UNSIGNED_BYTE, g.limits); gpu.limits = g.limits;
            }
            gl.uniform1i(u.samplingLimits, 3);
            gl.uniform2f(u.fieldSize, g.w, g.h); gl.uniform2f(u.metricSize, g.metricWidth, g.metricHeight);
            gl.uniform4f(u.placement, p.x / WIDTH, p.y / HEIGHT, p.width / WIDTH, p.height / HEIGHT);
            for (const [name, value] of Object.entries({depth, lateral, reach, fade:p.fade, saturation:p.saturation, intensity:p.intensity, cutoff:p.cutoff})) gl.uniform1f(u[name], value);
            gl.disable(gl.BLEND); gl.drawArrays(gl.TRIANGLE_STRIP, 0, 4);
            // getError can synchronize the GPU. Validate initial setup only; context-loss
            // detection remains non-blocking on every subsequent frame.
            if (gl.isContextLost() || (!gpu.validated && gl.getError() !== gl.NO_ERROR)) throw new Error('Halo GPU rendering failed.');
            gpu.validated = true;
            output.drawImage(gpuCanvas, 0, 0); return true;
        }

        function renderCpu(frame, p, reach, depth, lateral) {
            const g = geometry, w = g.w, h = g.h;
            if (cpuCanvas.width !== w || cpuCanvas.height !== h) { cpuCanvas.width = w; cpuCanvas.height = h; }
            cpu.clearRect(0, 0, w, h);
            cpu.drawImage(frame, p.x / WIDTH * w, p.y / HEIGHT * h, p.width / WIDTH * w, p.height / HEIGHT * h);
            const pixels = cpu.getImageData(0, 0, w, h).data, image = cpu.createImageData(w, h), result = image.data;
            const stepX = w / g.metricWidth, stepY = h / g.metricHeight;
            for (let i = 0; i < g.nearest.length; i++) {
                const outside = 1 - g.image.data[i * 4 + 3] / 255, distance = g.distance[i], seed = g.nearest[i];
                if (outside < .004 || distance >= reach || seed < 0) continue;
                const qx = seed % w, qy = Math.floor(seed / w), nx = g.normalX[i], ny = g.normalY[i];
                let red = 0, green = 0, blue = 0, alpha = 0, samples = 0;
                for (let side = lateral < .001 ? 0 : -1; side <= (lateral < .001 ? 0 : 1); side++) {
                    const encodedLimit = g.limits[i * 4 + side + 1];
                    if (!encodedLimit) continue;
                    const bandDepth = (encodedLimit - 1) / 254 * depth;
                    for (let band = 0; band < 4; band++) {
                    const x = Math.round(qx + (nx * band * bandDepth / 3 - ny * side * lateral) * stepX);
                    const y = Math.round(qy + (ny * band * bandDepth / 3 + nx * side * lateral) * stepY);
                    if (x < 0 || y < 0 || x >= w || y >= h || !g.alpha[y * w + x]) break;
                    const offset = (y * w + x) * 4;
                    const r = pixels[offset] / 255, greenValue = pixels[offset + 1] / 255, b = pixels[offset + 2] / 255;
                    const luminance = .2126 * r + .7152 * greenValue + .0722 * b;
                    const contribution = pixels[offset + 3] / 255 * (p.cutoff ? Math.max(0, Math.min(1, 5 * (luminance - p.cutoff))) : 1);
                    red += r * contribution; green += greenValue * contribution; blue += b * contribution; alpha += contribution; samples++;
                    }
                }
                if (alpha < .0001 || !samples) continue;
                red /= alpha; green /= alpha; blue /= alpha;
                const luminance = .2126 * red + .7152 * green + .0722 * blue;
                result[i * 4] = Math.max(0, Math.min(1, (luminance + (red - luminance) * p.saturation) * p.intensity)) * 255;
                result[i * 4 + 1] = Math.max(0, Math.min(1, (luminance + (green - luminance) * p.saturation) * p.intensity)) * 255;
                result[i * 4 + 2] = Math.max(0, Math.min(1, (luminance + (blue - luminance) * p.saturation) * p.intensity)) * 255;
                result[i * 4 + 3] = alpha / samples * outside * (p.fade ? (1 - distance / reach) ** (p.fade * 4) : 1) * 255;
            }
            cpu.putImageData(image, 0, 0); output.drawImage(cpuCanvas, 0, 0, outputCanvas.width, outputCanvas.height);
        }

        const api = {
            render(frame, drawMask, key, settings) {
                if (disposed) return;
                output.clearRect(0, 0, outputCanvas.width, outputCanvas.height);
                if (!frame || !frame.width || !frame.height || typeof drawMask !== 'function' || !outputCanvas.width || !outputCanvas.height) return;
                const p = parameters(settings);
                buildGeometry(drawMask, key, p, outputCanvas.width, outputCanvas.height);
                if (!geometry.count || geometry.allOpaque || !p.intensity) return;
                const size = Math.min(p.width, p.height / HEIGHT * geometry.metricHeight);
                const depth = Math.max(.5, size * p.depth), lateral = size * p.mix;
                const reach = p.fullscreen ? Math.hypot(geometry.metricWidth, geometry.metricHeight) : p.reach;
                buildSamplingLimits(depth, lateral);
                let rendered = false;
                if (!gpuUnavailable && !lost) {
                    try { rendered = renderGpu(frame, p, reach, depth, lateral); }
                    catch (_) { releaseGpu(); gpuUnavailable = true; fallbackReason = lost || ownedGl?.isContextLost() ? 'context-lost' : 'gpu-unavailable'; }
                }
                if (rendered) { backend = 'webgl'; fallbackReason = null; }
                else {
                    if (backend !== 'canvas2d') fallbackCount++;
                    backend = 'canvas2d';
                    if (lost && fallbackReason !== 'software-renderer') fallbackReason = 'context-lost';
                    renderCpu(frame, p, reach, depth, lateral);
                }
                renderCount++;
            },
            clear() { if (!disposed) output.clearRect(0, 0, outputCanvas.width, outputCanvas.height); },
            dispose() {
                if (disposed) return;
                api.clear(); releaseGpu(); geometry = null; disposed = true;
                gpuCanvas.removeEventListener('webglcontextlost', onLost); gpuCanvas.removeEventListener('webglcontextrestored', onRestored);
                // This context belongs solely to the engine. Release its browser/GPU slot
                // immediately when toggling styles rather than waiting for garbage collection.
                if (ownedGl && !ownedGl.isContextLost()) ownedGl.getExtension('WEBGL_lose_context')?.loseContext();
                ownedGl = null;
                for (const canvas of [gpuCanvas, maskCanvas, cpuCanvas]) { canvas.width = 0; canvas.height = 0; }
            },
            get diagnostics() {
                const pixels = geometry ? geometry.w * geometry.h : 0;
                return Object.freeze({backend, fallbackReason, geometryBuilds, geomBuilds:geometryBuilds, renderCount, fallbackCount, pixels,
                    maskWidth:geometry ? geometry.w : 0, maskHeight:geometry ? geometry.h : 0,
                    estimatedBytes:disposed ? 0 : pixels * 41 + outputCanvas.width * outputCanvas.height * (gpu ? 12 : 4), disposed});
            }
        };
        return api;
    }
    window.ContourHalo = Object.freeze({ create });
})();
