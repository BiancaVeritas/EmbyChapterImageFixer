using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;

namespace EmbyChapterImageFixer
{
    /// <summary>
    /// 计划任务：定期为缺失章节图的视频补图。
    /// 归入 Emby 内置的 Library 分类。
    /// </summary>
    public class ChapterImageFixerTask : IScheduledTask
    {
        private readonly ChapterImageFixerService _service;

        public ChapterImageFixerTask(ChapterImageFixerService service)
        {
            _service = service;
        }

        public string Name => "EmbyChapterImageFixer";

        /// <summary>任务唯一键，不要修改。</summary>
        public string Key => "ChapterImageFixerScan";

        public string Description =>
            "补全或重生成章节图。可只补缺失的章节图，也可覆盖重生成显示不正常的章节图。";

        public string Category => "Library";

        /// <summary>
        /// 不预设自动触发器。
        /// 运行周期由用户在「仪表板 → 计划任务」里按需配置，
        /// 不再由插件设置页提供间隔选项。
        /// </summary>
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield break;
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var result = await _service.RunAsync(cancellationToken).ConfigureAwait(false);

            progress?.Report(100);

            if (!result.Success)
            {
                throw new InvalidOperationException(result.Message);
            }
        }
    }
}
