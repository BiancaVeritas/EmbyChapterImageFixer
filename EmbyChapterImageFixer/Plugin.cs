using System;
using System.Collections.Generic;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace EmbyChapterImageFixer
{
    /// <summary>
    /// 插件入口。
    ///
    /// 配置页通过 IHasWebPages 注册 HTML 与脚本两个页面：
    ///  - HTML 里不能放 &lt;script&gt;，否则服务端 PackageCreator.ModifyHtml
    ///    会走注释脚本分支并抛异常，请求返回 500。
    ///  - 根元素必须带 class="view"，前端靠它定位根视图。
    ///  - data-title 决定顶部导航栏显示的标题。
    /// </summary>
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
    {
        public static readonly Guid PluginId = new Guid("9a2e6b14-7c3d-4f52-8d91-3b6c5e0f7a24");

        /// <summary>配置页名，必须全小写。</summary>
        public const string PageName = "chapterimagefixer";

        /// <summary>脚本页名，与 HTML 的 data-controller 对应。</summary>
        public const string ScriptPageName = "cifjs";

        private const string HtmlResource = "EmbyChapterImageFixer.Web.config.html";
        private const string JsResource = "EmbyChapterImageFixer.Web.config.js";

        public static Plugin Instance { get; private set; }

        private readonly ILogger _logger;

        public Plugin(
            IApplicationHost applicationHost,
            ILogManager logManager,
            IApplicationPaths applicationPaths,
            IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            _logger = logManager?.GetLogger("ChapterImageFixer");

            // 注意：这里不能访问 Configuration / ConfigurationFilePath。
            // BasePlugin<T>.ConfigurationFilePath 依赖 AssemblyFileName ->
            // base.AssemblyFilePath，后者要等 Emby 调用 SetAttributes() 后才赋值，
            // 构造函数里访问会抛 ArgumentNullException 导致插件创建失败。
            Log("插件已加载。");
        }

        public override string Name => "EmbyChapterImageFixer";

        public override Guid Id => PluginId;

        public override string Description =>
            "为缺失章节图的视频补上章节图。只补空缺，不覆盖已有图片。";

        public IEnumerable<PluginPageInfo> GetPages()
        {
            return new[]
            {
                new PluginPageInfo
                {
                    Name = PageName,
                    EmbeddedResourcePath = HtmlResource,
                    DisplayName = "EmbyChapterImageFixer",
                    EnableInMainMenu = false,
                    EnableInUserMenu = false,
                    IsMainConfigPage = true
                },
                new PluginPageInfo
                {
                    Name = ScriptPageName,
                    EmbeddedResourcePath = JsResource,
                    EnableInMainMenu = false,
                    EnableInUserMenu = false,
                    IsMainConfigPage = false
                }
            };
        }

        public PluginConfiguration Options => Configuration ?? new PluginConfiguration();

        /// <summary>
        /// 替换配置并落盘。BasePlugin&lt;T&gt;.Configuration 的 setter 是 protected。
        /// </summary>
        public bool UpdateOptions(PluginConfiguration configuration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            Configuration = configuration;

            try
            {
                if (string.IsNullOrEmpty(AssemblyFilePath))
                {
                    Log("插件尚未完成初始化，本次不落盘。");
                    return false;
                }

                SaveConfiguration();
                return true;
            }
            catch (Exception ex)
            {
                LogError("保存配置失败", ex);
                return false;
            }
        }

        public void Log(string message) => _logger?.Info("[ChapterImageFixer] " + message);

        public void LogError(string message, Exception ex = null)
        {
            if (ex == null) _logger?.Error("[ChapterImageFixer] " + message);
            else _logger?.ErrorException("[ChapterImageFixer] " + message, ex);
        }
    }
}
