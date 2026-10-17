window.palkiaScrollToBottom = function (elementId) {
    var el = document.getElementById(elementId);
    if (el) {
        el.scrollTop = el.scrollHeight;
    }
};
