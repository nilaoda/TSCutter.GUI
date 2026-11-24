"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const { test } = require("node:test");
const script = fs.readFileSync(path.join(__dirname, "../src/TSCutter.GUI/Web/Client/video-playback.js"), "utf8");
const h264 = 'video/mp4; codecs="avc1.42C034"';

function player(sources) {
    const document = new EventTarget();
    document.hidden = false;
    const urls = [];
    const context = vm.createContext({ window: sources, MediaSource: sources.MediaSource, document, performance,
        setInterval, clearInterval, URL: {
            createObjectURL(source) { urls.push(source); return "blob:stream"; },
            revokeObjectURL() { urls.length = 0; }
        } });
    const Playback = vm.runInContext(script + "\nVideoPlayback;", context);
    const video = { dataset: {}, disableRemotePlayback: false, playbackRate: 1,
        play: async () => {}, removeAttribute() {}, load() {} };
    return { Playback, video, urls };
}

function sourceFor(supported) {
    return class extends EventTarget {
        static isTypeSupported(mime) { return supported.includes(mime); }
    };
}

test("普通 MSE 可用时继续使用，关闭播放器后释放对象 URL", () => {
    const classic = sourceFor([h264]);
    const managed = sourceFor([h264]);
    const { Playback, video, urls } = player({ MediaSource: classic, ManagedMediaSource: managed });
    assert.equal(Playback.supportedMime("h264"), h264);
    const playback = new Playback(video, h264, () => {}, () => {});
    try {
        assert.ok(playback.source instanceof classic);
        assert.equal(video.src, "blob:stream");
    } finally { playback.close(); }
    assert.equal(urls.length, 0);
});

test("iPhone 仅提供 ManagedMediaSource 时可检测并创建，挂载前关闭 AirPlay", () => {
    for (const classic of [undefined, sourceFor([])]) {
        const sources = { MediaSource: classic };
        const { Playback, video, urls } = player(sources);
        class Managed extends EventTarget {
            static isTypeSupported(mime) { return mime === h264; }
            constructor() {
                super();
                assert.equal(video.disableRemotePlayback, true);
                assert.equal(video.src, undefined);
            }
        }
        sources.ManagedMediaSource = Managed;
        assert.equal(Playback.supportedMime("h264"), h264);
        const playback = new Playback(video, h264, () => {}, () => {});
        try { assert.ok(playback.source instanceof Managed); }
        finally { playback.close(); }
        assert.equal(urls.length, 0);
    }
});

test("没有媒体源或两种源均不支持时保留不支持结果，不误报 AV1 可用", () => {
    for (const sources of [{}, { MediaSource: sourceFor([]), ManagedMediaSource: sourceFor([h264]) }]) {
        const { Playback, video } = player(sources);
        assert.equal(Playback.supportedMime("av1"), undefined);
        if (!sources.ManagedMediaSource) {
            assert.equal(Playback.supportedMime("h264"), undefined);
            assert.throws(() => new Playback(video, h264, () => {}, () => {}), /Unsupported media source/);
        }
    }
});
