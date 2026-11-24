"use strict";

const form = document.querySelector("#access");
const pairCode = document.querySelector("#pair-code");
const browserName = document.querySelector("#browser-name");
const connectButton = document.querySelector("#connect");
const reconnect = document.querySelector("#reconnect");
const logout = document.querySelector("#logout");
const workspace = document.querySelector("#workspace");
const status = document.querySelector("#status");
const canvas = document.querySelector("#desktop");
const streamVideo = document.querySelector("#stream-video");
const transportStatus = document.querySelector("#transport-status");
const transportDetail = document.querySelector("#transport-detail");
const retryVideo = document.querySelector("#retry-video");
const keyboard = document.querySelector("#keyboard");
const stage = document.querySelector("#stage");
const touchpadToggle = document.querySelector("#touchpad-toggle");
const touchpadHint = document.querySelector("#touchpad-hint");
const cursorHint = document.querySelector("#touchpad-cursor-hint");
const showKeyboard = document.querySelector("#show-keyboard");
const resetView = document.querySelector("#reset-view");
const controls = document.querySelector("#controls");
const controlsToggle = document.querySelector("#controls-toggle");
const controlsPanel = document.querySelector("#controls-panel");
const controlsStatus = document.querySelector("#controls-status");
const controlsTabs = [...document.querySelectorAll("[data-controls-tab]")];
const controlsClose = document.querySelector("#controls-close");
const sound = document.querySelector("#sound");
const fullscreen = document.querySelector("#fullscreen");
const videoMode = document.querySelector("#video-mode");
const quality = document.querySelector("#quality");
const resolution = document.querySelector("#resolution");
const aspectRatio = document.querySelector("#aspect-ratio");
const drawing = canvas.getContext("2d", { alpha: false });
const editorAlreadyConnected = 4009;
const titlePrefix = "title:";
const viewportPrefix = "viewport:";
const initialTitle = document.title;
let locale = "en-US";
let inheritedLocale = false;
let strings;
let socket;
let ready = false;
let remoteCursor = "default";
const cursorStyles = new Set(["default", "text", "wait", "crosshair", "ew-resize", "ns-resize", "move",
    "not-allowed", "pointer", "progress", "help", "n-resize", "s-resize", "e-resize", "w-resize",
    "nwse-resize", "nesw-resize", "copy", "alias", "none", "grab", "grabbing"]);
let rememberedAccess = false;
let accountMode = false;
let loggingOut = false;
let videoPlayback;
let videoActive = false;
let videoMime;
let streamId = 0;
let videoPending = false;
let videoTimeout;
let videoRecoveryTimer;
let videoStableTimer;
let videoRecoveryAttempts = 0;
let videoRecovering = false;
const videoRecoveryDelays = [300, 1000, 2000];
const recoverableVideoReasons = new Set([
    "videoReasonPlaybackTimeout", "videoReasonAppendTimeout", "videoReasonDecoder", "videoReasonBuffer",
    "videoReasonSource", "videoReasonFeedbackTimeout", "videoReasonStartTimeout", "videoReasonQueue"
]);
let videoFailure;
let videoFailureReason;
let pendingFrame;
let pendingViewport;
let shownViewport;
let drawingFrame = false;
let canvasReady = false;
let move;
let moving = false;
const heldKeys = new Set();
let heldButtons = 0;
let audioContext;
let audioCursor = 0;
let soundEnabled = false;
const audioSources = new Set();
const touchpad = new RemoteTouchpad(stage, document.querySelector("#touchpad-cursor"),
    () => videoActive ? streamVideo : canvas, sendTouchpadPointer);
const viewport = new RemoteViewport(stage, () => videoActive ? streamVideo : canvas,
    touchpad, sendDirectTouch, resetRemoteInput, () => focusRemoteInput(true), () => {
        touchpad.layout();
        resetView.disabled = viewport.scale === 1;
    });
const cursorLayout = new ResizeObserver(() => viewport.layout());
cursorLayout.observe(canvas);
cursorLayout.observe(streamVideo);
cursorLayout.observe(stage);

function updateCursor(style = remoteCursor) {
    remoteCursor = cursorStyles.has(style) ? style : "default";
    canvas.style.cursor = streamVideo.style.cursor = remoteCursor;
    touchpad.setCursor(remoteCursor);
    const hint = {
        "ew-resize": "touchpadResizeHorizontal", "e-resize": "touchpadResizeHorizontal", "w-resize": "touchpadResizeHorizontal",
        "ns-resize": "touchpadResizeVertical", "n-resize": "touchpadResizeVertical", "s-resize": "touchpadResizeVertical",
        "nwse-resize": "touchpadResize", "nesw-resize": "touchpadResize",
        "move": "touchpadDraggable", "grab": "touchpadDraggable", "grabbing": "touchpadDraggable"
    }[remoteCursor];
    cursorHint.hidden = !ready || !touchpad.enabled || !hint;
    cursorHint.textContent = hint ? strings?.[hint] ?? "" : "";
}

function setControlLabel(button, label) {
    button.querySelector(".control-label").textContent = label;
}

function updateSoundLabel() {
    setControlLabel(sound, strings[soundEnabled ? "muteSound" : "enableSound"]);
    sound.setAttribute("aria-pressed", String(soundEnabled));
}

function resetAudio() {
    for (const source of audioSources)
        source.stop();
    audioSources.clear();
    audioCursor = 0;
}

function scheduleAudio(bytes) {
    if (bytes[1])
        resetAudio();
    if (!soundEnabled || audioContext?.state !== "running" || bytes.byteLength <= 2)
        return;
    const samples = new DataView(bytes.buffer, 2);
    const frames = Math.floor(samples.byteLength / 4);
    const now = audioContext.currentTime;
    if (audioCursor > now + 0.22)
        resetAudio();
    if (audioCursor < now)
        audioCursor = now + 0.065;
    const audio = audioContext.createBuffer(2, frames, 48000);
    for (let channel = 0; channel < 2; channel++) {
        const output = audio.getChannelData(channel);
        for (let frame = 0; frame < frames; frame++) {
            output[frame] = samples.getInt16((frame * 2 + channel) * 2, true) / 32768;
        }
    }
    const source = audioContext.createBufferSource();
    source.buffer = audio;
    source.connect(audioContext.destination);
    source.onended = () => audioSources.delete(source);
    audioSources.add(source);
    source.start(audioCursor);
    audioCursor += audio.duration;
}

function selectControlsTab(name, focus = false) {
    const selected = controlsTabs.find(tab => tab.dataset.controlsTab === name) ?? controlsTabs[0];
    for (const tab of controlsTabs) {
        const active = tab === selected;
        tab.setAttribute("aria-selected", String(active));
        tab.tabIndex = active ? 0 : -1;
        document.getElementById(tab.getAttribute("aria-controls")).hidden = !active;
    }
    document.querySelector("#controls-body").scrollTop = 0;
    if (focus)
        selected.focus({ preventScroll: true });
}

function showControls(open, tab) {
    if (tab)
        selectControlsTab(tab);
    controlsPanel.hidden = !open;
    controls.dataset.open = String(open);
    controlsToggle.hidden = open;
    controlsToggle.setAttribute("aria-expanded", String(open));
    controlsToggle.title = strings[open ? "hideControls" : "showControls"];
    controlsToggle.setAttribute("aria-label", controlsToggle.title);
    controlsStatus.hidden = true;
    if (open) {
        resetRemoteInput();
        controlsTabs.find(tab => tab.getAttribute("aria-selected") === "true").focus({ preventScroll: true });
    }
}

function finishControlAction() {
    showControls(false);
    if (ready)
        focusRemoteInput();
}

function showControlError(message) {
    if (controlsPanel.hidden)
        return;
    controlsStatus.textContent = message;
    controlsStatus.hidden = false;
}

controlsToggle.addEventListener("click", () => {
    if (controlsPanel.hidden)
        showControls(true);
    else
        finishControlAction();
});

controlsClose.addEventListener("click", finishControlAction);
for (const tab of controlsTabs) {
    tab.addEventListener("click", () => selectControlsTab(tab.dataset.controlsTab));
    tab.addEventListener("keydown", event => {
        const index = controlsTabs.indexOf(tab);
        const next = event.key === "ArrowRight" ? (index + 1) % controlsTabs.length :
            event.key === "ArrowLeft" ? (index + controlsTabs.length - 1) % controlsTabs.length :
            event.key === "Home" ? 0 : event.key === "End" ? controlsTabs.length - 1 : -1;
        if (next < 0)
            return;
        event.preventDefault();
        selectControlsTab(controlsTabs[next].dataset.controlsTab, true);
    });
}

let dismissingPointer;
let dismissingTouch = false;
workspace.addEventListener("pointerdown", event => {
    dismissingPointer = undefined;
    if (controlsPanel.hidden || controls.contains(event.target))
        return;
    // 收起浮层的这次手势不传给远程编辑器，避免误点击或触发长按拖动。
    dismissingTouch = event.pointerType === "touch";
    if (!dismissingTouch)
        dismissingPointer = event.pointerId;
    event.preventDefault();
    event.stopPropagation();
    finishControlAction();
}, true);
for (const type of ["pointerup", "pointercancel"]) {
    workspace.addEventListener(type, event => {
        if (event.pointerId !== dismissingPointer)
            return;
        dismissingPointer = undefined;
        event.preventDefault();
        event.stopPropagation();
    }, true);
}
for (const type of ["touchstart", "touchmove", "touchend", "touchcancel"]) {
    workspace.addEventListener(type, event => {
        if (!dismissingTouch)
            return;
        event.preventDefault();
        event.stopPropagation();
        if (type === "touchcancel" || !event.touches.length)
            dismissingTouch = false;
    }, { capture: true, passive: false });
}

workspace.addEventListener("keydown", event => {
    if (event.altKey && event.shiftKey && !event.ctrlKey && !event.metaKey && event.code === "KeyU") {
        event.preventDefault();
        event.stopPropagation();
        if (event.repeat)
            return;
        if (controlsPanel.hidden)
            showControls(true);
        else
            finishControlAction();
    } else if (event.key === "Escape" && !controlsPanel.hidden && controls.contains(event.target)) {
        event.preventDefault();
        finishControlAction();
    }
}, true);

sound.addEventListener("click", async () => {
    if (soundEnabled) {
        soundEnabled = false;
        resetAudio();
    } else {
        sound.disabled = true;
        try {
            audioContext ??= new AudioContext();
            await audioContext.resume();
            soundEnabled = true;
        } catch {
            showControlError(strings.soundUnavailable);
            return;
        } finally {
            sound.disabled = false;
        }
    }
    updateSoundLabel();
    send({ type: "audio", enabled: soundEnabled });
});

function showStatus(message) {
    status.textContent = message;
    status.hidden = !message;
}

function updateTransport() {
    const h264 = videoMode.value === "h264";
    const videoStatus = h264 ? "transportH264Socket" : "transportAv1Socket";
    transportStatus.textContent = strings[videoActive ? videoStatus : videoMode.value === "jpeg" ? "transportJpeg" :
        videoFailure && !videoRecovering ? "transportVideoStopped" : "transportVideoConnecting"];
    const detail = videoPending ? (h264 ? "transportH264Connecting" : "transportAv1Connecting") : videoFailure;
    const description = videoRecovering ? strings.transportReconnecting
        .replace("{0}", videoRecoveryAttempts).replace("{1}", videoRecoveryDelays.length) : strings[detail];
    transportDetail.textContent = [description, !videoPending && strings[videoFailureReason]].filter(Boolean).join("\n");
    transportDetail.hidden = !transportDetail.textContent;
    retryVideo.hidden = !ready || !videoFailure || videoRecovering || videoMode.value === "jpeg";
    controls.dataset.videoState = videoFailure && !videoRecovering ? "error" :
        videoActive || ready && videoMode.value === "jpeg" ? "active" : "pending";
}

function closeVideo() {
    resetRemoteInput();
    clearTimeout(videoTimeout);
    clearTimeout(videoRecoveryTimer);
    clearTimeout(videoStableTimer);
    videoRecovering = false;
    // 只在结束视频流时拷贝一次最后画面，切换和回退期间继续显示，避免两种画面同时隐藏。
    if (videoActive && streamVideo.readyState >= 2 && streamVideo.videoWidth) {
        try { paintCanvas(streamVideo, pendingViewport); } catch { /* 失效的视频源继续使用上一张有效快照。 */ }
    }
    videoPlayback?.close();
    canvas.hidden = !canvasReady;
    videoPlayback = undefined;
    videoMime = undefined;
    videoActive = videoPending = false;
}

function disableVideo(notifyHost, reason = "videoReasonHost", diagnostics) {
    console.warn("Remote video stopped", { streamId, codec: videoMode.value, reason, ...diagnostics });
    closeVideo();
    videoFailure = videoMode.value === "h264" ? "transportH264Failure" : "transportAv1Failure";
    videoFailureReason = reason;
    if (notifyHost)
        send({ type: "video-stop", streamId });
    // 只重建所选视频流，保留工作区、认证连接和最后画面；连续失败后交还用户处理。
    if (ready && videoMode.value !== "jpeg" && recoverableVideoReasons.has(reason) && videoRecoveryAttempts < videoRecoveryDelays.length) {
        const delay = videoRecoveryDelays[videoRecoveryAttempts++];
        videoRecovering = true;
        videoRecoveryTimer = setTimeout(() => {
            if (ready)
                startMedia(true);
        }, delay);
    }
    updateTransport();
    if (ready && !videoRecovering) {
        showControls(true, "connection");
        retryVideo.focus();
    }
}

function receiveVideo(bytes, initialize, keyFrame) {
    if (!videoMime || bytes.length <= 9)
        return;
    const header = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    if (header.getInt32(1, true) !== streamId)
        return;
    if (!videoPlayback) {
        if (!initialize)
            return;
        try {
            videoPlayback = new VideoPlayback(streamVideo, videoMime, () => {
                if (videoActive)
                    return;
                clearTimeout(videoTimeout);
                resetRemoteInput();
                videoActive = true;
                if (touchpad.enabled)
                    touchpad.emit("move", -1);
                videoPending = false;
                videoRecovering = false;
                // 短暂恢复后又停滞仍计入连续失败，稳定一段时间后才重置重试额度。
                const currentStream = streamId;
                videoStableTimer = setTimeout(() => {
                    if (ready && videoActive && streamId === currentStream)
                        videoRecoveryAttempts = 0;
                }, 15000);
                canvas.hidden = true;
                delete streamVideo.dataset.pending;
                updateTransport();
            }, (reason, diagnostics) => disableVideo(true, reason, diagnostics),
                report => send({ type: "video-playback", streamId, ...report }));
        } catch (error) {
            disableVideo(true, "videoReasonStart", { error: error.message });
            return;
        }
    }
    videoPlayback.push(bytes.subarray(9), initialize, header.getUint32(5, true), keyFrame);
}

function startMedia(automatic = false) {
    closeVideo();
    if (!automatic)
        videoRecoveryAttempts = 0;
    videoRecovering = automatic;
    videoFailure = videoFailureReason = undefined;
    const codec = videoMode.value;
    ++streamId;
    if (codec !== "jpeg") {
        videoMime = VideoPlayback.supportedMime(codec);
        if (videoMime) {
            videoPending = true;
            videoTimeout = setTimeout(() => disableVideo(true, "videoReasonStartTimeout"), 7000);
        } else {
            videoFailure = codec === "h264" ? "transportH264Unsupported" : "transportUnsupported";
        }
    }
    send({ type: "video-start", streamId, codec, feedback: true, manualRecovery: true });
    if (codec !== "jpeg" && !videoMime)
        send({ type: "video-stop", streamId });
    updateTransport();
    if (videoFailure) {
        showControls(true, "connection");
        retryVideo.focus();
    }
}

streamVideo.addEventListener("resize", () => {
    if (streamVideo.videoHeight)
        streamVideo.style.setProperty("--frame-ratio", streamVideo.videoWidth / streamVideo.videoHeight);
});

function connect(credential) {
    if (!credential || socket && socket.readyState !== WebSocket.CLOSED)
        return;
    rememberedAccess = credential === "remembered";
    reconnect.hidden = !rememberedAccess;
    const url = new URL("/connect?media=explicit", location.href);
    url.protocol = location.protocol === "https:" ? "wss:" : "ws:";
    const connection = new WebSocket(url);
    socket = connection;
    connection.binaryType = "arraybuffer";
    // 浏览器可把离开的页面冻结在往返缓存中；及时释放工作区，返回页面时再接回。
    const leavePage = event => {
        if (connection.readyState !== WebSocket.OPEN && connection.readyState !== WebSocket.CONNECTING)
            return;
        if (event.persisted && !loggingOut) {
            window.addEventListener("pageshow", () => {
                const resume = () => connect(credential);
                if (connection.readyState === WebSocket.CLOSED)
                    resume();
                else
                    connection.addEventListener("close", resume, { once: true });
            }, { once: true });
        }
        connection.close(1000);
    };
    window.addEventListener("pagehide", leavePage, { once: true });
    showStatus(strings.connecting);
    connection.onopen = () => connection.send(credential);
    connection.onmessage = ({ data }) => {
        if (socket !== connection)
            return;
        if (data === "ready") {
            ready = true;
            logout.hidden = !rememberedAccess;
            form.hidden = true;
            workspace.hidden = false;
            showStatus("");
            updateCursor("default");
            send({ type: "cursor", enabled: true });
            send({ type: "quality", quality: quality.value });
            send({ type: "audio", enabled: soundEnabled });
            resizeDesktop();
            if (!inheritedLocale)
                send({ type: "locale", locale });
            focusRemoteInput();
            startMedia();
            if (touchpad.enabled)
                touchpad.emit("move", -1);
        } else if (typeof data === "string" && data.startsWith("video-failure:")) {
            const [, id, reason] = data.split(":");
            if (Number(id) === streamId)
                videoFailureReason = {
                "encoder-unavailable": "videoReasonEncoderUnavailable",
                "encoder-error": "videoReasonEncoder",
                "feedback-timeout": "videoReasonFeedbackTimeout"
            }[reason] ?? "videoReasonHost";
        } else if (data === "video-fallback:" + streamId) {
            disableVideo(false, videoFailureReason);
        } else if (ready && typeof data === "string" && data.startsWith("cursor:")) {
            updateCursor(data.slice(7));
        } else if (ready && typeof data === "string" && data.startsWith(titlePrefix)) {
            document.title = data.slice(titlePrefix.length) || initialTitle;
        } else if (ready && typeof data === "string" && data.startsWith(viewportPrefix)) {
            const [width, height] = data.slice(viewportPrefix.length).split(",").map(Number);
            if (width > 0 && height > 0)
                pendingViewport = { width, height };
        } else if (data instanceof ArrayBuffer) {
            const bytes = new Uint8Array(data);
            if (bytes[0] === 65) {
                scheduleAudio(bytes);
                return;
            }
            if (bytes[0] === 73 || bytes[0] === 75 || bytes[0] === 86) {
                receiveVideo(bytes, bytes[0] === 73, bytes[0] !== 86);
                return;
            }
            if (bytes[0] === 255 && videoMode.value === "jpeg") {
                pendingFrame = { image: new Blob([data], { type: "image/jpeg" }), connection, viewport: pendingViewport };
                void draw();
            }
        }
    };
    connection.onclose = event => {
        window.removeEventListener("pagehide", leavePage);
        if (socket !== connection)
            return;
        ready = false;
        updateCursor("default");
        document.title = initialTitle;
        closeVideo();
        videoFailure = videoFailureReason = undefined;
        updateTransport();
        pendingFrame = pendingViewport = shownViewport = undefined;
        canvasReady = false;
        canvas.hidden = true;
        resetAudio();
        form.hidden = false;
        workspace.hidden = true;
        showControls(false);
        if (document.fullscreenElement === workspace)
            void document.exitFullscreen().catch(() => {});
        if (event.code === 1008) {
            rememberedAccess = false;
            reconnect.hidden = true;
            showStatus(accountMode ? strings.loginExpired : strings.accessDenied);
        }
        else if (event.code === editorAlreadyConnected)
            showStatus(strings.editorAlreadyConnected);
        else if (event.code === 4013)
            showStatus(strings.workerLimit);
        else if (event.code === 4014)
            showStatus(strings.workspaceUpdated);
        else if (event.code === 1011)
            showStatus(strings.workerFailed);
        else
            showStatus(loggingOut ? strings.signedOut : strings.disconnected);
        if (loggingOut)
            showStatus(strings.signedOut);
        loggingOut = false;
    };
}

function paintCanvas(image, logicalViewport) {
    const width = image.videoWidth ?? image.width;
    const height = image.videoHeight ?? image.height;
    if (canvas.width !== width || canvas.height !== height) {
        canvas.width = width;
        canvas.height = height;
        canvas.style.setProperty("--frame-ratio", width / height);
    }
    drawing.drawImage(image, 0, 0, canvas.width, canvas.height);
    shownViewport = logicalViewport;
    canvasReady = true;
    canvas.hidden = false;
}

async function draw() {
    if (drawingFrame)
        return;
    drawingFrame = true;
    try {
        while (pendingFrame) {
            const frame = pendingFrame;
            pendingFrame = undefined;
            let bitmap;
            try {
                bitmap = await createImageBitmap(frame.image);
            } catch {
                continue;
            }
            if (frame.connection !== socket || !ready || videoActive || videoMode.value !== "jpeg") {
                bitmap.close();
                continue;
            }
            paintCanvas(bitmap, frame.viewport);
            bitmap.close();
        }
    } finally {
        drawingFrame = false;
    }
}

function send(message) {
    if (!ready || socket?.readyState !== WebSocket.OPEN)
        return;
    if (message.type === "keyDown" && message.code && heldKeys.size < 32)
        heldKeys.add(message.code);
    else if (message.type === "keyUp")
        heldKeys.delete(message.code);
    else if (message.type === "reset") {
        heldKeys.clear();
        heldButtons = 0;
    }
    if (["down", "up", "move"].includes(message.type) && Number.isInteger(message.buttons))
        heldButtons = message.buttons & 7;
    socket.send(JSON.stringify(message));
}

function resetRemoteInput() {
    move = undefined;
    viewport.cancel();
    heldKeys.clear();
    heldButtons = 0;
    send({ type: "reset" });
}

// 只有输入仍被按住时才保活；服务端不会把保活当成新画面活动。
setInterval(() => {
    if (!document.hidden && (heldKeys.size || heldButtons))
        send({ type: "input-state", keys: [...heldKeys], buttons: heldButtons });
}, 1000);

window.addEventListener("blur", resetRemoteInput);
keyboard.addEventListener("blur", () => {
    keyboard.inputMode = "none";
    resetRemoteInput();
});
document.addEventListener("visibilitychange", () => {
    if (document.hidden)
        resetRemoteInput();
});

function modifiers(event) {
    return { shift: event.shiftKey, control: event.ctrlKey, alt: event.altKey, meta: event.metaKey };
}

function focusRemoteInput(touch = false) {
    // 手机触摸只聚焦非编辑区域；软键盘由用户显式打开，桌面保留直接键盘输入。
    if (touch || matchMedia("(pointer: coarse)").matches || touchpad.enabled) {
        if (document.activeElement !== keyboard)
            stage.focus({ preventScroll: true });
    } else {
        keyboard.inputMode = "text";
        keyboard.focus({ preventScroll: true });
    }
}

showKeyboard.addEventListener("click", () => {
    showControls(false);
    keyboard.inputMode = "text";
    keyboard.focus({ preventScroll: true });
});
resetView.addEventListener("click", () => {
    viewport.reset();
    finishControlAction();
});

function sendDirectTouch(type, point) {
    const message = pointer({ currentTarget: videoActive ? streamVideo : canvas,
        clientX: point.x, clientY: point.y, button: 0, buttons: type === "up" ? 0 : 1 }, type);
    if (type === "move")
        queueMove(message);
    else {
        flushMove();
        send(message);
    }
}

function pointer(event, type) {
    const bounds = event.currentTarget.getBoundingClientRect();
    // 返回到宿主的坐标按实际工作区计算，画面分辨率可随画质变化。
    const viewport = pointerViewport();
    return {
        type, ...modifiers(event), button: event.button, buttons: event.buttons,
        x: (event.clientX - bounds.left) * viewport.width / bounds.width,
        y: (event.clientY - bounds.top) * viewport.height / bounds.height
    };
}

function pointerViewport() {
    return (videoActive ? pendingViewport : shownViewport) ?? canvas;
}

function queueMove(message) {
    if (message.buttons & 1)
        videoPlayback?.noteDrag();
    move = message;
    if (moving)
        return;
    moving = true;
    requestAnimationFrame(() => { moving = false; flushMove(); });
}

function sendTouchpadPointer(message, x, y) {
    const viewport = pointerViewport();
    message.x = Math.min(x * viewport.width, viewport.width - 1);
    message.y = Math.min(y * viewport.height, viewport.height - 1);
    if (message.type === "move")
        queueMove(message);
    else {
        flushMove();
        send(message);
    }
}

function flushMove() {
    if (move)
        send(move);
    move = undefined;
}

for (const surface of [canvas, streamVideo]) {
    surface.addEventListener("pointermove", event => {
        if (event.pointerType === "touch")
            return;
        queueMove(pointer(event, "move"));
    });
    surface.addEventListener("pointerdown", event => {
        if (event.pointerType === "touch")
            return;
        event.preventDefault();
        focusRemoteInput();
        surface.setPointerCapture(event.pointerId);
        flushMove();
        send(pointer(event, "down"));
    });
    surface.addEventListener("pointerup", event => {
        if (event.pointerType === "touch")
            return;
        flushMove();
        send(pointer(event, "up"));
    });
    surface.addEventListener("pointercancel", event => {
        if (event.pointerType !== "touch")
            resetRemoteInput();
    });
    surface.addEventListener("pointerleave", event => {
        if (event.pointerType === "touch")
            return;
        flushMove();
        send({ type: "leave" });
    });
    surface.addEventListener("contextmenu", event => event.preventDefault());
    surface.addEventListener("wheel", event => {
        event.preventDefault();
        const scale = event.deltaMode === WheelEvent.DOM_DELTA_PIXEL ? 100 : 3;
        send({ ...pointer(event, "wheel"), dx: -event.deltaX / scale, dy: -event.deltaY / scale });
    }, { passive: false });
}

for (const [name, type] of [["keydown", "keyDown"], ["keyup", "keyUp"]]) {
    workspace.addEventListener(name, event => {
        if (event.target !== keyboard && event.target !== stage || event.isComposing && name === "keydown")
            return;
        send({ type, ...modifiers(event), key: event.key, code: event.code });
        // 非编辑焦点仍接收外接键盘；文本框的输入与中文组词继续走 input/composition。
        if (event.target === stage && name === "keydown" && !event.ctrlKey && !event.metaKey && !event.altKey && [...event.key].length === 1)
            sendKeyboardText(event.key);
        if (event.target === stage || event.key === "Tab" || event.ctrlKey || event.metaKey || event.altKey)
            event.preventDefault();
    });
}
function sendKeyboardText(text) {
    keyboard.value = "";
    // 用 Unicode 码点分段，长文本也能传输，且不会切开代理对。
    let chunk = "";
    for (const character of text) {
        if (chunk.length + character.length > 512) {
            send({ type: "text", text: chunk });
            chunk = "";
        }
        chunk += character;
    }
    if (chunk)
        send({ type: "text", text: chunk });
}

keyboard.addEventListener("input", event => {
    if (!event.isComposing)
        sendKeyboardText(keyboard.value);
});
keyboard.addEventListener("compositionend", () => {
    // 有些浏览器在组合结束后不再发 input；等同步输入事件处理完再补发。
    queueMicrotask(() => sendKeyboardText(keyboard.value));
});
async function restoreAccess() {
    try {
        const response = await fetch("/auth", { cache: "no-store" });
        if (!response.ok)
            throw new Error();
        const mode = await response.text();
        accountMode = response.headers.get("X-TSCutter-Auth") === "accounts";
        document.querySelector("#account-fields").hidden = !accountMode;
        document.querySelector("#pair-fields").hidden = accountMode;
        document.querySelector("#username").required = accountMode;
        document.querySelector("#password").required = accountMode;
        pairCode.required = !accountMode;
        connectButton.textContent = strings[accountMode ? "login" : "connect"];
        setControlLabel(logout, strings[accountMode ? "signOut" : "forgetBrowser"]);
        document.querySelector("#login-hint").hidden = !accountMode;
        const name = response.headers.get("X-TSCutter-User");
        const identity = document.querySelector("#identity");
        identity.hidden = !name;
        identity.textContent = name ? strings.signedInAs.replace("{0}", name) : "";
        if (mode === "remembered")
            connect(mode);
        else {
            rememberedAccess = false;
            reconnect.hidden = true;
        }
    } catch {
        showStatus(strings.disconnected);
    }
}

reconnect.addEventListener("click", () => void restoreAccess());
form.addEventListener("submit", async event => {
    event.preventDefault();
    if (connectButton.disabled)
        return;
    connectButton.disabled = true;
    showStatus(strings.connecting);
    try {
        const body = new URLSearchParams(accountMode ? {
            username: document.querySelector("#username").value.trim(),
            password: document.querySelector("#password").value
        } : { code: pairCode.value.trim(), name: browserName.value });
        const response = await fetch(accountMode ? "/login" : "/pair", { method: "POST", body });
        if (response.ok) {
            if (accountMode) {
                document.querySelector("#password").value = "";
                location.reload();
            } else {
                pairCode.value = "";
                connect(await response.text());
            }
        } else {
            showStatus(response.status === 409 ? strings.editorAlreadyConnected :
                response.status === 429 ? strings[accountMode ? "loginLimited" : "pairLocked"] :
                response.status === 400 || response.status === 401 ? strings[accountMode ? "loginFailed" : "accessDenied"] : strings.pairFailed);
        }
    } catch {
        showStatus(strings.pairFailed);
    } finally {
        connectButton.disabled = false;
    }
});
logout.addEventListener("click", async () => {
    loggingOut = accountMode;
    try {
        const response = await fetch("/logout", { method: "POST" });
        if (!response.ok)
            throw new Error();
        rememberedAccess = false;
        reconnect.hidden = true;
        if (socket && socket.readyState !== WebSocket.CLOSED)
            socket.close();
        else {
            loggingOut = false;
            showStatus(accountMode ? strings.signedOut : strings.disconnected);
        }
    } catch {
        loggingOut = false;
        showControlError(strings.pairFailed);
    }
});

const browserLocales = { en: "en-US", "zh-Hans": "zh-CN", "zh-Hant": "zh-TW" };

function preferredLocale() {
    for (const language of navigator.languages) {
        const preference = new Intl.Locale(language).maximize();
        const supported = browserLocales[`${preference.language}-${preference.script}`] ?? browserLocales[preference.language];
        if (supported)
            return supported;
    }
    return "en-US";
}

async function loadLocale(culture) {
    const response = await fetch(`/locales/${culture}.json`);
    if (!response.ok)
        throw new Error(String(response.status));
    return response.json();
}

async function initialize() {
    locale = preferredLocale();
    try {
        const response = await fetch("/startup-locale");
        const hostLocale = response.ok ? await response.text() : "";
        if (Object.values(browserLocales).includes(hostLocale)) {
            locale = hostLocale;
            inheritedLocale = true;
        }
    } catch {
        // 获取宿主语言失败时沿用浏览器语言。
    }
    try {
        strings = await loadLocale(locale);
    } catch {
        locale = "en-US";
        strings = await loadLocale(locale);
    }
    document.documentElement.lang = locale;
    document.querySelector("#username-label").textContent = strings.username;
    document.querySelector("#password-label").textContent = strings.password;
    document.querySelector("#login-hint").textContent = strings.loginHint;
    document.querySelector("#pair-code-label").textContent = strings.pairCode;
    document.querySelector("#browser-name-label").textContent = strings.browserName;
    connectButton.textContent = strings.connect;
    reconnect.textContent = strings.reconnect;
    setControlLabel(logout, strings.forgetBrowser);
    canvas.setAttribute("aria-label", strings.desktop);
    streamVideo.setAttribute("aria-label", strings.desktop);
    keyboard.setAttribute("aria-label", strings.keyboard);
    stage.setAttribute("aria-label", strings.desktop);
    setControlLabel(showKeyboard, strings.showKeyboard);
    setControlLabel(resetView, strings.resetView);
    showKeyboard.hidden = resetView.hidden = navigator.maxTouchPoints === 0;
    document.querySelector("#controls-title").textContent = strings.controlsTitle;
    document.querySelector("#controls-toggle-label").textContent = strings.controlsButton;
    controlsClose.setAttribute("aria-label", strings.hideControls);
    for (const tab of controlsTabs)
        tab.textContent = strings["controls" + tab.dataset.controlsTab[0].toUpperCase() + tab.dataset.controlsTab.slice(1)];
    showControls(false);
    updateTransport();
    retryVideo.textContent = strings.retryVideo;
    setControlLabel(fullscreen, strings.fullscreen);
    setControlLabel(touchpadToggle, strings.touchpad);
    // 以触摸为主要指针的设备默认使用触控板，不影响以鼠标为主的触屏电脑。
    touchpad.setEnabled(navigator.maxTouchPoints > 0 && matchMedia("(pointer: coarse)").matches);
    updateCursor();
    touchpadToggle.setAttribute("aria-pressed", String(touchpad.enabled));
    touchpadHint.textContent = strings[touchpad.enabled ? "touchpadHint" : "touchHint"];
    touchpadHint.hidden = navigator.maxTouchPoints === 0;
    document.querySelector("#touchpad-dragging").textContent = strings.touchpadDragging;
    document.querySelector("#touchpad-scrolling").textContent = strings.touchpadScrolling;
    touchpadToggle.hidden = navigator.maxTouchPoints === 0;
    document.querySelector("#video-mode-label").textContent = strings.videoMode;
    videoMode.querySelector('option[value="av1"]').textContent = strings.videoModeAv1;
    videoMode.querySelector('option[value="h264"]').textContent = strings.videoModeH264;
    const requestedMode = new URLSearchParams(location.search).get("transport");
    if (["h264", "av1", "jpeg"].includes(requestedMode))
        videoMode.value = requestedMode;

    document.querySelector("#quality-label").textContent = strings.quality;
    quality.options[0].textContent = strings.qualitySmooth;
    quality.options[1].textContent = strings.qualityBalanced;
    quality.options[2].textContent = strings.qualitySharp;
    document.querySelector("#resolution-label").textContent = strings.resolution;
    resolution.options[0].textContent = strings.resolutionAuto;
    document.querySelector("#resolution-hint").textContent = strings.resolutionHint;
    document.querySelector("#aspect-ratio-label").textContent = strings.aspectRatio;
    aspectRatio.options[0].textContent = strings.aspectRatioAuto;
    updateSoundLabel();
    form.hidden = false;

    const initialKey = location.hash.slice(1);
    if (/^[a-f\d]{64}$/i.test(initialKey)) {
        history.replaceState(null, "", location.pathname + location.search);
        connect(initialKey);
    } else {
        await restoreAccess();
    }
}

function resizeDesktop() {
    if (!ready || !stage.clientWidth || !stage.clientHeight)
        return;
    const scale = Math.max(670 / stage.clientWidth, 400 / stage.clientHeight, 1);
    let width = Math.round(stage.clientWidth * scale), height = Math.round(stage.clientHeight * scale);
    if (aspectRatio.value === "16:9") {
        const units = Math.max(45, Math.min(240, Math.floor(Math.min(width / 16, height / 9))));
        width = units * 16;
        height = units * 9;
    }
    // 按画面实际占用的区域计算密度，抵消小屏工作区放大及固定比例产生的留白。
    const displayScale = Math.min(stage.clientWidth / width, stage.clientHeight / height);
    send({ type: "resize", width, height, aspectRatio: aspectRatio.value,
        resolution: resolution.value, pixelRatio: window.devicePixelRatio * displayScale });
}
new ResizeObserver(resizeDesktop).observe(stage);
function watchDisplayScale() {
    matchMedia(`(resolution: ${window.devicePixelRatio}dppx)`).addEventListener("change", () => {
        watchDisplayScale();
        resizeDesktop();
    }, { once: true });
}
watchDisplayScale();
fullscreen.hidden = !document.fullscreenEnabled;
fullscreen.addEventListener("click", async () => {
    try {
        if (document.fullscreenElement)
            await document.exitFullscreen();
        else
            await workspace.requestFullscreen();
        if (!controlsPanel.hidden)
            finishControlAction();
    } catch {
        showControlError(strings.fullscreenUnavailable);
    }
});
touchpadToggle.addEventListener("click", () => {
    resetRemoteInput();
    touchpad.setEnabled(!touchpad.enabled);
    updateCursor();
    touchpadToggle.setAttribute("aria-pressed", String(touchpad.enabled));
    touchpadHint.textContent = strings[touchpad.enabled ? "touchpadHint" : "touchHint"];
});
retryVideo.addEventListener("click", () => {
    if (ready)
        startMedia();
});
videoMode.addEventListener("change", () => {
    if (ready)
        startMedia();
});
quality.addEventListener("change", () => {
    send({ type: "quality", quality: quality.value });
});
for (const setting of [resolution, aspectRatio]) {
    setting.addEventListener("change", resizeDesktop);
}
document.addEventListener("fullscreenchange", () => {
    setControlLabel(fullscreen, strings[document.fullscreenElement ? "exitFullscreen" : "fullscreen"]);
});
setInterval(() => {
    if (ready && !document.hidden)
        document.querySelector("#appearance").href = "/appearance.css?t=" + Date.now();
}, 5000);
void initialize();
