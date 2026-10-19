// 聊天区辅助脚本：新消息后滚动到底部。
window.sdChat = {
    scrollToBottom: function (element) {
        if (!element) {
            return;
        }

        element.scrollTop = element.scrollHeight;
    }
};
