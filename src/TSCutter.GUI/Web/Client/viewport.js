"use strict";

class RemoteViewport {
    constructor(stage, surface, touchpad, sendPointer, resetInput, focusInput, changed) {
        Object.assign(this, { stage, surface, touchpad, sendPointer, resetInput, focusInput, changed });
        this.scale = 1;
        this.x = this.y = 0;
        for (const [name, method] of [["touchstart", "start"], ["touchmove", "move"], ["touchend", "end"]]) {
            stage.addEventListener(name, event => {
                event.preventDefault();
                const touches = [...event.touches].filter(touch => stage.contains(touch.target));
                this[method]({ targetTouches: touches });
            }, { passive: false });
        }
        stage.addEventListener("touchcancel", () => this.resetInput(), { passive: true });
        matchMedia("(orientation: portrait)").addEventListener("change", () => this.resetInput());
    }

    point(touch) { return { x: touch.clientX, y: touch.clientY }; }

    pair(touches) {
        const a = this.point(touches[0]), b = this.point(touches[1]);
        return { x: (a.x + b.x) / 2, y: (a.y + b.y) / 2, distance: Math.hypot(a.x - b.x, a.y - b.y) };
    }

    start(event) {
        const touches = event.targetTouches;
        if (touches.length > 2) {
            this.resetInput();
            this.blocked = true;
            return;
        }
        if (this.blocked || this.multi)
            return;
        if (touches.length === 2) {
            // 第二根手指到来时取消待定点击与长按，不让缩放顺便点击远程按钮。
            this.resetInput();
            this.multi = { ...this.pair(touches), scale: this.scale, panX: this.x, panY: this.y };
            if (this.touchpad.enabled)
                this.touchpad.start(event);
            return;
        }
        if (touches.length !== 1)
            return;
        this.focusInput();
        if (this.touchpad.enabled) {
            this.touchpad.start(event);
            return;
        }
        const point = this.point(touches[0]), bounds = this.surface().getBoundingClientRect();
        if (point.x < bounds.left || point.x > bounds.right || point.y < bounds.top || point.y > bounds.bottom)
            return;
        const single = this.single = { point, last: point, panX: this.x, panY: this.y };
        // 轻点在抬手时发送；长按后才按住，给捏合和本地平移留出判定时间。
        single.hold = setTimeout(() => {
            if (this.single !== single)
                return;
            single.down = true;
            this.stage.dataset.touchpadGesture = "drag";
            this.sendPointer("down", point);
        }, 420);
    }

    move(event) {
        if (this.blocked)
            return;
        const touches = event.targetTouches;
        if (this.multi) {
            const start = this.multi;
            if (touches.length !== 2 || start.finished)
                return;
            const current = this.pair(touches);
            const distance = Math.abs(current.distance - start.distance);
            if (start.mode !== "zoom" && distance > Math.max(8, start.distance * 0.05)) {
                start.mode = "zoom";
                this.touchpad.cancel(false);
            }
            if (start.mode === "zoom") {
                this.scale = Math.max(1, Math.min(4, start.scale * current.distance / Math.max(1, start.distance)));
                const bounds = this.stage.getBoundingClientRect(), ratio = this.scale / start.scale;
                // 缩放以双指中心为锚点，中心移动时同步平移，避免画面跳到左上角。
                this.x = current.x - bounds.left - bounds.width / 2 - (start.x - bounds.left - bounds.width / 2 - start.panX) * ratio;
                this.y = current.y - bounds.top - bounds.height / 2 - (start.y - bounds.top - bounds.height / 2 - start.panY) * ratio;
                this.layout();
            } else if (this.touchpad.enabled) {
                this.touchpad.move(event);
            } else {
                this.x = start.panX + current.x - start.x;
                this.y = start.panY + current.y - start.y;
                this.layout();
            }
            return;
        }
        if (this.touchpad.enabled) {
            this.touchpad.move(event);
            return;
        }
        const single = this.single;
        if (!single || touches.length !== 1)
            return;
        const point = this.point(touches[0]);
        single.last = point;
        const dx = point.x - single.point.x, dy = point.y - single.point.y;
        if (!single.down && !single.panning) {
            if (Math.hypot(dx, dy) < 7)
                return;
            clearTimeout(single.hold);
            if (this.scale > 1)
                single.panning = true;
            else {
                single.down = true;
                this.sendPointer("down", single.point);
            }
        }
        if (single.panning) {
            this.x = single.panX + dx;
            this.y = single.panY + dy;
            this.layout();
        } else
            this.sendPointer("move", point);
    }

    end(event) {
        if (this.blocked) {
            if (!event.targetTouches.length)
                this.blocked = false;
            return;
        }
        if (this.multi) {
            if (this.multi.mode !== "zoom" && this.touchpad.enabled)
                this.touchpad.end(event);
            // 捏合结束后剩下的一根手指不能重新变成点击或拖动。
            if (event.targetTouches.length)
                this.multi.finished = true;
            else
                this.multi = undefined;
            return;
        }
        if (this.touchpad.enabled) {
            this.touchpad.end(event);
            return;
        }
        const single = this.single;
        if (!single)
            return;
        clearTimeout(single.hold);
        this.single = undefined;
        delete this.stage.dataset.touchpadGesture;
        if (single.panning)
            return;
        if (!single.down)
            this.sendPointer("down", single.point);
        this.sendPointer("up", single.last);
    }

    layout() {
        const surface = this.surface();
        if (!surface.clientWidth || !surface.clientHeight)
            return;
        const limitX = Math.max(0, (surface.clientWidth * this.scale - this.stage.clientWidth) / 2);
        const limitY = Math.max(0, (surface.clientHeight * this.scale - this.stage.clientHeight) / 2);
        this.x = Math.max(-limitX, Math.min(limitX, this.x));
        this.y = Math.max(-limitY, Math.min(limitY, this.y));
        this.stage.style.setProperty("--view-scale", this.scale);
        this.stage.style.setProperty("--view-x", this.x + "px");
        this.stage.style.setProperty("--view-y", this.y + "px");
        this.changed();
    }

    cancel() {
        clearTimeout(this.single?.hold);
        this.single = this.multi = undefined;
        this.blocked = false;
        // 宿主通过 reset 在控件外释放；取消长按不能变成一次正常点击。
        this.touchpad.cancel(false);
        delete this.stage.dataset.touchpadGesture;
    }

    reset() {
        this.resetInput();
        this.scale = 1;
        this.x = this.y = 0;
        this.layout();
    }
}
