using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Services;

namespace EmbyChapterImageFixer
{
    /// <summary>插件配置，由 Emby 负责读写 XML。</summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        public bool Enabled { get; set; } = true;

        /// <summary>只处理视频（电影/剧集/家庭视频）。</summary>
        public bool VideosOnly { get; set; } = true;

        public bool DebugLogging { get; set; }

        /// <summary>
        /// 重生成模式：覆盖已有章节图。
        /// 关闭时只补没有图的章节；打开时会重新生成所有命中条目的章节图。
        /// </summary>
        public bool ForceRegenerate { get; set; }

        /// <summary>每次最多处理多少个条目，0 表示不限制。防止一次跑太久。</summary>
        public int MaxItemsPerRun { get; set; }

        /// <summary>
        /// 当条目完全没有可用的 bif 时，是否允许调用 Emby 抽帧兜底。
        /// 默认关闭：本插件只做「把已有 bif 引用补到缺图章节」这一件事。
        /// 打开后会对完全没有 bif 的条目尝试抽帧（较耗 CPU；strm 链接文件无实体视频，会跳过）。
        /// </summary>
        public bool AllowExtractFallback { get; set; }
    }

    // ============================================================
    // API：读取与保存
    // ============================================================

    [Route("/ChapterImageFixer/Config", "GET", Summary = "获取章节图插件配置")]
    public class GetConfig : IReturn<ConfigResult>
    {
    }

    public class ConfigResult
    {
        public bool Enabled { get; set; }
        public bool VideosOnly { get; set; }
        public bool DebugLogging { get; set; }
        public bool ForceRegenerate { get; set; }
        public int MaxItemsPerRun { get; set; }
        public bool AllowExtractFallback { get; set; }
    }

    public class ConfigService : IService
    {
        public object Get(GetConfig request)
        {
            var cfg = Plugin.Instance?.Options ?? new PluginConfiguration();
            return new ConfigResult
            {
                Enabled = cfg.Enabled,
                VideosOnly = cfg.VideosOnly,
                DebugLogging = cfg.DebugLogging,
                ForceRegenerate = cfg.ForceRegenerate,
                MaxItemsPerRun = cfg.MaxItemsPerRun,
                AllowExtractFallback = cfg.AllowExtractFallback
            };
        }
    }

    [Route("/ChapterImageFixer/Config", "POST", Summary = "保存章节图插件配置")]
    public class SaveConfig : IReturn<SaveResult>
    {
        public bool Enabled { get; set; }
        public bool VideosOnly { get; set; }
        public bool DebugLogging { get; set; }
        public bool ForceRegenerate { get; set; }
        public int MaxItemsPerRun { get; set; }
        public bool AllowExtractFallback { get; set; }
    }

    public class SaveResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    public class SaveConfigService : IService
    {
        public object Post(SaveConfig request)
        {
            try
            {
                var plugin = Plugin.Instance;
                if (plugin == null)
                {
                    return new SaveResult { Success = false, Message = "插件尚未初始化" };
                }

                var cfg = new PluginConfiguration
                {
                    Enabled = request.Enabled,
                    VideosOnly = request.VideosOnly,
                    DebugLogging = request.DebugLogging,
                    ForceRegenerate = request.ForceRegenerate,
                    MaxItemsPerRun = request.MaxItemsPerRun > 0 ? request.MaxItemsPerRun : 0,
                    AllowExtractFallback = request.AllowExtractFallback
                };

                plugin.UpdateOptions(cfg);
                return new SaveResult { Success = true, Message = "已保存" };
            }
            catch (Exception ex)
            {
                Plugin.Instance?.LogError("保存配置失败", ex);
                return new SaveResult { Success = false, Message = ex.Message };
            }
        }
    }

    // ============================================================
    // API：立即运行
    // ============================================================

    [Route("/ChapterImageFixer/Run", "POST", Summary = "立即处理章节图")]
    public class RunNow : IReturn<RunResult>
    {
        /// <summary>临时覆盖是否重生（null 表示用配置里的值）。</summary>
        public bool? ForceRegenerate { get; set; }

        /// <summary>临时限制本次处理数量，0 = 用配置。</summary>
        public int Limit { get; set; }
    }

    public class RunResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public List<string> Details { get; set; } = new List<string>();
    }

    public class RunService : IService
    {
        private readonly ChapterImageFixerService _runner;

        public RunService(ChapterImageFixerService runner)
        {
            _runner = runner;
        }

        public object Post(RunNow request)
        {
            try
            {
                var result = _runner.RunAsync(
                        System.Threading.CancellationToken.None,
                        request?.ForceRegenerate,
                        request?.Limit ?? 0)
                    .GetAwaiter().GetResult();
                return result;
            }
            catch (Exception ex)
            {
                Plugin.Instance?.LogError("手动处理失败", ex);
                return new RunResult { Success = false, Message = ex.Message };
            }
        }
    }

    // ============================================================
    // API：诊断
    // ============================================================

    [Route("/ChapterImageFixer/Diagnose", "GET", Summary = "诊断条目章节图状态")]
    public class Diagnose : IReturn<DiagnoseResult>
    {
        public long ItemId { get; set; }
    }

    public class DiagnoseResult
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public bool SupportsThumbnails { get; set; }
        public int ChapterCount { get; set; }
        public int WithImageTag { get; set; }
        public int WithImagePath { get; set; }
        public bool NeedsFix { get; set; }
        public List<string> ChapterDetails { get; set; } = new List<string>();
    }

    public class DiagnoseService : IService
    {
        private readonly MediaBrowser.Controller.Library.ILibraryManager _libraryManager;
        private readonly MediaBrowser.Controller.Persistence.IItemRepository _repo;
        private readonly MediaBrowser.Controller.Library.IMediaSourceManager _mediaSourceManager;
        private readonly MediaBrowser.Model.IO.IFileSystem _fileSystem;
        private readonly MediaBrowser.Model.Logging.ILogManager _logManager;

        public DiagnoseService(
            MediaBrowser.Controller.Library.ILibraryManager libraryManager,
            MediaBrowser.Controller.Persistence.IItemRepository repo,
            MediaBrowser.Controller.Library.IMediaSourceManager mediaSourceManager,
            MediaBrowser.Model.IO.IFileSystem fileSystem,
            MediaBrowser.Model.Logging.ILogManager logManager)
        {
            _libraryManager = libraryManager;
            _repo = repo;
            _mediaSourceManager = mediaSourceManager;
            _fileSystem = fileSystem;
            _logManager = logManager;
        }

        public object Get(Diagnose request)
        {
            var item = _libraryManager.GetItemById(request.ItemId) as MediaBrowser.Controller.Entities.Video;
            if (item == null) return new DiagnoseResult { Name = "(未找到)" };

            var result = new DiagnoseResult
            {
                Name = item.Name,
                Path = item.Path,
                SupportsThumbnails = item.SupportsThumbnails
            };

            var chapters = _repo.GetChapters(item) ?? new List<MediaBrowser.Model.Entities.ChapterInfo>();
            result.ChapterCount = chapters.Count;
            result.WithImageTag = chapters.Count(c => !string.IsNullOrEmpty(c.ImageTag));
            result.WithImagePath = chapters.Count(c => !string.IsNullOrEmpty(c.ImagePath));

            var fixer = new ChapterImageFixer(
                _libraryManager, _repo, _mediaSourceManager, _fileSystem, _logManager?.GetLogger("ChapterImageFixer"));

            // 用与插件相同的判定：文件不存在也算需要修复
            result.NeedsFix = chapters.Count(c => fixer.ChapterNeedsFix(c)) > 0;

            foreach (var c in chapters)
            {
                var exists = string.IsNullOrEmpty(c.ImagePath) ? (bool?)null : _fileSystem.FileExists(c.ImagePath);
                result.ChapterDetails.Add(
                    $"Name={c.Name} ImageTag='{c.ImageTag}' 文件存在={exists} Path='{c.ImagePath}'");
            }

            return result;
        }
    }
}
