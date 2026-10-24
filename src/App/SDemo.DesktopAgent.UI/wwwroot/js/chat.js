// 聊天区辅助脚本：滚动、复制。
window.sdChat = {
    // 贴底滚动：只有用户本来就在底部时才跟随新内容；手动往上翻后不再被拽回去。
    scrollToBottom: function (element) {
        if (!element) {
            return;
        }

        var state = element.__sdChatScroll;

        if (!state) {
            state = { stick: true, bound: false };
            element.__sdChatScroll = state;

            element.addEventListener('scroll', function () {
                var distance = element.scrollHeight - element.scrollTop - element.clientHeight;
                state.stick = distance <= 48;
            }, { passive: true });
        }

        if (state.stick) {
            element.scrollTop = element.scrollHeight;
        }
    },

    // 显式跳到底部（例如用户发了新消息），并把贴底状态重新打开。
    jumpToBottom: function (element) {
        if (!element) {
            return;
        }

        if (element.__sdChatScroll) {
            element.__sdChatScroll.stick = true;
        }

        element.scrollTop = element.scrollHeight;
    },

    copyText: async function (text) {
        if (!text) {
            return;
        }

        try {
            if (navigator.clipboard && window.isSecureContext) {
                await navigator.clipboard.writeText(text);
                return;
            }
        } catch (error) {
            // 权限被拒时走下面的 fallback。
        }

        var area = document.createElement('textarea');
        area.value = text;
        area.setAttribute('readonly', '');
        area.style.position = 'fixed';
        area.style.top = '-1000px';
        area.style.opacity = '0';
        document.body.appendChild(area);
        area.select();

        try {
            document.execCommand('copy');
        } finally {
            area.remove();
        }
    }
};
