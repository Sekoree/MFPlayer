namespace S.Media.Core.Video;

public enum VideoOutputBackpressureMode
{
    DropNewest = 0,
    DropOldest = 1,
    Wait = 2,
}

