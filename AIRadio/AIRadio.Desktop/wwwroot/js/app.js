// App - C# 通信层
(function () {
    'use strict';

    // 监听来自 C# 的消息
    window.addEventListener('message', function (event) {
        try {
            const data = typeof event.data === 'string' ? JSON.parse(event.data) : event.data;
            handleCommand(data);
        } catch (e) {
            console.error('Failed to parse message:', e);
        }
    });

    // WebView2 特定
    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.addEventListener('message', function (event) {
            try {
                const data = JSON.parse(event.data);
                handleCommand(data);
            } catch (e) {
                console.error('Failed to parse webview message:', e);
            }
        });
    }

    function handleCommand(data) {
        switch (data.command) {
            case 'loadModel':
                window.avatar.loadModel(data.modelPath);
                break;
            case 'setExpression':
                window.avatar.setExpression(data.expression);
                break;
            case 'playMotion':
                window.avatar.playMotion(data.motion);
                break;
            case 'updateLipSync':
                window.avatar.updateLipSync(data.spectrum);
                break;
            default:
                console.warn('Unknown command:', data);
        }
    }

    // 提供给 C# 调用的 API
    window.loadModel = function (modelPath) {
        window.avatar.loadModel(modelPath);
    };

    window.setExpression = function (expressionName) {
        window.avatar.setExpression(expressionName);
    };

    window.playMotion = function (motionName) {
        window.avatar.playMotion(motionName);
    };

    window.updateLipSync = function (spectrumData) {
        window.avatar.updateLipSync(spectrumData);
    };

    console.log('AI Radio Web App initialized');
})();