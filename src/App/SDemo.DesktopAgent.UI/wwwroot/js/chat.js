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

    // 读取聊天区里的划词选区：只接受「同一条消息的正文内」、非空的选择。
    // 返回视口坐标，供 position: fixed 的浮动条直接使用；无效选择返回 null。
    readSelection: function (container) {
        var selection = window.getSelection();

        if (!selection || selection.isCollapsed || selection.rangeCount === 0) {
            return null;
        }

        var text = selection.toString();

        if (!text || !text.trim()) {
            return null;
        }

        var range = selection.getRangeAt(0);
        var common = range.commonAncestorContainer;

        if (!container || !container.contains(common)) {
            return null;
        }

        var startElement = range.startContainer.nodeType === 1
            ? range.startContainer
            : range.startContainer.parentElement;
        var endElement = range.endContainer.nodeType === 1
            ? range.endContainer
            : range.endContainer.parentElement;

        if (!startElement || !endElement) {
            return null;
        }

        var startMessage = startElement.closest ? startElement.closest('[data-message-id]') : null;
        var endMessage = endElement.closest ? endElement.closest('[data-message-id]') : null;

        // v1 只处理单条消息内的选区：跨消息的锚点、历史切片都不明确。
        if (!startMessage || startMessage !== endMessage) {
            return null;
        }

        // 只认正文区域，避免选中时间 / 按钮 / 操作栏也弹插件菜单。
        if (!startElement.closest('.msg-body') && !endElement.closest('.msg-body')) {
            return null;
        }

        var rect = range.getBoundingClientRect();

        if (!rect || (rect.width === 0 && rect.height === 0)) {
            return null;
        }

        // 预留浮动条宽度，靠近右边时往左收，保证整条菜单在视口内。
        var estimatedWidth = 360;
        var left = Math.min(
            Math.max(8, rect.left),
            Math.max(8, window.innerWidth - estimatedWidth - 8));

        return {
            text: text,
            messageId: startMessage.getAttribute('data-message-id'),
            left: left,
            top: rect.top,
            bottom: rect.bottom,
            placeAbove: rect.bottom + 200 > window.innerHeight
        };
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
