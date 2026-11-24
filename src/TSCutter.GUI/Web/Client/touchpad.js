"use strict";

class RemoteTouchpad {
    constructor(stage, cursor, surface, sendPointer) {
        this.stage = stage;
        this.cursor = cursor;
        this.surface = surface;
        this.sendPointer = sendPointer;
        this.enabled = false;
        this.x = 0.5;
        this.y = 0.5;

        // 手势由 RemoteViewport 统一分发，避免双指缩放同时触发远程滚动。
    }

    setEnabled(enabled) {
        this.cancel();
        this.enabled = enabled;
        this.layout();
        if (enabled)
            this.emit("move", -1);
    }

    setCursor(style) {
        this.cursor.dataset.cursor = style;
        this.cursor.dataset.shape = {
            "ew-resize": "horizontal", "e-resize": "horizontal", "w-resize": "horizontal",
            "ns-resize": "vertical", "n-resize": "vertical", "s-resize": "vertical",
            "nwse-resize": "diagonal", "nesw-resize": "diagonal",
            "grabbing": "grab", "progress": "wait", "alias": "copy"
        }[style] ?? style;
        this.layout();
    }

    layout() {
        if (!this.enabled || this.cursor.dataset.cursor === "none") {
            this.cursor.hidden = true;
            return;
        }
        const bounds = this.surface().getBoundingClientRect();
        this.cursor.hidden = !bounds.width || !bounds.height;
        if (this.cursor.hidden)
            return;

        const stage = this.stage.getBoundingClientRect();
        this.cursor.style.left = bounds.left - stage.left + this.x * bounds.width + "px";
        this.cursor.style.top = bounds.top - stage.top + this.y * bounds.height + "px";
    }

    point(touches) {
        const first = touches[0], second = touches[1];
        return second
            ? { x: (first.clientX + second.clientX) / 2, y: (first.clientY + second.clientY) / 2 }
            : { x: first.clientX, y: first.clientY };
    }

    start(event) {
        const touches = event.targetTouches;
        if (!touches.length)
            return;
        const point = this.point(touches);
        if (!this.gesture) {
            const gesture = { point, time: performance.now(), fingers: touches.length, moved: false, dragging: false };
            this.gesture = gesture;
            if (gesture.fingers === 1) {
                gesture.hold = setTimeout(() => {
                    gesture.dragging = true;
                    this.stage.dataset.touchpadGesture = "drag";
                    this.emit("down", 0, 1);
                }, 420);
            }
            return;
        }

        if (touches.length > this.gesture.fingers) {
            const gesture = this.gesture;
            // 手指数变化时重新取中心点，避免第二根手指接触时误滚动。
            this.releasePointer(gesture);
            gesture.fingers = touches.length;
            gesture.point = point;
        }
    }

    move(event) {
        const gesture = this.gesture;
        const touches = event.targetTouches;
        if (!gesture?.point || touches.length !== gesture.fingers || touches.length > 2)
            return;
        const point = this.point(touches);
        const dx = point.x - gesture.point.x, dy = point.y - gesture.point.y;
        if (!gesture.moved && !gesture.dragging) {
            if (Math.hypot(dx, dy) < 7)
                return;
            // 长按前已开始滑动，按普通光标移动处理。
            clearTimeout(gesture.hold);
        }
        gesture.moved = true;

        gesture.point = point;
        if (gesture.fingers === 2) {
            this.stage.dataset.touchpadGesture = "scroll";
            this.emit("wheel", 0, 0, { dx: dx / 60, dy: dy / 60 });
            return;
        }

        const bounds = this.surface().getBoundingClientRect();
        if (!bounds.width || !bounds.height)
            return;
        this.x = Math.max(0, Math.min(1, this.x + dx / bounds.width));
        this.y = Math.max(0, Math.min(1, this.y + dy / bounds.height));
        this.layout();
        this.emit("move", -1, gesture.dragging ? 1 : 0);
    }

    end(event) {
        const gesture = this.gesture;
        if (!gesture)
            return;
        if (event.targetTouches.length) {
            delete this.stage.dataset.touchpadGesture;
            gesture.point = undefined;
            return;
        }
        this.gesture = undefined;

        if (this.releasePointer(gesture))
            return;
        if (gesture.moved || gesture.fingers > 2)
            return;
        if (gesture.fingers === 2 && performance.now() - gesture.time > 320)
            return;
        const button = gesture.fingers === 2 ? 2 : 0;
        this.emit("down", button, button === 2 ? 2 : 1);
        this.emit("up", button);
    }

    releasePointer(gesture) {
        clearTimeout(gesture.hold);
        delete this.stage.dataset.touchpadGesture;
        if (!gesture.dragging)
            return false;
        gesture.dragging = false;
        this.emit("up");
        return true;
    }

    cancel(release = true) {
        if (this.gesture) {
            clearTimeout(this.gesture.hold);
            if (release)
                this.releasePointer(this.gesture);
        }
        this.gesture = undefined;
        delete this.stage.dataset.touchpadGesture;
    }

    emit(type, button = 0, buttons = 0, delta) {
        this.sendPointer({ type, button, buttons, ...delta }, this.x, this.y);
    }
}
