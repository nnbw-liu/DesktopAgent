// 组合输入框的键盘约定：
//   Enter        → 发送（阻止默认换行）
//   Shift+Enter  → 换行（浏览器默认行为）
//   Ctrl+Enter   → 换行（浏览器默认不插入，这里手动插入并同步给 Blazor）
// 用 document 捕获阶段监听，元素重新渲染后依然有效。
window.sdComposer = {
    _handler: null,

    attach: function (dotNetRef) {
        this.detach();

        this._handler = function (event) {
            if (event.key !== 'Enter' || event.isComposing) {
                return;
            }

            var target = event.target;
            if (!target || typeof target.closest !== 'function' || !target.closest('.composer-text')) {
                return;
            }

            if (event.shiftKey) {
                return;
            }

            event.preventDefault();

            if (event.ctrlKey) {
                this._insertNewline(target);
                return;
            }

            if (event.repeat) {
                return;
            }

            dotNetRef.invokeMethodAsync('HandleEnterAsync');
        }.bind(this);

        document.addEventListener('keydown', this._handler, true);
    },

    detach: function () {
        if (this._handler) {
            document.removeEventListener('keydown', this._handler, true);
            this._handler = null;
        }
    },

    // 在光标处插入换行，并触发 input 事件让 Blazor 的 Text 保持同步。
    _insertNewline: function (element) {
        var start = element.selectionStart ?? element.value.length;
        var end = element.selectionEnd ?? start;

        element.setRangeText('\n', start, end, 'end');
        element.dispatchEvent(new Event('input', { bubbles: true }));
    }
};
