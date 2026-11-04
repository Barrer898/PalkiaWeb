// PS3 XMB-style animated background: a deep gradient, a few slow translucent
// "ribbon" waves, and drifting dust motes. Purely cosmetic and self-contained:
// it injects its own <canvas> behind everything, so no Blazor component needs
// to know it exists, and it never touches the DOM that Blazor manages.
(function () {
    "use strict";

    // The real XMB tints itself by month (and by time of day, in the original
    // firmware). Each entry is [top, bottom, wave RGB]. Tweak freely.
    var PALETTES = [
        ["#05102a", "#1b4a8a", "90,160,255"],   // Jan
        ["#0a0f2e", "#3a3f9a", "140,150,255"],  // Feb
        ["#06201a", "#1f7a5a", "110,230,180"],  // Mar
        ["#0b2008", "#3f8a2a", "170,240,120"],  // Apr
        ["#06241f", "#1a8a8a", "110,235,230"],  // May
        ["#04162e", "#1a6aa8", "120,200,255"],  // Jun
        ["#2a0c0c", "#a8452a", "255,170,120"],  // Jul
        ["#2a1604", "#a8701a", "255,210,120"],  // Aug
        ["#2a1a04", "#8a5a1a", "255,200,130"],  // Sep
        ["#240a1e", "#8a2a6a", "240,140,210"],  // Oct
        ["#14102e", "#4a3a8a", "170,150,240"],  // Nov
        ["#2a0a14", "#8a1f3a", "255,140,170"]   // Dec
    ];

    var pal = PALETTES[new Date().getMonth()];
    var reduceMotion = window.matchMedia &&
        window.matchMedia("(prefers-reduced-motion: reduce)").matches;

    var canvas = document.createElement("canvas");
    canvas.id = "xmb-bg";
    canvas.setAttribute("aria-hidden", "true");
    document.body.insertBefore(canvas, document.body.firstChild);
    var ctx = canvas.getContext("2d");
    if (!ctx) { return; }

    var w = 0, h = 0, dpr = 1;
    var particles = [];

    function resize() {
        // Cap the pixel ratio: the effect is soft and blurry by nature, so
        // rendering at 4K-retina resolution would just waste GPU/CPU.
        dpr = Math.min(window.devicePixelRatio || 1, 1.5);
        w = window.innerWidth;
        h = window.innerHeight;
        canvas.width = Math.floor(w * dpr);
        canvas.height = Math.floor(h * dpr);
        canvas.style.width = w + "px";
        canvas.style.height = h + "px";
        ctx.setTransform(dpr, 0, 0, dpr, 0, 0);

        var count = Math.max(40, Math.min(110, Math.round((w * h) / 18000)));
        particles = [];
        for (var i = 0; i < count; i++) { particles.push(spawn(true)); }
    }

    function spawn(anywhere) {
        return {
            x: Math.random() * w,
            y: anywhere ? Math.random() * h : h + 10,
            r: 0.5 + Math.random() * 1.8,
            vx: (Math.random() - 0.3) * 0.18,
            vy: -(0.04 + Math.random() * 0.22),
            phase: Math.random() * Math.PI * 2,
            speed: 0.4 + Math.random() * 1.2
        };
    }

    // One ribbon: a filled band between two sine-sum curves, so it reads as a
    // twisting sheet of silk rather than a single line.
    var ribbons = [
        { base: 0.62, amp: 0.07, freq: 1.3, speed: 0.10, off: 0.0, width: 0.16, alpha: 0.16 },
        { base: 0.66, amp: 0.09, freq: 1.0, speed: 0.07, off: 2.1, width: 0.20, alpha: 0.12 },
        { base: 0.58, amp: 0.06, freq: 1.7, speed: 0.13, off: 4.2, width: 0.12, alpha: 0.10 },
        { base: 0.70, amp: 0.05, freq: 0.8, speed: 0.05, off: 1.0, width: 0.24, alpha: 0.08 }
    ];

    function curve(r, x, t, phaseShift) {
        var u = x / w;
        return h * (r.base +
            r.amp * Math.sin(u * Math.PI * 2 * r.freq + t * r.speed * 6 + r.off + phaseShift) +
            r.amp * 0.5 * Math.sin(u * Math.PI * 2 * r.freq * 2.3 - t * r.speed * 4 + r.off));
    }

    function draw(tSec) {
        var bg = ctx.createLinearGradient(0, 0, 0, h);
        bg.addColorStop(0, pal[0]);
        bg.addColorStop(1, pal[1]);
        ctx.globalCompositeOperation = "source-over";
        ctx.fillStyle = bg;
        ctx.fillRect(0, 0, w, h);

        // Additive blending makes overlapping ribbons glow where they cross.
        ctx.globalCompositeOperation = "lighter";
        var step = 10;
        for (var i = 0; i < ribbons.length; i++) {
            var r = ribbons[i];
            var thick = h * r.width;
            ctx.beginPath();
            for (var x = 0; x <= w + step; x += step) {
                ctx.lineTo(x, curve(r, x, tSec, 0));
            }
            for (var x2 = w + step; x2 >= 0; x2 -= step) {
                // The lower edge is the same curve, phase-shifted and offset,
                // which is what gives the ribbon its twisting look.
                ctx.lineTo(x2, curve(r, x2, tSec, 0.9) + thick);
            }
            ctx.closePath();
            var g = ctx.createLinearGradient(0, h * (r.base - r.amp), 0, h * (r.base + r.amp) + thick);
            g.addColorStop(0, "rgba(" + pal[2] + ",0)");
            g.addColorStop(0.5, "rgba(" + pal[2] + "," + r.alpha + ")");
            g.addColorStop(1, "rgba(" + pal[2] + ",0)");
            ctx.fillStyle = g;
            ctx.fill();

            // Thin bright rim on the top edge, like the XMB's silk highlight.
            ctx.beginPath();
            for (var x3 = 0; x3 <= w + step; x3 += step) {
                ctx.lineTo(x3, curve(r, x3, tSec, 0));
            }
            ctx.strokeStyle = "rgba(" + pal[2] + "," + (r.alpha * 1.6) + ")";
            ctx.lineWidth = 1.2;
            ctx.stroke();
        }

        // Dust motes: slow upward drift with a gentle twinkle.
        for (var p = 0; p < particles.length; p++) {
            var d = particles[p];
            if (!reduceMotion) {
                d.x += d.vx + Math.sin(tSec * 0.3 + d.phase) * 0.06;
                d.y += d.vy;
                if (d.y < -10 || d.x < -10 || d.x > w + 10) {
                    particles[p] = d = spawn(false);
                }
            }
            var tw = 0.35 + 0.65 * (0.5 + 0.5 * Math.sin(tSec * d.speed + d.phase));
            ctx.beginPath();
            ctx.arc(d.x, d.y, d.r, 0, Math.PI * 2);
            ctx.fillStyle = "rgba(255,255,255," + (0.55 * tw).toFixed(3) + ")";
            ctx.fill();
            // Soft halo so larger motes look out-of-focus, as in the original.
            if (d.r > 1.2) {
                ctx.beginPath();
                ctx.arc(d.x, d.y, d.r * 3, 0, Math.PI * 2);
                ctx.fillStyle = "rgba(" + pal[2] + "," + (0.07 * tw).toFixed(3) + ")";
                ctx.fill();
            }
        }
    }

    resize();
    var resizeTimer = 0;
    window.addEventListener("resize", function () {
        clearTimeout(resizeTimer);
        resizeTimer = setTimeout(resize, 150);
    });

    if (reduceMotion) {
        draw(0);   // single static frame for people who asked for less motion
        return;
    }

    // ~30fps is plenty for slow ambient motion and keeps CPU use low.
    var last = 0;
    function frame(ms) {
        if (ms - last >= 33) {
            last = ms;
            draw(ms / 1000);
        }
        requestAnimationFrame(frame);
    }
    requestAnimationFrame(frame);
})();
