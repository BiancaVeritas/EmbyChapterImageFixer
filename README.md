# EmbyChapterImageFixer

为 Emby 视频**补齐章节图引用**，包括**片头、片尾等标记点**。

- 目标服务端：Emby Server **4.10.x**
- 产物：单个 `EmbyChapterImageFixer.dll`，不携带 SDK 依赖

---

## 它解决什么问题

Emby 的章节图机制是：视频旁边放一张 **`.bif` 缩略图集**（如 `片名-320-10.bif`），
渲染时按各章节的 `StartPositionTicks` 去 bif 里取对应时间点的帧。

但有些条目里，**普通章节引用了 bif，片头/片尾等标记点却是空的**：

```
Chapter 01  ImageTag='0da73e...'  Path='...-320-10.bif'   ← 有图
片头        ImageTag=''           Path=''                  ← 缺图
片尾        ImageTag=''           Path=''                  ← 缺图
```

反编译 `Emby.Api.Images.ImageService.GetChapterImageResult` 后可以确认：

```csharp
ChapterInfo chapter = _itemRepo.GetChapter(itemId, request.Index, ct);
if (chapter == null || string.IsNullOrEmpty(chapter.ImagePath))
    throw new ResourceNotFoundException(...);   // ← 片头片尾就卡在这里
if (Path.GetExtension(chapter.ImagePath) == ".bif")
    return GetThumbnailImageResult(request, chapter.StartPositionTicks, ...);
```

**只要章节的 `ImagePath` 为空，界面就一定显示不出图**；非空且指向 bif，
就按自己的时间点从 bif 取帧。

所以本插件做的事只有一件：

> **给缺图的章节补上 bif 引用（含片头、片尾等标记点）。**

bif 的来源有两个，按优先级：
1. 同一条目里其它章节已记录的 bif 路径
2. **扫描视频所在目录**，按 Emby 自己的规则找 bif
   （目录内、扩展名 `.bif`、文件名以视频文件名开头）

第 2 条很关键：即使数据库里所有章节的 `ImagePath` 都是空的，
只要 bif 就放在视频旁边，也能补上引用。

不抽帧、不生成新文件、不改动媒体库设置。目录里没有 bif 的条目默认跳过。

---

## 安装

1. 下载 `EmbyChapterImageFixer.dll`
2. 复制到 Emby 插件目录：
   - Windows：`%AppData%\Emby-Server\programdata\plugins\`
   - Linux：`/var/lib/emby/plugins/`
   - Docker：容器内 `/config/plugins/`
3. 重启 Emby Server
4. **仪表板 → 插件 → EmbyChapterImageFixer → 设置**

配置保存在 `plugins/configurations/EmbyChapterImageFixer.xml`，由 Emby 自动管理。

---

## 配置

| 选项 | 默认 | 说明 |
| --- | --- | --- |
| 启用插件 | 开 | 关闭后定时任务与手动运行都不执行 |
| 重生成模式 | 关 | 打开后覆盖已有章节图引用 |
| 仅处理视频 | 开 | 只处理电影/剧集/家庭视频 |
| 无 bif 时允许抽帧 | **关** | 条目完全没有 bif 时，默认跳过；打开才会尝试 ffmpeg 抽帧 |
| 调试日志 | 关 | 把每个条目的处理结果写入 Emby 日志 |
| 每次最多处理 | 0（不限） | 分批跑，避免一次占用太久 |

手动运行：设置页「立即处理」，或在「计划任务 → Library → EmbyChapterImageFixer」里运行。
运行周期（触发器）直接在 Emby 计划任务界面配置，插件设置页不提供间隔选项。

---

## 行为边界

| 情况 | 处理 |
| --- | --- |
| 条目有 bif，部分章节（含片头片尾）缺引用 | ✅ 复制 bif 引用，立即出图 |
| 数据库里全部章节都无引用，但**视频目录里有 bif** | ✅ 扫描目录找到并写入引用 |
| 条目所有章节都已有有效引用 | 跳过，不做任何改动 |
| 视频目录里也没有 bif | 默认跳过（由用户自行补 bif） |
| `.strm` 链接文件 | **照常处理**——补的是章节记录里的 bif 引用，与视频文件格式无关 |

注：只有在打开「无 bif 时允许抽帧」时，才会因为 `.strm` 无实体视频而跳过抽帧兜底。

**幂等**：修好后再跑不会重复改动。

---

## 构建

需要 .NET SDK 8：

```bash
dotnet build EmbyChapterImageFixer/EmbyChapterImageFixer.csproj -c Release
```

产物：`EmbyChapterImageFixer/bin/Release/EmbyChapterImageFixer.dll`

项目只引用 NuGet 上的官方 SDK（`MediaBrowser.Server.Core` /
`MediaBrowser.Common` 4.10.0.24-beta2），不依赖服务端私有程序集，
因此可在任意机器上构建。

---

## 项目结构

```
EmbyChapterImageFixer/
├─ EmbyChapterImageFixer/
│  ├─ Plugin.cs                    插件入口、页面注册
│  ├─ Configuration.cs             配置模型、读写与诊断 API
│  ├─ ChapterImageFixer.cs         核心：bif 引用复制 + 抽帧兜底
│  ├─ ChapterImageFixerService.cs  扫描与批处理调度
│  ├─ Tasks/ChapterImageFixerTask.cs   计划任务（Library 类目）
│  └─ Web/                         配置页 HTML 与脚本（嵌入 DLL）
└─ README.md
```

---

## 实现要点（供二次开发）

- **配置页 HTML 里不能有 `<script>`**
  `WebAppService.Get` 会把配置页交给 `PackageCreator.ModifyHtml`，
  含脚本标签会走特殊分支并抛异常，请求返回 500。HTML 与脚本拆成两个 `PluginPageInfo`。
- **根元素需含 `class="view"` 且带 `data-title`**
  前者供 `viewmanager` 定位根视图，后者决定导航栏标题。
- **插件 API 需要认证**
  `ApiClient.ajax` 会自动附加 `X-Emby-Token`；仅带 Cookie 会 401。
- **构造函数里不能访问 `Configuration`**
  `BasePlugin<T>.ConfigurationFilePath` 依赖 `AssemblyFilePath`，
  后者要等 Emby 调用 `SetAttributes()` 后才赋值。

## 许可证

[MIT](LICENSE)
