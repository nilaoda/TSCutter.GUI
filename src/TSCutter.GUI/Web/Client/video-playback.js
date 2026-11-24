"use strict";

class VideoPlayback {
    static mediaSourceFor(mime) {
        // iPhone Safari 使用 ManagedMediaSource；检测和创建必须选择同一个实现。
        return [window.MediaSource, window.ManagedMediaSource]
            .find(source => typeof source === "function" && source.isTypeSupported?.(mime));
    }

    static supportedMime(codec) {
        if (codec === "h264") {
            const mime = 'video/mp4; codecs="avc1.42C034"';
            return this.mediaSourceFor(mime) ? mime : undefined;
        }
        return ['video/mp4; codecs="av01.0.13M.08"', 'video/mp4; codecs="av01.0.08M.08"']
            .find(mime => this.mediaSourceFor(mime));
    }

    constructor(video, mime, onReady, onError, reportPlayback) {
        this.video = video;
        this.onError = onError;
        this.interactiveUntil = 0;
        this.queue = [];
        this.queuedBytes = 0;
        const sourceType = VideoPlayback.mediaSourceFor(mime);
        if (!sourceType)
            throw new Error("Unsupported media source: " + mime);
        // Safari 仅在禁用远程播放或提供 AirPlay 替代源时启用 ManagedMediaSource。
        video.disableRemotePlayback = true;
        this.source = new sourceType();
        this.url = URL.createObjectURL(this.source);
        this.closed = false;
        this.reportPlayback = reportPlayback;
        this.appendedFrames = [];
        this.keyFrames = [];
        this.lastReport = -Infinity;
        this.lastBufferedEnd = 0;
        this.lastSequence = 0;
        this.presentedFrames = 0;
        if (video.requestVideoFrameCallback) {
            const onFrame = (now, frame) => {
                if (this.closed)
                    return;
                this.frameCallback = video.requestVideoFrameCallback(onFrame);
                this.reportFrame(now, frame);
            };
            this.frameCallback = video.requestVideoFrameCallback(onFrame);
        }
        video.onloadeddata = () => {
            if (this.closed || video.readyState < 2 || !video.videoWidth)
                return;
            this.ready = true;
            onReady();
        };
        video.onplaying = video.onloadeddata;
        video.onerror = () => this.fail("videoReasonDecoder", video.error);
        this.source.addEventListener("sourceopen", () => this.open(mime), { once: true });
        // 媒体源可能失效而不触发 video.error，此时不能继续把空的视频元素当作正常画面。
        this.source.addEventListener("sourceclose", () => this.fail("videoReasonSource"));
        video.dataset.pending = "";
        video.hidden = false;
        video.src = this.url;
        void video.play().catch(error => this.fail("videoReasonPlaybackBlocked", error));
        // 没有新画面、后台恢复或缺少逐帧回调时，也必须继续回执，避免发送预算停住。
        this.progressTimer = setInterval(() => this.reportProgress(performance.now()), 100);
        this.visibilityChanged = () => {
            this.presentation = this.stalledSince = undefined;
            if (!document.hidden)
                this.visibleSince = performance.now();
            this.reportProgress(performance.now(), true);
        };
        document.addEventListener("visibilitychange", this.visibilityChanged);
    }

    open(mime) {
        if (this.closed)
            return;
        try {
            this.buffer = this.source.addSourceBuffer(mime);
            this.buffer.addEventListener("error", () => this.fail("videoReasonBuffer"));
            this.buffer.addEventListener("updateend", () => {
                if (this.closed)
                    return;
                if (this.appendedFrames && this.appendingArrival !== undefined) {
                    const ranges = this.buffer.buffered;
                    const end = ranges.length ? ranges.end(ranges.length - 1) : 0;
                    if (end > this.lastBufferedEnd) {
                        if (this.appendingKeyFrame)
                            this.keyFrames.push(this.appendingStart);
                        this.appendedFrames.push({ start: this.appendingStart, end, arrival: this.appendingArrival });
                        if (this.appendedFrames.length > 120)
                            this.appendedFrames.shift();
                        this.lastBufferedEnd = end;
                    }
                }
                if (this.appendingSequence !== undefined)
                    this.lastSequence = this.appendingSequence;
                this.appendingSequence = this.appendingArrival = undefined;
                this.appendingKeyFrame = false;
                if (this.removingBefore !== undefined) {
                    this.keyFrames = this.keyFrames.filter(time => time >= this.removingBefore);
                    this.removingBefore = undefined;
                }
                this.reportProgress(performance.now());
                this.adjustPlayback();
                this.pump();
            });
            this.pump();
        } catch (error) {
            this.fail("videoReasonBuffer", error);
        }
    }

    push(bytes, initialize, sequence, keyFrame = initialize) {
        if (this.closed)
            return;
        this.queue.push({ bytes, initialize, sequence, keyFrame, arrival: performance.now() });
        this.queuedBytes += bytes.length;
        if (this.queuedBytes > 4 * 1024 * 1024 || this.queue.length > 90) {
            this.fail("videoReasonQueue");
            return;
        }
        this.pump();
    }

    reportFrame(now, frame) {
        ++this.presentedFrames;
        const frames = this.appendedFrames;
        // 使用媒体时间匹配分片，浏览器跳帧时一起移除已经过期的记录。
        while (frames.length && frames[0].end <= frame.mediaTime + 0.0005)
            frames.shift();
        const shown = frames[0];
        if (!shown || shown.start > frame.mediaTime + 0.0005)
            return;
        this.presentedEnd = shown.end;
        this.presentation = { at: now, presentMs: Math.max(0, frame.presentationTime - shown.arrival),
            ...(Number.isFinite(frame.processingDuration) ? { decodeMs: frame.processingDuration * 1000 } : {}) };
        this.reportProgress(now);
    }

    hasPendingFrames() {
        const newest = this.appendedFrames.at(-1);
        if (!newest || newest.end <= this.video.currentTime + 0.001)
            return false;
        // 最后一帧已经显示时，缓冲中剩余的样本时长不算待播放画面。
        return this.presentedEnd !== undefined ? newest.end > this.presentedEnd + 0.000001 :
            newest.start > this.video.currentTime + 0.001 || this.video.paused;
    }

    reportProgress(now, force = false) {
        if (this.closed || !this.reportPlayback || !force && now - this.lastReport < 100)
            return;
        this.lastReport = now;
        const ranges = this.video.buffered;
        const end = ranges.length ? ranges.end(ranges.length - 1) : this.video.currentTime;
        const oldest = this.appendingArrival ?? this.queue[0]?.arrival;
        const needsPlayback = this.hasPendingFrames();
        // 呈现回调和正常播放时钟都可证明进度，避免浏览器暂缓回调时被误判；进行中的跳转不计入进度。
        const advanced = this.presentedFrames !== this.previousPresentedFrames || !this.video.seeking &&
            this.video.currentTime > (this.playbackPosition ?? 0) + 0.001;
        if (document.hidden || !this.ready || !needsPlayback || advanced)
            this.stalledSince = undefined;
        else
            this.stalledSince ??= now;
        this.previousPresentedFrames = this.presentedFrames;
        this.playbackPosition = this.video.currentTime;
        if (this.stalledSince !== undefined && now - this.stalledSince > 2000) {
            this.fail("videoReasonPlaybackTimeout");
            return;
        }
        // 媒体追加异常停滞时回退画面，避免回执停住后宿主与浏览器互相等待。
        // 后台计时不算前台追加超时，恢复后留出处理已排队媒体事件的时间。
        if (!document.hidden && oldest !== undefined && now - Math.max(oldest, this.visibleSince ?? oldest) > 2000) {
            this.fail("videoReasonAppendTimeout");
            return;
        }
        const shown = this.presentation && now - this.presentation.at < 1000 ? this.presentation : undefined;
        this.reportPlayback({
            sequence: this.lastSequence,
            presentMs: shown?.presentMs ?? 0,
            decodeMs: shown?.decodeMs ?? 0,
            bufferedMs: needsPlayback ? Math.max(0, (end - this.video.currentTime) * 1000) : 0,
            pending: this.queue.length + Number(this.appendingSequence !== undefined),
            queuedMs: oldest === undefined ? 0 : Math.max(0, now - oldest),
            visible: !document.hidden
        });
    }

    noteDrag() {
        // 给松手后的最终预览留出到达时间。
        this.interactiveUntil = performance.now() + 250;
    }

    adjustPlayback() {
        // 丢掉过期的播放缓冲，避免网络抖动后继续显示已经过时的操作。
        const ranges = this.video.buffered;
        if (this.closed || !ranges.length)
            return;
        const last = ranges.length - 1;
        const edge = ranges.end(last);
        const ahead = edge - this.video.currentTime;
        if (this.ready && this.video.readyState < 3 && !this.video.paused && !this.video.seeking && this.hasPendingFrames()) {
            // 静止界面可能只更新一帧，不够触发浏览器重新起播；直接定位到最新样本内部以显示它。
            const newest = this.appendedFrames.at(-1);
            const target = newest.start + Math.min((newest.end - newest.start) / 2, 0.001);
            this.video.currentTime = Math.max(ranges.start(last), Math.min(target, edge - 0.000001));
            this.video.playbackRate = 1;
            return;
        }
        if (performance.now() < this.interactiveUntil) {
            // 拖动时追近最新画面，保留约两帧供解码，避免重复打断正在进行的跳转。
            if (ahead > 0.15 && !this.video.seeking)
                this.video.currentTime = Math.max(ranges.start(last), edge - 0.065);
            if (this.video.playbackRate !== 1)
                this.video.playbackRate = 1;
            return;
        }
        if (ahead > 0.25 && !this.video.seeking)
            this.video.currentTime = Math.max(ranges.start(last), edge - 0.09);
        // 留少量余量给解码，帧到达较慢时避免反复播空缓冲。
        const rate = ahead < 0.08 ? 0.8 : ahead > 0.18 ? 1.1 : 1;
        if (this.video.playbackRate !== rate)
            this.video.playbackRate = rate;
    }

    pump() {
        if (this.closed || !this.buffer || this.buffer.updating)
            return;
        try {
            const ranges = this.buffer.buffered;
            const keepFrom = this.video.currentTime - 1;
            let trimTo;
            if (ranges.length) {
                // MSE 会连带删除依赖帧，只能回收到已知关键帧之前，不能按任意时间截断。
                for (const time of this.keyFrames)
                    if (time <= keepFrom && time > ranges.start(0) + 0.000001)
                        trimTo = time;
            }
            if (trimTo !== undefined) {
                this.removingBefore = trimTo;
                this.buffer.remove(0, trimTo - 0.000001);
                return;
            }
            const next = this.queue.shift();
            if (!next)
                return;
            this.queuedBytes -= next.bytes.length;
            if (next.initialize)
                this.buffer.timestampOffset = ranges.length ? ranges.end(ranges.length - 1) : 0;
            this.appendingArrival = next.arrival;
            this.appendingSequence = next.sequence;
            this.appendingKeyFrame = next.keyFrame;
            this.appendingStart = next.initialize ? this.buffer.timestampOffset : this.lastBufferedEnd;
            this.buffer.appendBuffer(next.bytes);
        } catch (error) {
            this.fail("videoReasonBuffer", error);
        }
    }

    fail(reason, error) {
        if (this.closed)
            return;
        const ranges = this.video.buffered;
        // 仅记录媒体状态，不包含文件路径、账号和认证信息，方便区分真正的解码错误与保护性回退。
        this.onError(reason, {
            error: error?.message,
            mediaError: this.video.error?.code,
            readyState: this.video.readyState,
            paused: this.video.paused,
            sourceState: this.source.readyState,
            currentTime: this.video.currentTime,
            buffered: Array.from({ length: ranges.length }, (_, index) => [ranges.start(index), ranges.end(index)]),
            pending: this.queue.length + Number(this.appendingSequence !== undefined),
            queuedBytes: this.queuedBytes,
            sequence: this.lastSequence
        });
    }

    close() {
        this.closed = true;
        this.queue.length = 0;
        clearInterval(this.progressTimer);
        document.removeEventListener("visibilitychange", this.visibilityChanged);
        if (this.frameCallback !== undefined)
            this.video.cancelVideoFrameCallback?.(this.frameCallback);
        this.video.onloadeddata = this.video.onplaying = this.video.onerror = null;
        this.video.removeAttribute("src");
        this.video.load();
        this.video.playbackRate = 1;
        this.video.hidden = true;
        delete this.video.dataset.pending;
        URL.revokeObjectURL(this.url);
    }
}
