using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;

namespace EmbyChapterImageFixer
{
    /// <summary>
    /// 扫描媒体库并补全 / 重生成章节图。
    /// 被计划任务和手动运行共用。
    /// </summary>
    public class ChapterImageFixerService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly IMediaSourceManager _mediaSourceManager;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger _logger;

        /// <summary>同一时间只允许一个处理流程。</summary>
        private static readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        public ChapterImageFixerService(
            ILibraryManager libraryManager,
            IItemRepository itemRepository,
            IMediaSourceManager mediaSourceManager,
            IFileSystem fileSystem,
            ILogManager logManager)
        {
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _mediaSourceManager = mediaSourceManager;
            _fileSystem = fileSystem;
            _logger = logManager?.GetLogger("ChapterImageFixer");
        }

        /// <summary>
        /// 执行一次处理。
        ///
        /// 参数均为临时覆盖，便于从界面「立即补图」时指定本次行为，
        /// 不修改已保存的配置。
        /// </summary>
        public async Task<RunResult> RunAsync(
            CancellationToken ct,
            bool? forceRegenerateOverride = null,
            int limitOverride = 0)
        {
            var cfg = Plugin.Instance?.Options ?? new PluginConfiguration();

            var result = new RunResult { Success = true };
            if (!cfg.Enabled)
            {
                result.Success = false;
                result.Message = "插件已关闭，未执行。";
                return result;
            }

            if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
            {
                result.Success = false;
                result.Message = "已有任务正在运行，请稍后再试。";
                return result;
            }

            try
            {
                var sw = Stopwatch.StartNew();
                var forceRegenerate = forceRegenerateOverride ?? cfg.ForceRegenerate;
                var limit = limitOverride > 0 ? limitOverride : cfg.MaxItemsPerRun;

                var fixer = new ChapterImageFixer(_libraryManager, _itemRepository, _mediaSourceManager, _fileSystem, _logger);

                var all = GetCandidateVideos(cfg);

                var targets = new List<Video>();
                foreach (var video in all)
                {
                    ct.ThrowIfCancellationRequested();

                    List<MediaBrowser.Model.Entities.ChapterInfo> chapters;
                    int missing;
                    if (fixer.NeedsFix(video, forceRegenerate, out chapters, out missing))
                    {
                        targets.Add(video);
                    }
                }

                var modeText = forceRegenerate ? "重生成" : "补缺";
                result.Details.Add($"检查 {all.Count} 个条目，{modeText}模式，待处理 {targets.Count} 个。");

                if (limit > 0 && targets.Count > limit)
                {
                    result.Details.Add($"本次最多处理 {limit} 个，其余留待下次。");
                    targets = targets.Take(limit).ToList();
                }

                _logger?.Info($"[ChapterImageFixer] 检查 {all.Count}，待处理 {targets.Count}（{modeText}）。");

                int done = 0, failed = 0;

                foreach (var video in targets)
                {
                    ct.ThrowIfCancellationRequested();

                    var trace = cfg.DebugLogging
                        ? new Action<string>(m => _logger?.Info("[ChapterImageFixer][调试] " + m))
                        : null;

                    try
                    {
                        var ok = await fixer.ProcessAsync(video, forceRegenerate, trace, ct).ConfigureAwait(false);
                        if (ok)
                        {
                            done++;
                            result.Details.Add($"✓ {video.Name}");
                        }
                        else
                        {
                            result.Details.Add($"· {video.Name}（未产生变化）");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        _logger?.ErrorException("[ChapterImageFixer] 处理失败：" + video.Name, ex);
                        result.Details.Add($"✗ {video.Name}（{ex.Message}）");
                    }
                }

                sw.Stop();
                result.Message = $"完成：成功 {done} 个，失败 {failed} 个，耗时 {sw.Elapsed.TotalSeconds:F0} 秒。";
                _logger?.Info("[ChapterImageFixer] " + result.Message);

                return result;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// 取出候选视频条目。
        /// 由 ChapterImageFixer.NeedsFix 逐个精确判断是否真的需要处理。
        /// </summary>
        private List<Video> GetCandidateVideos(PluginConfiguration cfg)
        {
            var query = new InternalItemsQuery
            {
                Recursive = true,
                IsFolder = false
            };

            if (cfg.VideosOnly)
            {
                query.MediaTypes = new[] { "Video" };
            }

            var items = _libraryManager.GetItemList(query) ?? Array.Empty<BaseItem>();

            var result = new List<Video>();
            foreach (var item in items)
            {
                var video = item as Video;
                if (video == null) continue;

                // 主题曲、预告等附加内容不处理
                if (video.ExtraType != null) continue;
                if (video.IsThemeMedia) continue;

                // 刻意不按扩展名或文件路径过滤：
                // 本插件补的是「章节记录里指向 bif 的 ImagePath」，
                // 与视频文件本身是 mp4 / mkv / strm 无关。
                // bif 是独立放在视频旁边的缩略图集文件。

                result.Add(video);
            }

            return result;
        }
    }
}
