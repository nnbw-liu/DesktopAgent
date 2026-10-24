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

            // 菜单打开时按键归菜单：鼠标点过候选行之后焦点可能已经不在输入框里
            // （行被移除时还会落到 body），此时若按「焦点必须在 .composer-text」筛掉，
            // ↑↓ / Enter / Tab / Esc 就全部失灵，菜单也关不掉。
            // 只接管「输入框内 / 焦点已丢失」这两种情形，别的输入控件不抢。
            if (this._menuOpen && this._isMenuScope(target) && this._handleMenuKey(event, dotNetRef)) {
                return;
            }

            if (!target || typeof target.closest !== 'function' || !target.closest('.composer-text')) {
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

    // 菜单打开时，这个按键该不该归菜单：输入框内、或者焦点已经丢了（行被移除后落到 body）。
    // 焦点在别的输入控件里就不抢——用户可能已经离开输入框了。
    _isMenuScope: function (target) {
        if (!target || target === document.body) {
            return true;
        }

        return typeof target.closest === 'function' && !!target.closest('.composer');
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

    // 把输入框的文本与光标同步到宿主算好的结果。
    //
    // 文本**只由 C# 决定**（界面、测试、回灌拿到同一份），JS 不再按 start/end 做二次插入：
    // 之前用 setRangeText 在这里又插了一遍，而 Blazor 的重渲染已经把 textarea 更新成新文本，
    // 于是 Tab/Enter 确认候选后会出现「@文件 文件」这种重复尾巴。
    //
    // text 传 null 表示不动文本，只摆光标（普通插入之后用得到）。
    applyText: function (element, value, caret) {
        if (!element) {
            return;
        }

        if (typeof value === 'string' && element.value !== value) {
            element.value = value;
        }

        if (document.activeElement !== element) {
            element.focus({ preventScroll: true });
        }

        var length = typeof element.value === 'string' ? element.value.length : 0;
        var position = typeof caret === 'number' && caret >= 0 ? Math.min(caret, length) : length;
        element.setSelectionRange(position, position);
    },

    // 把焦点还给输入框：用鼠标点候选行会先把焦点给到按钮，行一消失焦点就落到 body，
    // 之后 Enter 既不是「发送」也不是「确认」。已经在输入框里时什么都不做，
    // 避免把光标从中间位置拽到末尾。
    focusInput: function (element) {
        if (!element || document.activeElement === element) {
            return;
        }

        element.focus({ preventScroll: true });

        var end = typeof element.value === 'string' ? element.value.length : 0;
        element.setSelectionRange(end, end);
    },

    // 输入框滚动时，高亮层跟着滚；出现滚动条时再补上右侧留白，两边的换行位置才对得上。
    syncScroll: function (element, highlight) {
        if (!element || !highlight) {
            return;
        }

        highlight.scrollTop = element.scrollTop;
        highlight.scrollLeft = element.scrollLeft;

        // offsetWidth - clientWidth 就是竖向滚动条宽度（没有滚动条时是 0）。
        var gutter = element.offsetWidth - element.clientWidth;
        highlight.style.paddingRight = gutter > 0 ? (16 + gutter) + 'px' : '';
    },

    detach: function () {
        // 组件卸载时菜单状态跟着复位：否则下一个输入框会以为菜单还开着，把 Enter 吃掉。
        this._menuOpen = false;

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
