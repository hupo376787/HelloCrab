from pathlib import Path


def read(path: str) -> str:
    return Path(path).read_text(encoding="utf-8")


def write(path: str, text: str) -> None:
    Path(path).write_text(text, encoding="utf-8", newline="\n")


def replace_once(text: str, old: str, new: str, label: str) -> str:
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected exactly one match, found {count}")
    return text.replace(old, new, 1)


def replace_n(text: str, old: str, new: str, expected: int, label: str) -> str:
    count = text.count(old)
    if count != expected:
        raise RuntimeError(f"{label}: expected {expected} matches, found {count}")
    return text.replace(old, new)


# AppSettings: the two new main-media switches default to enabled.
path = "src/HelloCrab.Core/Services/Settings/AppSettings.cs"
text = read(path)
text = replace_once(text, "public int Version { get; set; } = 13;", "public int Version { get; set; } = 14;", "settings version")
text = replace_once(
    text,
    "    public string DownloadRoot { get; set; } = string.Empty;\n    public bool IncludeWorkId { get; set; } = false;",
    "    public string DownloadRoot { get; set; } = string.Empty;\n    public bool DownloadVideo { get; set; } = true;\n    public bool DownloadImage { get; set; } = true;\n    public bool IncludeWorkId { get; set; } = false;",
    "settings media flags",
)
write(path, text)

# Migrate existing v13 settings so both newly introduced switches start checked.
path = "src/HelloCrab.Core/Services/Settings/SettingsService.cs"
text = read(path)
text = replace_once(
    text,
    "v12 增加人像检测置信度；v13 增加 Live 图下载开关。",
    "v12 增加人像检测置信度；v13 增加 Live 图下载开关；v14 增加视频/图片独立下载开关。",
    "settings migration comment",
)
text = replace_once(
    text,
    "            if (settings.Version < 13)\n                settings.Version = 13;",
    "            if (settings.Version < 14)\n            {\n                // v14 之前视频和普通图片始终都会下载，因此升级时保持原有行为。\n                settings.DownloadVideo = true;\n                settings.DownloadImage = true;\n                settings.Version = 14;\n            }",
    "settings v14 migration",
)
write(path, text)

# Download options: append switches to preserve existing positional constructor compatibility.
path = "src/HelloCrab.Core/Models/MediaModels.cs"
text = read(path)
text = replace_once(
    text,
    "    decimal DownloadSpeedLimitMBps = 0,\n    double PersonDetectionConfidence = 0.60);",
    "    decimal DownloadSpeedLimitMBps = 0,\n    double PersonDetectionConfidence = 0.60,\n    bool DownloadVideo = true,\n    bool DownloadImage = true);",
    "crawler options",
)
write(path, text)

# Actual download path: filter main media by the user's switches. Covers/music/live photo remain independent.
path = "src/HelloCrab.Core/Services/Downloading/MediaDownloadService.cs"
text = read(path)
text = replace_once(
    text,
    "        var primaryAssets = work.Assets\n            .Where(x => x.Type is MediaAssetType.Video or MediaAssetType.Image)\n            .OrderBy(x => x.Index)\n            .ToArray();",
    "        var availablePrimaryAssets = work.Assets\n            .Where(x => x.Type is MediaAssetType.Video or MediaAssetType.Image)\n            .OrderBy(x => x.Index)\n            .ToArray();\n        var primaryAssets = availablePrimaryAssets\n            .Where(x => x.Type switch\n            {\n                MediaAssetType.Video => options.DownloadVideo,\n                MediaAssetType.Image => options.DownloadImage,\n                _ => false\n            })\n            .ToArray();",
    "main media filtering",
)
text = replace_once(
    text,
    "        if (primaryAssets.Length == 0)\n            throw new InvalidOperationException(RuntimeLocalization.Get(\"Error.Download.NoMedia\", \"作品中没有可下载的视频或图片资源。\"));",
    "        if (availablePrimaryAssets.Length == 0)\n            throw new InvalidOperationException(RuntimeLocalization.Get(\"Error.Download.NoMedia\", \"作品中没有可下载的视频或图片资源。\"));",
    "source media availability",
)
text = replace_once(
    text,
    "        var appendSequence = primaryAssets.Length > 1\n                             || primaryAssets.Any(x => x.Type == MediaAssetType.Image);",
    "        var appendSequence = availablePrimaryAssets.Length > 1\n                             || availablePrimaryAssets.Any(x => x.Type == MediaAssetType.Image);",
    "sequence policy",
)
text = replace_once(
    text,
    "                    primaryAssets,\n                    asset => asset.Index == livePhoto.Index);",
    "                    availablePrimaryAssets,\n                    asset => asset.Index == livePhoto.Index);",
    "live photo sequence mapping",
)
write(path, text)

# Completion index must distinguish media selections. Legacy keys default to video/image=true,
# matching the behavior of all versions before v14.
path = "src/HelloCrab.Core/Services/Downloading/JsonDownloadIndex.cs"
text = read(path)
text = replace_once(
    text,
    "            options.IncludeWorkId,\n            options.DownloadCover,",
    "            options.IncludeWorkId,\n            options.DownloadVideo,\n            options.DownloadImage,\n            options.DownloadCover,",
    "index build key args",
)
text = replace_once(
    text,
    "            [\"workId\"] = false,\n            [\"cover\"] = false,",
    "            [\"workId\"] = false,\n            // 旧版本没有这两个字段，当时视频和普通图片都是默认下载。\n            [\"video\"] = true,\n            [\"image\"] = true,\n            [\"cover\"] = false,",
    "index legacy defaults",
)
text = replace_once(
    text,
    "            flags[\"workId\"],\n            flags[\"cover\"],",
    "            flags[\"workId\"],\n            flags[\"video\"],\n            flags[\"image\"],\n            flags[\"cover\"],",
    "index canonical parse args",
)
text = replace_once(
    text,
    "        bool includeWorkId,\n        bool downloadCover,",
    "        bool includeWorkId,\n        bool downloadVideo,\n        bool downloadImage,\n        bool downloadCover,",
    "index canonical signature",
)
text = replace_once(
    text,
    "           $\"workId={(includeWorkId ? 1 : 0)}:\" +\n           $\"cover={(downloadCover ? 1 : 0)}:\" +",
    "           $\"workId={(includeWorkId ? 1 : 0)}:\" +\n           $\"video={(downloadVideo ? 1 : 0)}:\" +\n           $\"image={(downloadImage ? 1 : 0)}:\" +\n           $\"cover={(downloadCover ? 1 : 0)}:\" +",
    "index canonical output",
)
write(path, text)

# Desktop ViewModel: expose, persist, pass to downloader, and synchronize through remote settings.
path = "src/HelloCrab.Core/ViewModels/MainWindowViewModel.cs"
text = read(path)
text = replace_once(
    text,
    "    private bool _includeWorkId;\n    private bool _downloadCover;",
    "    private bool _downloadVideo = true;\n    private bool _downloadImage = true;\n    private bool _includeWorkId;\n    private bool _downloadCover;",
    "desktop vm fields",
)
text = replace_once(
    text,
    "        _downloadRoot = ResolveDownloadRoot(settings.DownloadRoot);\n        _includeWorkId = settings.IncludeWorkId;",
    "        _downloadRoot = ResolveDownloadRoot(settings.DownloadRoot);\n        _downloadVideo = settings.DownloadVideo;\n        _downloadImage = settings.DownloadImage;\n        _includeWorkId = settings.IncludeWorkId;",
    "desktop vm settings load",
)
text = replace_once(
    text,
    "    public bool IncludeWorkId\n    {",
    "    public bool DownloadVideo\n    {\n        get => _downloadVideo;\n        set\n        {\n            if (SetProperty(ref _downloadVideo, value))\n                QueueSettingsSave();\n        }\n    }\n\n    public bool DownloadImage\n    {\n        get => _downloadImage;\n        set\n        {\n            if (SetProperty(ref _downloadImage, value))\n                QueueSettingsSave();\n        }\n    }\n\n    public bool IncludeWorkId\n    {",
    "desktop vm media properties",
)
text = replace_once(
    text,
    "                DownloadSpeedLimitMBps,\n                PersonDetectionConfidence);",
    "                DownloadSpeedLimitMBps,\n                PersonDetectionConfidence,\n                DownloadVideo,\n                DownloadImage);",
    "desktop vm crawler options",
)
text = replace_n(
    text,
    "                DownloadRoot = DownloadRoot,\n                IncludeWorkId = IncludeWorkId,",
    "                DownloadRoot = DownloadRoot,\n                DownloadVideo = DownloadVideo,\n                DownloadImage = DownloadImage,\n                IncludeWorkId = IncludeWorkId,",
    1,
    "desktop vm remote snapshot",
)
text = replace_once(
    text,
    "            DownloadRoot = settings.DownloadRoot;\n            IncludeWorkId = settings.IncludeWorkId;",
    "            DownloadRoot = settings.DownloadRoot;\n            DownloadVideo = settings.DownloadVideo;\n            DownloadImage = settings.DownloadImage;\n            IncludeWorkId = settings.IncludeWorkId;",
    "desktop vm apply remote settings",
)
text = replace_once(
    text,
    "            DownloadRoot = DownloadRoot,\n            IncludeWorkId = IncludeWorkId,",
    "            DownloadRoot = DownloadRoot,\n            DownloadVideo = DownloadVideo,\n            DownloadImage = DownloadImage,\n            IncludeWorkId = IncludeWorkId,",
    "desktop vm settings snapshot",
)
write(path, text)

# Desktop UI: exact order requested by the reference image.
path = "src/HelloCrab.Core/Views/MainWindow.axaml"
text = read(path)
old = '''                <Grid ColumnDefinitions="*,*" ColumnSpacing="14">
                  <CheckBox Content="{DynamicResource Lang.Download.IncludeWorkId}"
                            IsChecked="{Binding IncludeWorkId}"
                            VerticalAlignment="Center" />
                  <CheckBox Grid.Column="1"
                            Content="{DynamicResource Lang.Download.LivePhoto}"
                            IsChecked="{Binding DownloadLivePhoto}"
                            VerticalAlignment="Center" />
                </Grid>
                <Grid ColumnDefinitions="*,*" ColumnSpacing="14">
                  <CheckBox Content="{DynamicResource Lang.Download.Cover}"
                            IsChecked="{Binding DownloadCover}"
                            VerticalAlignment="Center" />
                  <CheckBox Grid.Column="1"
                            Content="{DynamicResource Lang.Download.Music}"
                            IsChecked="{Binding DownloadMusic}"
                            VerticalAlignment="Center" />
                </Grid>'''
new = '''                <Grid ColumnDefinitions="*,*" ColumnSpacing="14">
                  <CheckBox Content="{DynamicResource Lang.Download.Video}"
                            IsChecked="{Binding DownloadVideo}"
                            VerticalAlignment="Center" />
                  <CheckBox Grid.Column="1"
                            Content="{DynamicResource Lang.Download.Image}"
                            IsChecked="{Binding DownloadImage}"
                            VerticalAlignment="Center" />
                </Grid>
                <Grid ColumnDefinitions="*,*" ColumnSpacing="14">
                  <CheckBox Content="{DynamicResource Lang.Download.LivePhoto}"
                            IsChecked="{Binding DownloadLivePhoto}"
                            VerticalAlignment="Center" />
                  <CheckBox Grid.Column="1"
                            Content="{DynamicResource Lang.Download.IncludeWorkId}"
                            IsChecked="{Binding IncludeWorkId}"
                            VerticalAlignment="Center" />
                </Grid>
                <Grid ColumnDefinitions="*,*" ColumnSpacing="14">
                  <CheckBox Content="{DynamicResource Lang.Download.Cover}"
                            IsChecked="{Binding DownloadCover}"
                            VerticalAlignment="Center" />
                  <CheckBox Grid.Column="1"
                            Content="{DynamicResource Lang.Download.Music}"
                            IsChecked="{Binding DownloadMusic}"
                            VerticalAlignment="Center" />
                </Grid>'''
text = replace_once(text, old, new, "desktop settings checkbox layout")
write(path, text)

# Remote contract and VM preserve the same settings when controlled from Web/Android/iOS.
path = "src/HelloCrab.Core/Contracts/RemoteContracts.cs"
text = read(path)
text = replace_once(
    text,
    "    public string DownloadRoot { get; set; } = string.Empty;\n    public bool IncludeWorkId { get; set; } = false;",
    "    public string DownloadRoot { get; set; } = string.Empty;\n    public bool DownloadVideo { get; set; } = true;\n    public bool DownloadImage { get; set; } = true;\n    public bool IncludeWorkId { get; set; } = false;",
    "remote settings dto",
)
write(path, text)

path = "src/HelloCrab.Core/Remote/ViewModels/RemoteMainViewModel.cs"
text = read(path)
text = replace_once(
    text,
    "    private string _downloadRoot = string.Empty;\n    private bool _includeWorkId;",
    "    private string _downloadRoot = string.Empty;\n    private bool _downloadVideo = true;\n    private bool _downloadImage = true;\n    private bool _includeWorkId;",
    "remote vm fields",
)
text = replace_once(
    text,
    "    public bool IncludeWorkId\n    {",
    "    public bool DownloadVideo\n    {\n        get => _downloadVideo;\n        set { if (SetProperty(ref _downloadVideo, value)) MarkSettingsDirty(); }\n    }\n\n    public bool DownloadImage\n    {\n        get => _downloadImage;\n        set { if (SetProperty(ref _downloadImage, value)) MarkSettingsDirty(); }\n    }\n\n    public bool IncludeWorkId\n    {",
    "remote vm properties",
)
text = replace_once(
    text,
    "                DownloadRoot = DownloadRoot,\n                IncludeWorkId = IncludeWorkId,",
    "                DownloadRoot = DownloadRoot,\n                DownloadVideo = DownloadVideo,\n                DownloadImage = DownloadImage,\n                IncludeWorkId = IncludeWorkId,",
    "remote vm save dto",
)
text = replace_once(
    text,
    "                DownloadRoot = snapshot.Settings.DownloadRoot;\n                IncludeWorkId = snapshot.Settings.IncludeWorkId;",
    "                DownloadRoot = snapshot.Settings.DownloadRoot;\n                DownloadVideo = snapshot.Settings.DownloadVideo;\n                DownloadImage = snapshot.Settings.DownloadImage;\n                IncludeWorkId = snapshot.Settings.IncludeWorkId;",
    "remote vm snapshot load",
)
write(path, text)

# Remote UI uses the same logical ordering.
path = "src/HelloCrab.Core/Remote/Views/RemoteMainView.axaml"
text = read(path)
old = '''            <CheckBox Content="文件名中添加作品 ID" IsChecked="{Binding IncludeWorkId, Mode=TwoWay}" />
            <CheckBox Content="下载Live图" IsChecked="{Binding DownloadLivePhoto, Mode=TwoWay}" />
            <CheckBox Content="下载作品封面" IsChecked="{Binding DownloadCover, Mode=TwoWay}" />
            <CheckBox Content="下载背景音乐" IsChecked="{Binding DownloadMusic, Mode=TwoWay}" />'''
new = '''            <Grid ColumnDefinitions="*,*" ColumnSpacing="12">
              <CheckBox Content="下载视频" IsChecked="{Binding DownloadVideo, Mode=TwoWay}" />
              <CheckBox Grid.Column="1" Content="下载图片" IsChecked="{Binding DownloadImage, Mode=TwoWay}" />
            </Grid>
            <Grid ColumnDefinitions="*,*" ColumnSpacing="12">
              <CheckBox Content="下载Live图" IsChecked="{Binding DownloadLivePhoto, Mode=TwoWay}" />
              <CheckBox Grid.Column="1" Content="文件名中添加作品 ID" IsChecked="{Binding IncludeWorkId, Mode=TwoWay}" />
            </Grid>
            <Grid ColumnDefinitions="*,*" ColumnSpacing="12">
              <CheckBox Content="下载作品封面" IsChecked="{Binding DownloadCover, Mode=TwoWay}" />
              <CheckBox Grid.Column="1" Content="下载背景音乐" IsChecked="{Binding DownloadMusic, Mode=TwoWay}" />
            </Grid>'''
text = replace_once(text, old, new, "remote settings checkbox layout")
write(path, text)

# Add localized desktop labels without reformatting the language files.
language_values = {
    "src/HelloCrab.Core/Languages/zh-CN.json": ("下载视频", "下载图片"),
    "src/HelloCrab.Core/Languages/en-US.json": ("Download videos", "Download images"),
    "src/HelloCrab.Core/Languages/ja-JP.json": ("動画をダウンロード", "画像をダウンロード"),
}
for path, (video_label, image_label) in language_values.items():
    text = read(path)
    marker = '    "Download.IncludeWorkId":'
    insertion = (
        f'    "Download.Video": "{video_label}",\n'
        f'    "Download.Image": "{image_label}",\n'
        + marker
    )
    text = replace_once(text, marker, insertion, f"language labels {path}")
    write(path, text)

print("Media toggle patch applied successfully.")
