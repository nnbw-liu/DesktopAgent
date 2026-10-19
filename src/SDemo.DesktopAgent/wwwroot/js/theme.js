// 主题桥接：由 AppShell 通过 JS 互操作调用，把 WPF 侧的系统主题应用到 Web 界面。
window.sdTheme = {
    apply: function (theme) {
        var root = document.documentElement;
        root.dataset.theme = theme;
        root.style.colorScheme = theme;
    }
};
