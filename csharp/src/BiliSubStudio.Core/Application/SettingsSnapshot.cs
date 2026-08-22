using BiliSubStudio.Core.Configuration;

namespace BiliSubStudio.Core.Application;

public sealed record SettingsSnapshot(
    string Root,
    string Drive,
    AppConfig Config,
    StorageUsage Storage,
    string? ConfigWarning);
