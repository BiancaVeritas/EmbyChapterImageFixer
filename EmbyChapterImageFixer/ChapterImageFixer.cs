using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Dto;

namespace EmbyChapterImageFixer
{
    /// <summary>
    /// 章节图补全 / 重生成。
    ///
    /// 直接调用 ILibraryManager.RefreshThumbnailImages —— 这是 Emby 内部
    /// 元数据服务生成章节图时用的同一个公开接口，比走 RefreshMetadata 更可靠：
    /// RefreshMetadata 只在"保存元数据"阶段才会触发抽帧，
    /// 且受多种前置条件影响，容易出现"调用成功但没做任何事"。
    ///
    /// 该接口参数明确：
    ///  - extractImages        ：是否抽取
    ///  - replaceExistingImages：是否覆盖已有图（重生模式用 true）
    ///  - saveChapters         ：是否把结果写回数据库
    /// </summary>
    public class ChapterImageFixer
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly IMediaSourceManager _mediaSourceManager;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger _logger;

        public ChapterImageFixer(
            ILibraryManager libraryManager,
            IItemRepository itemRepository,
            IMediaSourceManager mediaSourceManager,
            IFileSystem fileSystem,
            ILogger logger)
        {
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _mediaSourceManager = mediaSourceManager;
            _fileSystem = fileSystem;
            _logger = logger;
        }

        /// <summary>
        /// 判断该条目是否需要处理。
        ///
        /// forceRegenerate = false：存在"没有图"或"图片文件已丢失"的章节才处理
        /// forceRegenerate = true ：只要有章节就处理
        /// </summary>
        public bool NeedsFix(Video video, bool forceRegenerate, out List<ChapterInfo> chapters, out int missing)
        {
            chapters = null;
            missing = 0;

            if (video == null) return false;
            if (!video.SupportsThumbnails) return false;

            var list = _itemRepository.GetChapters(video);
            if (list == null || list.Count == 0) return false;

            chapters = list;

            if (forceRegenerate) { missing = list.Count; return true; }

            missing = list.Count(ChapterNeedsFix);
            return missing > 0;
        }

        /// <summary>
        /// 该章节是否需要补图。
        ///
        /// 判定依据来自 Emby 的 ImageService.GetChapterImageResult：
        ///     if (chapter == null || string.IsNullOrEmpty(chapter.ImagePath)) 抛错
        /// 也就是说，只要章节的 ImagePath 为空，界面就一定显示不出图，
        /// 无论它是普通章节还是片头/片尾标记点。
        ///
        /// 因此片头、片尾、演职员表这些 MarkerType 也都要补 —— 这正是用户要的。
        /// 补法是把同一条目里已有的 bif 引用复制过来（见 FixChaptersAsync）。
        /// </summary>
        public bool ChapterNeedsFix(ChapterInfo c)
        {
            if (c == null) return false;

            // 没有图记录（ImagePath 为空在 Emby 看来就是没图）
            if (string.IsNullOrEmpty(c.ImageTag) && string.IsNullOrEmpty(c.ImagePath)) return true;

            // 有路径就校验文件是否存在（bif 可能被删掉）
            if (!string.IsNullOrEmpty(c.ImagePath))
            {
                try
                {
                    return !_fileSystem.FileExists(c.ImagePath);
                }
                catch
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 给缺少图片引用的章节补上同一条目已有的 bif 引用。
        ///
        /// Emby 的章节图机制：所有章节共享一张 .bif 缩略图集，
        /// 渲染时按各章节的 StartPositionTicks 去 bif 里取对应时间点的帧。
        /// 所以只要把已有章节的 ImagePath / ImageTag 复制给缺图的章节
        /// （包括片头/片尾标记点），它们就能显示对应的画面。
        ///
        /// 本方法不抽帧、不改动媒体库设置，只更新章节记录。
        /// 返回被修复的章节数。
        /// </summary>
        public int FixChapterReferences(Video video, List<ChapterInfo> chapters, bool forceRegenerate, Action<string> trace)
        {
            if (chapters == null || chapters.Count == 0) return 0;

            // 模板来源一：同一条目里已有的、文件真实存在的 bif 引用。
            string bifPath = null;
            string bifTag = null;
            DateTimeOffset dateModified = default(DateTimeOffset);

            foreach (var c in chapters)
            {
                if (string.IsNullOrEmpty(c.ImagePath)) continue;
                if (!FileExistsSafe(c.ImagePath)) continue;
                bifPath = c.ImagePath;
                bifTag = c.ImageTag;
                dateModified = c.ImageDateModified;
                if (c.MarkerType == MarkerType.Chapter) break;
            }

            // 模板来源二：扫描视频所在目录里的 bif 文件。
            // 对齐 Emby 自己的 GetBifsFromFolder 规则：
            // 目录内、扩展名 .bif、文件名以视频文件名（不含扩展名）开头。
            // 这样即使数据库里所有章节的 ImagePath 都是空的，
            // 只要 bif 就放在视频旁边，也能补上引用。
            if (bifPath == null)
            {
                bifPath = FindLocalBif(video);
                if (bifPath != null)
                {
                    dateModified = _fileSystem.GetLastWriteTimeUtc(bifPath);
                    trace?.Invoke($"从视频目录找到 bif：{bifPath}");
                }
            }

            if (bifPath == null)
            {
                // 目录里也没有 bif：这个条目确实没图，交给用户自行补，或走抽帧兜底。
                trace?.Invoke("视频目录里未找到 bif，跳过");
                return 0;
            }

            int fixedCount = 0;
            foreach (var c in chapters)
            {
                if (c == null) continue;

                var needs = forceRegenerate || ChapterNeedsFix(c);
                if (!needs) continue;

                c.ImagePath = bifPath;
                if (!string.IsNullOrEmpty(bifTag)) c.ImageTag = bifTag;
                if (dateModified != default(DateTimeOffset)) c.ImageDateModified = dateModified;
                fixedCount++;
            }

            if (fixedCount > 0)
            {
                // 写回数据库：Emby 的 GetChapterImageResult 会读到这里的新路径
                _itemRepository.SaveChapters(video.InternalId, true, chapters);
                trace?.Invoke($"已给 {fixedCount} 个章节补上 bif 引用：{bifPath}");
            }

            return fixedCount;
        }

        /// <summary>
        /// 在视频所在目录里找 bif。规则与 Emby 的 GetBifsFromFolder 一致：
        /// 目录内、扩展名 .bif、文件名以视频文件名（不含扩展名）开头。
        /// 有多个时按文件名排序取第一个。
        /// </summary>
        private string FindLocalBif(Video video)
        {
            try
            {
                var videoPath = video.Path;
                if (string.IsNullOrWhiteSpace(videoPath)) return null;

                var folder = _fileSystem.GetDirectoryName(videoPath);
                if (string.IsNullOrWhiteSpace(folder)) return null;

                var prefix = _fileSystem.GetFileNameWithoutExtension(videoPath);
                if (string.IsNullOrEmpty(prefix)) return null;

                var files = _fileSystem.GetFiles(folder, new[] { ".bif" }, false, false);
                if (files == null) return null;

                string best = null;
                foreach (var f in files)
                {
                    if (f == null || f.IsDirectory) continue;
                    var name = _fileSystem.GetFileNameWithoutExtension(f.FullName);
                    if (string.IsNullOrEmpty(name)) continue;
                    if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (best == null || string.CompareOrdinal(f.FullName, best) < 0) best = f.FullName;
                }

                return best;
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("[ChapterImageFixer] 扫描 bif 失败：" + video.Name, ex);
                return null;
            }
        }

        private bool FileExistsSafe(string path)
        {
            try { return _fileSystem.FileExists(path); }
            catch { return false; }
        }

        /// <summary>
        /// 处理单个条目。返回是否产生了变化。
        /// </summary>
        public async Task<bool> ProcessAsync(Video video, bool forceRegenerate, Action<string> trace, CancellationToken ct)
        {
            if (video == null) return false;

            List<ChapterInfo> chapters;
            int missing;
            if (!NeedsFix(video, forceRegenerate, out chapters, out missing))
            {
                return false;
            }

            trace?.Invoke($"{video.Name}：共 {chapters.Count} 个章节，缺图/失效 {missing} 个" +
                          (forceRegenerate ? "（重生模式）" : string.Empty));

            try
            {
                // 首选：把同一条目已有的 bif 引用复制给缺图章节。
                // 这不抽帧、不改动媒体库设置，且能让片头/片尾等标记点
                // 按各自时间点从 bif 取到画面 —— 正是用户要的效果。
                var fixedCount = FixChapterReferences(video, chapters, forceRegenerate, trace);
                if (fixedCount > 0)
                {
                    // 复核：从数据库重新读一遍，确认引用已写入
                    var after = _itemRepository.GetChapters(video) ?? new List<ChapterInfo>();
                    var stillMissing = after.Count(ChapterNeedsFix);
                    trace?.Invoke($"{video.Name}：剩余缺图 {stillMissing} 个");
                    return stillMissing < missing;
                }

                // 兜底：该条目连一张可用的 bif 都没有。
                // 默认不抽帧（AllowExtractFallback=false），交给用户自行补 bif。
                var cfg = Plugin.Instance?.Options;
                if (cfg != null && !cfg.AllowExtractFallback)
                {
                    trace?.Invoke($"{video.Name}：无 bif，按设置跳过抽帧");
                    return false;
                }

                return await ExtractImagesAsync(video, chapters, forceRegenerate, trace, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.ErrorException("[ChapterImageFixer] 处理失败：" + video.Name, ex);
                return false;
            }
        }

        /// <summary>
        /// 兜底路径：确实没有 bif 时，调用 Emby 的抽帧接口生成。
        /// 只在 FixChapterReferences 返回 0 时才会走到这里。
        /// </summary>
        private async Task<bool> ExtractImagesAsync(Video video, List<ChapterInfo> chapters, bool forceRegenerate, Action<string> trace, CancellationToken ct)
        {
            // 抽帧必须有实体视频文件。strm 只是链接，无法抽帧。
            // 注意：这个判断只放在抽帧兜底里，不影响上面的 bif 引用复制。
            if (string.IsNullOrWhiteSpace(video.Path) ||
                video.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
            {
                trace?.Invoke($"{video.Name}：无实体视频文件，跳过抽帧");
                return false;
            }

            // 取媒体源（要含章节信息）
            var sources = _mediaSourceManager.GetStaticMediaSources(
                video, enablePathSubstitution: false, fillChapters: true, deviceProfile: null, user: null);

            MediaSourceInfo mediaSource = null;
            if (sources != null)
            {
                foreach (var s in sources) { mediaSource = s; break; }
            }
            if (mediaSource == null)
            {
                trace?.Invoke($"{video.Name}：没有可用的媒体源，跳过");
                return false;
            }

            var libraryOptions = BuildLibraryOptions();
            var directoryService = new DirectoryService(_logger, _fileSystem);
            var refreshOptions = new MetadataRefreshOptions(directoryService)
            {
                EnableThumbnailImageExtraction = true,
                ReplaceThumbnailImages = forceRegenerate,
                MetadataRefreshMode = MetadataRefreshMode.ValidationOnly,
                ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
                IsAutomated = true
            };

            var ok = await _libraryManager.RefreshThumbnailImages(
                video, mediaSource, null, libraryOptions, refreshOptions, chapters,
                extractImages: true,
                replaceExistingImages: forceRegenerate,
                saveChapters: true,
                cancellationToken: ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
            trace?.Invoke($"{video.Name}：抽帧接口返回 {ok}");
            return ok;
        }

        /// <summary>
        /// 构造一次性的库选项，只用于本次抽帧。
        ///
        /// ExtractChapterImagesDuringLibraryScan 是 Emby 判断"是否抽章节图"的依据之一，
        /// 用户库里通常为 false，这里临时置 true，不改动用户配置。
        ///
        /// ThumbnailImagesIntervalSeconds = -1：只处理章节图，不生成额外缩略图集。
        /// </summary>
        private static LibraryOptions BuildLibraryOptions()
        {
            return new LibraryOptions
            {
                ExtractChapterImagesDuringLibraryScan = true,
                EnableChapterImageExtraction = true,
                ThumbnailImagesIntervalSeconds = -1,
                SaveLocalThumbnailSets = true
            };
        }
    }
}
