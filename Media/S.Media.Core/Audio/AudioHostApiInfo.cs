namespace S.Media.Core.Audio;

public readonly record struct AudioHostApiInfo(
    string Id,
    string Name,
    bool IsDefault,
    int DeviceCount);

