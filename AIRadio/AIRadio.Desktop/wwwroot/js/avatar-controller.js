// Avatar Controller - Live2D Cubism Web SDK 封装
(function () {
    'use strict';

    const AvatarController = {
        model: null,
        modelPath: null,
        canvas: null,
        gl: null,
        textures: [],
        currentExpression: 'idle',
        currentMotion: 'idle',
        isModelLoaded: false,

        // 初始化
        init: function (canvasId) {
            this.canvas = document.getElementById(canvasId);
            if (!this.canvas) {
                this.canvas = document.createElement('canvas');
                this.canvas.id = 'live2d-canvas';
                document.body.appendChild(this.canvas);
            }
            this.resizeCanvas();
            window.addEventListener('resize', () => this.resizeCanvas());
            this.setStatus('就绪');
        },

        // 加载模型
        loadModel: function (modelDir) {
            this.setStatus('加载模型: ' + modelDir);
            this.modelPath = modelDir;

            // 从 C# 获取模型配置
            return fetch('./assets/models/' + modelDir + '/model.json')
                .then(response => {
                    if (!response.ok) throw new Error('模型配置不存在');
                    return response.json();
                })
                .then(modelJson => {
                    // 加载纹理
                    const promises = [];
                    if (modelJson.textures) {
                        modelJson.textures.forEach(texPath => {
                            const img = new Image();
                            const texPromise = new Promise((resolve, reject) => {
                                img.onload = () => {
                                    this.textures.push(img);
                                    resolve(img);
                                };
                                img.onerror = reject;
                            });
                            img.src = './assets/models/' + modelDir + '/' + texPath;
                            promises.push(texPromise);
                        });
                    }
                    return Promise.all(promises);
                })
                .then(() => {
                    this.isModelLoaded = true;
                    this.setStatus('模型加载完成');
                    this.reportToCSharp('model_loaded', { path: modelDir });
                    this.startIdleAnimation();
                })
                .catch(err => {
                    this.setStatus('模型加载失败: ' + err.message);
                    this.reportToCSharp('error', { message: err.message });
                });
        },

        // 设置表情
        setExpression: function (expressionName) {
            if (!this.isModelLoaded) return;
            this.currentExpression = expressionName;
            this.setStatus('表情: ' + expressionName);
            this.animateExpression(expressionName);
        },

        // 播放动作
        playMotion: function (motionName) {
            if (!this.isModelLoaded) return;
            this.currentMotion = motionName;
            this.setStatus('动作: ' + motionName);
            this.animateMotion(motionName);
        },

        // 口型同步
        updateLipSync: function (spectrumData) {
            // 简化实现：基于音频频谱数据驱动口型
            if (!this.isModelLoaded || !spectrumData || spectrumData.length === 0) return;

            // 计算音量
            let sum = 0;
            for (let i = 0; i < spectrumData.length; i++) {
                sum += spectrumData[i];
            }
            const avg = sum / spectrumData.length;

            // 驱动口型（这里简化处理）
            this.animateLipSync(avg);
        },

        // 报告给 C#
        reportToCSharp: function (type, data) {
            try {
                const message = JSON.stringify({ type: type, ...data });
                if (window.chrome && window.chrome.webview) {
                    window.chrome.webview.postMessage(message);
                } else if (window.webkit && window.webkit.messageHandlers) {
                    window.webkit.messageHandlers.airsis.postMessage(message);
                }
            } catch (e) {
                console.error('Failed to report to C#:', e);
            }
        },

        // 内部方法
        resizeCanvas: function () {
            if (!this.canvas) return;
            this.canvas.width = window.innerWidth;
            this.canvas.height = window.innerHeight;
        },

        setStatus: function (text) {
            const statusEl = document.getElementById('status');
            if (statusEl) statusEl.textContent = text;
        },

        animateExpression: function (expressionName) {
            // 简化实现：颜色变化模拟表情
            const canvas = this.canvas;
            if (!canvas) return;

            // 根据表情名称改变背景色调（实际项目中应操作 Live2D 模型）
            const colors = {
                'idle': 'rgba(100,200,255,0.1)',
                'happy': 'rgba(255,220,100,0.15)',
                'sad': 'rgba(100,150,200,0.1)',
                'excited': 'rgba(255,180,100,0.2)'
            };
            canvas.style.background = colors[expressionName] || colors['idle'];
        },

        animateMotion: function (motionName) {
            // 简化实现：canvas 震动模拟动作
            if (motionName === 'wave' || motionName === 'jump') {
                this.canvas.classList.add('motion-' + motionName);
                setTimeout(() => {
                    this.canvas.classList.remove('motion-wave', 'motion-jump');
                    this.reportToCSharp('motion_finished', { name: motionName });
                }, 500);
            }
        },

        animateLipSync: function (intensity) {
            // 简化实现：canvas 透明度变化模拟口型
            this.canvas.style.opacity = 0.95 + (intensity * 0.05);
        },

        startIdleAnimation: function () {
            // 简化实现：轻微摇动
            let angle = 0;
            const animate = () => {
                if (!this.isModelLoaded) return;
                angle += 0.02;
                this.canvas.style.transform = `rotate(${(Math.sin(angle) * 1.5).toFixed(2)}deg)`;
                requestAnimationFrame(animate);
            };
            animate();
        }
    };

    // 导出
    window.avatar = AvatarController;

    // 初始化
    window.addEventListener('DOMContentLoaded', () => {
        AvatarController.init('live2d-canvas');
    });
})();