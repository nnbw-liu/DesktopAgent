// 组合输入框的键盘约定：
//   Enter        → 发送（阻止默认换行）
//   Shift+Enter  → 换行（浏览器默认行为）
//   Ctrl+Enter   → 换行（浏览器默认不插入，这里手动插入并同步给 Blazor）
// 用 document 捕获阶段监听，元素重新渲染后依然有效。
// 另外接管文件拖放：把 drop 的文件写进 Blazor 的 file input，触发 change。
window.sdComposer = {
    _handler: null,
    _dragOver: null,
    _dragLeave: null,
    _drop: null,
    // 输入框补全菜单是否打开：打开时方向键 / Enter / Tab / Esc 归菜单，不发送。
    _menuOpen: false,

    attach: function (dotNetRef) {
        this.detach();

        this._handler = function (event) {
            var target = event.target;
            if (!target || typeof target.closest !== 'function' || !target.closest('.composer-text')) {
                return;
            }

            if (this._menuOpen && this._handleMenuKey(event, dotNetRef)) {
                return;
            }

            if (event.key !== 'Enter' || event.isComposing) {
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
        this._attachDrop();
    },

    // 返回 true 表示这个键已经被菜单吃掉。
    _handleMenuKey: function (event, dotNetRef) {
        switch (event.key) {
            case 'ArrowDown':
            case 'ArrowUp':
                event.preventDefault();
                dotNetRef.invokeMethodAsync('MenuMoveAsync', event.key === 'ArrowDown' ? 1 : -1);
                return true;
            case 'Enter':
            case 'Tab':
                if (event.isComposing) {
                    return false;
                }

                event.preventDefault();
                dotNetRef.invokeMethodAsync('MenuCommitAsync');
                return true;
            case 'Escape':
                event.preventDefault();
                dotNetRef.invokeMethodAsync('MenuDismissAsync');
                return true;
            default:
                return false;
        }
    },

    setMenuOpen: function (open) {
        this._menuOpen = !!open;
    },

    caretOf: function (element) {
        return element && typeof element.selectionStart === 'number' ? element.selectionStart : 0;
    },

    // 替换 [start, end) 为 text，并把光标放到末尾；返回替换后的完整文本。
    replaceRange: function (element, start, end, text) {
        if (!element) {
            return '';
        }

        element.focus();
        element.setRangeText(text, start, end, 'end');
        element.dispatchEvent(new Event('input', { bubbles: true }));

        return element.value;
    },

    detach: function () {
        if (this._handler) {
            document.removeEventListener('keydown', this._handler, true);
            this._handler = null;
        }

        if (this._dragOver) {
            document.removeEventListener('dragover', this._dragOver, true);
            this._dragOver = null;
        }

        if (this._dragLeave) {
            document.removeEventListener('dragleave', this._dragLeave, true);
            this._dragLeave = null;
        }

        if (this._drop) {
            document.removeEventListener('drop', this._drop, true);
            this._drop = null;
        }
    },

    // 在光标处插入换行，并触发 input 事件让 Blazor 的 Text 保持同步。
    _insertNewline: function (element) {
        var start = element.selectionStart ?? element.value.length;
        var end = element.selectionEnd ?? start;

        element.setRangeText('\n', start, end, 'end');
        element.dispatchEvent(new Event('input', { bubbles: true }));
    },

    _hasFiles: function (event) {
        var types = event.dataTransfer && event.dataTransfer.types;
        return !!types && Array.prototype.indexOf.call(types, 'Files') >= 0;
    },

    _attachDrop: function () {
        this._dragOver = function (event) {
            if (!this._hasFiles(event)) {
                return;
            }

            var composer = event.target && event.target.closest ? event.target.closest('.composer') : null;
            if (!composer) {
                return;
            }

            event.preventDefault();
            event.dataTransfer.dropEffect = 'copy';
            composer.classList.add('is-dragging');
        }.bind(this);

        this._dragLeave = function (event) {
            var composer = event.target && event.target.closest ? event.target.closest('.composer') : null;
            if (composer && !composer.contains(event.relatedTarget)) {
                composer.classList.remove('is-dragging');
            }
        }.bind(this);

        this._drop = function (event) {
            if (!this._hasFiles(event)) {
                return;
            }

            // 不让 WebView 直接打开被拖入的文件。
            event.preventDefault();

            var composer = event.target && event.target.closest ? event.target.closest('.composer') : null;
            if (!composer) {
                return;
            }

            composer.classList.remove('is-dragging');

            var input = composer.querySelector('input[type="file"]');
            if (!input || !event.dataTransfer.files.length) {
                return;
            }

            var transfer = new DataTransfer();
            for (var i = 0; i < event.dataTransfer.files.length; i++) {
                transfer.items.add(event.dataTransfer.files[i]);
            }

            input.files = transfer.files;
            input.dispatchEvent(new Event('change', { bubbles: true }));
        }.bind(this);

        document.addEventListener('dragover', this._dragOver, true);
        document.addEventListener('dragleave', this._dragLeave, true);
        document.addEventListener('drop', this._drop, true);
    }
};
