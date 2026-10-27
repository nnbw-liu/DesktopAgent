// 设置弹窗的键盘辅助：Esc 关闭，Tab 在面板内循环。
// 用 window 级监听，避免焦点落在输入框 / 按钮上时收不到 Escape。
(function () {
    let handler = null;

    function focusable(dialog) {
        return Array.from(dialog.querySelectorAll(
            'summary, button, input, select, textarea, a[href], [tabindex]:not([tabindex="-1"])'))
            .filter((element) => !element.disabled && element.getClientRects().length > 0)
            // 收起的卡片正文不参与 Tab 循环（summary 本身仍是可聚焦的开关）。
            .filter((element) => !element.closest('details:not([open])') || element.matches('summary'));
    }

    window.sdSettings = {
        bind(dotNetRef) {
            window.sdSettings.unbind();

            handler = function (event) {
                if (event.key === 'Escape' && !event.defaultPrevented) {
                    event.preventDefault();
                    dotNetRef.invokeMethodAsync('EscapeAsync');
                    return;
                }

                if (event.key !== 'Tab' || event.defaultPrevented) {
                    return;
                }

                const dialog = document.querySelector('.settings-dialog');
                if (!dialog) {
                    return;
                }

                const items = focusable(dialog);
                if (items.length === 0) {
                    return;
                }

                const first = items[0];
                const last = items[items.length - 1];
                const active = document.activeElement;

                // 焦点不在弹窗内（例如落在 body 上）：先把 Tab 收回到面板。
                if (!dialog.contains(active)) {
                    event.preventDefault();
                    first.focus();
                    return;
                }

                // 焦点还在面板容器本身（刚打开时）：Shift+Tab 不能退到遮罩后面的侧栏。
                if (active === dialog) {
                    event.preventDefault();
                    (event.shiftKey ? last : first).focus();
                    return;
                }

                if (event.shiftKey && active === first) {
                    event.preventDefault();
                    last.focus();
                } else if (!event.shiftKey && active === last) {
                    event.preventDefault();
                    first.focus();
                }
            };

            window.addEventListener('keydown', handler, false);
        },

        unbind() {
            if (handler) {
                window.removeEventListener('keydown', handler, false);
                handler = null;
            }
        }
    };
})();
