define([], function () {
    'use strict';

    // EmbyChapterImageFixer 配置页脚本。
    // 功能单一：一个按钮触发补图，外加几个开关。

    var API_GET = 'ChapterImageFixer/Config';
    var API_SAVE = 'ChapterImageFixer/Config';
    var API_RUN = 'ChapterImageFixer/Run';
    var STYLE_ID = 'cif-style';

    var CSS = [
        '.cif-root{font-family:-apple-system,BlinkMacSystemFont,"Segoe UI","Microsoft YaHei",sans-serif;font-size:15px;color:#333;padding:8px 4px 40px 4px;max-width:980px}',
        '.cif-root *{box-sizing:border-box}',
        '.cif-title{font-size:22px;font-weight:600;margin:0 0 8px}',
        '.cif-sub{color:#777;font-size:13px;margin-bottom:18px;line-height:1.6}',
        '.cif-card{border:1px solid #e2e2e2;border-radius:8px;background:#fff;margin-bottom:18px;overflow:hidden}',
        '.cif-card-head{padding:12px 16px;background:#f7f7f7;border-bottom:1px solid #e2e2e2;font-weight:600}',
        '.cif-card-body{padding:16px}',
        '.cif-row{display:flex;align-items:flex-start;gap:12px;padding:10px 0;border-bottom:1px dashed #eee}',
        '.cif-row:last-child{border-bottom:none}',
        '.cif-row label{display:flex;align-items:center;gap:8px;cursor:pointer;font-weight:500}',
        '.cif-row input[type=checkbox]{width:16px;height:16px;cursor:pointer}',
        '.cif-desc{color:#888;font-size:13px;margin-top:3px;line-height:1.5}',
        '.cif-btn{border:1px solid #ccc;background:#fff;color:#333;border-radius:6px;padding:9px 20px;font-size:14px;cursor:pointer;font-family:inherit}',
        '.cif-btn:hover{background:#f0f0f0}',
        '.cif-btn.cif-primary{background:#22a06b;border-color:#22a06b;color:#fff;font-weight:500}',
        '.cif-btn.cif-primary:hover{background:#1c8b5c}',
        '.cif-btn:disabled{opacity:.5;cursor:default}',
        '.cif-field{margin-bottom:14px}',
        '.cif-label{display:block;font-size:13px;font-weight:500;color:#555;margin-bottom:5px}',
        '#cif-root input[type=text]{width:100%;border:1px solid #ccc;border-radius:6px;padding:8px 10px;font-size:14px;font-family:inherit}',
        '#cif-root input[type=text]:focus{outline:none;border-color:#22a06b}',
        '.cif-hint{color:#999;font-size:12px;margin-top:4px;line-height:1.5}',
        '.cif-progress{margin-top:14px;padding:12px 14px;background:#fafafa;border:1px solid #eee;border-radius:6px;font-size:13px;color:#555;line-height:1.8;white-space:pre-wrap;max-height:260px;overflow-y:auto}',
        '.cif-loading{color:#888;padding:20px;text-align:center}',
        '.cif-toast{position:fixed;right:24px;bottom:24px;padding:11px 20px;border-radius:6px;color:#fff;font-size:14px;z-index:99999;box-shadow:0 4px 16px rgba(0,0,0,.18)}',
        '.cif-toast.ok{background:#22a06b}',
        '.cif-toast.err{background:#c0392b}'
    ].join('\n');

    function injectStyle() {
        if (document.getElementById(STYLE_ID)) return;
        var s = document.createElement('style');
        s.id = STYLE_ID;
        s.appendChild(document.createTextNode(CSS));
        document.head.appendChild(s);
    }

    function el(tag, cls, text) {
        var e = document.createElement(tag);
        if (cls) e.className = cls;
        if (text !== undefined && text !== null) e.textContent = text;
        return e;
    }

    function toast(msg, ok) {
        var t = document.getElementById('cif-toast');
        if (!t) {
            t = el('div', 'cif-toast');
            t.id = 'cif-toast';
            document.body.appendChild(t);
        }
        t.textContent = msg;
        t.className = 'cif-toast ' + (ok ? 'ok' : 'err');
        clearTimeout(t._timer);
        t._timer = setTimeout(function () { t.style.display = 'none'; }, 3000);
        t.style.display = 'block';
    }

    /**
     * Emby 的 API 需要认证，仅带 Cookie 会 401。
     * ApiClient.ajax 会自动把 X-Emby-Token 附加到请求上。
     */
    function request(path, options) {
        options = options || {};
        var api = window.ApiClient;

        if (api && typeof api.ajax === 'function' && typeof api.getUrl === 'function') {
            return api.ajax({
                type: options.method || 'GET',
                url: api.getUrl(path),
                data: options.body,
                contentType: options.body ? 'application/json' : undefined,
                dataType: 'json'
            });
        }

        var token = null;
        if (api) {
            if (typeof api.accessToken === 'function') token = api.accessToken();
            else if (api._userAuthInfo && api._userAuthInfo.AccessToken) token = api._userAuthInfo.AccessToken;
        }
        var url = api && typeof api.getUrl === 'function' ? api.getUrl(path) : '/emby/' + path;
        if (token) url += (url.indexOf('?') >= 0 ? '&' : '?') + 'api_key=' + encodeURIComponent(token);

        return fetch(url, {
            method: options.method || 'GET',
            headers: options.body ? { 'Content-Type': 'application/json', 'Accept': 'application/json' } : { 'Accept': 'application/json' },
            credentials: 'same-origin',
            body: options.body
        }).then(function (r) {
            if (!r.ok) throw new Error('HTTP ' + r.status);
            return r.json();
        });
    }

    function Page(view) {
        this.view = view;
        this.state = null;
    }

    Page.prototype.root = function () {
        return this.view.querySelector('#cif-root');
    };

    Page.prototype.init = function () {
        injectStyle();
        var self = this;
        var tries = 0;
        (function wait() {
            tries++;
            if (window.ApiClient || tries > 30) { self.load(); return; }
            setTimeout(wait, 100);
        })();
    };

    Page.prototype.load = function () {
        var self = this;
        var root = this.root();
        if (!root) return;

        request(API_GET).then(function (cfg) {
            self.state = cfg;
            self.render();
        }).catch(function (err) {
            root.innerHTML = '';
            root.appendChild(el('div', 'cif-desc', '加载配置失败：' + err.message));
        });
    };

    Page.prototype.render = function () {
        var self = this;
        var root = this.root();
        if (!root) return;
        root.innerHTML = '';
        root.className = 'cif-root';

        root.appendChild(el('h1', 'cif-title', 'Emby章节图修复工具'));
        var sub = el('div', 'cif-sub');
        sub.innerHTML = '把已有的缩略图集（bif）引用补到缺少章节图的章节上，包括片头、片尾等标记点。<br>' +
            '不抽帧、不改动媒体库设置；视频旁边已有 bif 的条目会自动按时间点取图。';
        root.appendChild(sub);

        // ---- 选项 ----
        var card = el('div', 'cif-card');
        var head = el('div', 'cif-card-head');
        head.textContent = '选项';
        card.appendChild(head);

        var body = el('div', 'cif-card-body');
        body.appendChild(this.switchRow('Enabled', '启用插件', '关闭后定时任务与手动补图都不会执行。'));
        body.appendChild(this.switchRow('ForceRegenerate', '重生成模式', '打开后覆盖已有章节图，用于修复显示不正常的图片。关闭时只补没有图的章节。'));
        body.appendChild(this.switchRow('VideosOnly', '仅处理视频', '只处理电影、剧集、家庭视频。'));
        body.appendChild(this.switchRow('AllowExtractFallback', '无 bif 时允许抽帧', '默认关闭。条目完全没有 bif 时直接跳过；打开后会尝试用 ffmpeg 抽帧（较耗 CPU，strm 链接无效）。'));
        body.appendChild(this.switchRow('DebugLogging', '调试日志', '把每个条目的处理结果写入 Emby 日志。'));

        var f = el('div', 'cif-field');
        f.appendChild(el('label', 'cif-label', '定时任务间隔（天）'));
        var input = document.createElement('input');
        input.type = 'text';
        input.value = String(this.state.IntervalDays || 7);
        input.addEventListener('input', function () {
            var n = parseInt(input.value, 10);
            self.state.IntervalDays = isNaN(n) ? 7 : n;
        });
        f.appendChild(input);
        f.appendChild(el('div', 'cif-hint', '计划任务按此间隔自动运行。手动运行不受限制。'));
        body.appendChild(f);

        var f2 = el('div', 'cif-field');
        f2.appendChild(el('label', 'cif-label', '每次最多处理（个）'));
        var input2 = document.createElement('input');
        input2.type = 'text';
        input2.value = String(this.state.MaxItemsPerRun || 0);
        input2.addEventListener('input', function () {
            var n = parseInt(input2.value, 10);
            self.state.MaxItemsPerRun = isNaN(n) ? 0 : n;
        });
        f2.appendChild(input2);
        f2.appendChild(el('div', 'cif-hint', '0 表示不限制。抽帧较耗 CPU，条目多时建议设一个上限分批跑。'));
        body.appendChild(f2);

        card.appendChild(body);
        root.appendChild(card);

        // ---- 手动运行 ----
        var runCard = el('div', 'cif-card');
        var runHead = el('div', 'cif-card-head');
        runHead.textContent = '手动运行';
        runCard.appendChild(runHead);

        var runBody = el('div', 'cif-card-body');
        var btnRow = el('div');
        btnRow.style.cssText = 'display:flex;gap:12px;align-items:center;flex-wrap:wrap';

        var saveBtn = el('button', 'cif-btn cif-primary', '保存配置');
        saveBtn.type = 'button';
        saveBtn.addEventListener('click', function () { self.save(); });
        btnRow.appendChild(saveBtn);

        var runBtn = el('button', 'cif-btn', '立即处理');
        runBtn.type = 'button';
        runBtn.addEventListener('click', function () { self.run(runBtn); });
        btnRow.appendChild(runBtn);

        btnRow.appendChild(el('span', 'cif-hint', '按上方设置处理。开启「重生成模式」会覆盖已有章节图。'));
        runBody.appendChild(btnRow);

        this.progressEl = el('div', 'cif-progress');
        this.progressEl.style.display = 'none';
        runBody.appendChild(this.progressEl);

        runCard.appendChild(runBody);
        root.appendChild(runCard);
    };

    Page.prototype.switchRow = function (prop, title, desc) {
        var self = this;
        var row = el('div', 'cif-row');
        var box = el('div');
        var label = el('label');
        var cb = document.createElement('input');
        cb.type = 'checkbox';
        cb.checked = !!self.state[prop];
        cb.addEventListener('change', function () { self.state[prop] = cb.checked; });
        label.appendChild(cb);
        label.appendChild(el('span', null, title));
        box.appendChild(label);
        box.appendChild(el('div', 'cif-desc', desc));
        row.appendChild(box);
        return row;
    };

    Page.prototype.save = function () {
        var self = this;
        var payload = {
            Enabled: !!self.state.Enabled,
            VideosOnly: !!self.state.VideosOnly,
            DebugLogging: !!self.state.DebugLogging,
            ForceRegenerate: !!self.state.ForceRegenerate,
            IntervalDays: self.state.IntervalDays || 7,
            MaxItemsPerRun: self.state.MaxItemsPerRun || 0,
            AllowExtractFallback: !!self.state.AllowExtractFallback
        };

        request(API_SAVE, { method: 'POST', body: JSON.stringify(payload) })
            .then(function (res) {
                if (res && res.Success) toast('配置已保存', true);
                else toast('保存失败：' + ((res && res.Message) || '未知错误'), false);
            })
            .catch(function (err) { toast('保存失败：' + err.message, false); });
    };

    Page.prototype.run = function (btn) {
        var self = this;
        btn.disabled = true;
        btn.textContent = '处理中…';

        var p = this.progressEl;
        p.style.display = 'block';
        p.textContent = '开始扫描…';

        // 运行时沿用界面上当前勾选的状态，不要求先保存
        var payload = {
            ForceRegenerate: !!self.state.ForceRegenerate,
            Limit: self.state.MaxItemsPerRun || 0
        };

        request(API_RUN, { method: 'POST', body: JSON.stringify(payload) })
            .then(function (res) {
                var lines = [];
                if (res && res.Message) lines.push(res.Message);
                if (res && res.Details) lines = lines.concat(res.Details);
                if (!lines.length) lines.push('完成');
                p.textContent = lines.join('\n');

                if (res && res.Success) toast('处理完成', true);
                else toast('处理结束：' + ((res && res.Message) || '见下方详情'), false);
            })
            .catch(function (err) {
                p.textContent = '执行失败：' + err.message;
                toast('执行失败：' + err.message, false);
            })
            .then(function () {
                btn.disabled = false;
                btn.textContent = '立即处理';
            });
    };

    return function (view) {
        if (!view._cifPage) view._cifPage = new Page(view);
        view._cifPage.init();

        view.addEventListener('viewdestroy', function () {
            view._cifPage = null;
        });
    };
});
